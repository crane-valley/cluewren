using System.Globalization;
using System.Text;
using Cluewren.Bridge;
using Cluewren.Jev;
using Cluewren.Kit;
using Cluewren.Mcp;
using Cluewren.Sandbox;

const string Usage = """
    usage:
      cluewren connect claude|codex [--remove]   set up your own agent to use Cluewren
      cluewren connect codex --approve-tools     the same, and Codex calls Cluewren's tools without asking
      cluewren mcp                               the MCP server your agent starts
      cluewren run <run folder> [seconds] [--project <folder>]
                                                 play <run folder>/harness.cs once, without an agent, in the
                                                 editor of <folder> (default: this folder, inside or around it)
      cluewren --version                         this build's version
    """;

switch (args.FirstOrDefault())
{
    case "mcp":
        return await ServeMcp();
    case "connect" when args.Length >= 2 && args[1] is "claude" or "codex":
        var connect = new Connect(Console.WriteLine);
        var approveTools = args.Contains("--approve-tools");
        if (approveTools && (args[1] == "claude" || args.Contains("--remove")))
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }
        return args.Contains("--remove") ? connect.Remove(args[1]) : connect.Add(args[1], approveTools);
    case "describer":
        Console.WriteLine(Connect.DescriberInstructions());
        return 0;
    case "run" when args.Length >= 2:
        return await RunHarness();
    case "--version":
        Console.WriteLine($"cluewren {McpServer.Version}");
        return 0;
    case "--help" or "-h":
        Console.WriteLine(Usage);
        return 0;
    default:
        Console.Error.WriteLine(Usage);
        return 2;
}

static SandboxMode? SandboxSetting()
{
    var setting = Environment.GetEnvironmentVariable(SandboxStarter.Setting);
    var mode = SandboxStarter.ModeFrom(setting);
    if (mode is null)
        Console.Error.WriteLine($"{SandboxStarter.Setting}={setting} isn't a setting: leave it unset, or set it to auto or required.");
    return mode;
}

static async Task<int> ServeMcp()
{
    if (SandboxSetting() is not { } sandbox)
        return 2;
    var utf8 = new UTF8Encoding(false);
    var output = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true };
    Console.SetOut(Console.Error);
    await using var server = new McpServer(output) { Sandbox = sandbox };
    await server.ServeAsync(new StreamReader(Console.OpenStandardInput(), utf8)).ConfigureAwait(false);
    return 0;
}

async Task<int> RunHarness()
{
    if (SandboxSetting() is not { } sandbox)
        return 2;
    var positional = new List<string>();
    string? project = null;
    for (var i = 1; i < args.Length; i++)
    {
        if (args[i] != "--project")
            positional.Add(args[i]);
        else if (i + 1 < args.Length)
            project = args[++i];
        else
            project = "";
    }
    if (positional.Count == 0)
    {
        Console.Error.WriteLine(Usage);
        return 2;
    }
    if (project is not null && (!ProjectScope.IsLocalPath(project) || !Directory.Exists(project)))
    {
        Console.Error.WriteLine($"--project needs the game project's folder as a full local path: \"{project}\" isn't one.");
        return 2;
    }
    var scope = project is null ? ProjectScope.Related(Environment.CurrentDirectory) : ProjectScope.ExactlyAt(project);
    var seconds = positional.Count > 1 && double.TryParse(positional[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
        ? parsed : 30;
    using var cancel = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cancel.Cancel();
    };

    var secret = Handshake.NewSecret();
    await using var server = await BridgeServer.StartAsync(secret, scope);
    server.Refusing += refusal => Console.WriteLine($"Refused an editor: {refusal}");
    var announced = new Discovery(server.Url, Convert.ToBase64String(secret), Environment.ProcessId, DateTimeOffset.UtcNow,
        scope.Folders, scope.Match);
    var path = DiscoveryFile.Write(announced, DiscoveryFile.DefaultFolder);

    try
    {
        Console.WriteLine($"Waiting on {server.Url} for an editor whose project is {scope.Described}");
        var link = await server.NextLinkAsync(cancel.Token);
        return await Cluewren.RunCommand.PlayAsync(link, positional[0], seconds, sandbox, cancel.Token);
    }
    catch (OperationCanceledException)
    {
        return 130;
    }
    finally
    {
        DiscoveryFile.Withdraw(path, announced);
    }
}

namespace Cluewren
{
    public static class RunCommand
    {
        public static async Task<int> PlayAsync(BridgeLink link, string runFolder, double seconds, SandboxMode sandbox,
            CancellationToken ct)
        {
            try
            {
                var key = ApiKeys.Find(ApiKeys.TypeSafeKey);
                await using var jev = key is null ? null : new JevClient(new JevOptions(key));
                await using var host = new HarnessHost(new HostOptions(link, runFolder)
                {
                    Jev = jev, Say = Console.WriteLine, Mode = sandbox,
                });
                var (map, problem) = await host.StartMapAsync(ct).ConfigureAwait(false);
                if (problem is not null)
                {
                    Console.WriteLine(problem);
                    return 1;
                }
                try
                {
                    await PlaytestRun.StartGameAsync(link, map, ct).ConfigureAwait(false);
                }
                catch (Exception e) when (e is InvalidOperationException or GameException or GameDisconnectedException)
                {
                    Console.Error.WriteLine($"The run didn't start: {e.Message}");
                    return 1;
                }
                link.Send(json => json.WriteString("type", "run_started"));
                var result = await host.RunHarnessAsync(seconds, ct: ct).ConfigureAwait(false);
                Console.WriteLine($"sandbox: {result.Sandbox}");
                Console.WriteLine($"state: {result.State} reason: {result.Reason} decisions: {result.Decisions}");
                Console.WriteLine(result.Text);
                Console.WriteLine(result.Stats?.GetRawText());
                return result.Played ? 0 : 1;
            }
            catch (OperationCanceledException)
            {
                return 130;
            }
            catch (SandboxException e)
            {
                Console.Error.WriteLine($"The sandbox failed: {e.Message}");
                return 1;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"The run couldn't use its files: {e.Message}");
                return 1;
            }
        }
    }
}
