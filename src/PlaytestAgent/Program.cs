using System.Text;
using PlaytestAgent.Bridge;
using PlaytestAgent.Jev;
using PlaytestAgent.Mcp;
using PlaytestAgent.Sandbox;

switch (args.FirstOrDefault())
{
    case "mcp":
        return await ServeMcp();
    case "connect" when args.Length >= 2 && args[1] is "claude" or "codex":
        var connect = new Connect(Console.WriteLine);
        return args.Contains("--remove") ? connect.Remove(args[1]) : connect.Add(args[1]);
    case "describer":
        Console.WriteLine(Connect.DescriberInstructions());
        return 0;
    case "run" when args.Length >= 2:
        return await RunHarness();
    default:
        Console.Error.WriteLine("""
            usage:
              playtest-agent connect claude|codex [--remove]   set up your own agent to use Playtest Agent
              playtest-agent mcp                               the MCP server your agent starts
              playtest-agent run <run folder> [seconds]        play <run folder>/harness.cs once, without an agent
            """);
        return 2;
}

static async Task<int> ServeMcp()
{
    var utf8 = new UTF8Encoding(false);
    var output = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true };
    Console.SetOut(Console.Error);
    await using var server = new McpServer(output);
    await server.ServeAsync(new StreamReader(Console.OpenStandardInput(), utf8)).ConfigureAwait(false);
    return 0;
}

async Task<int> RunHarness()
{
    var seconds = args.Length > 2 && double.TryParse(args[2], out var parsed) ? parsed : 30;
    using var cancel = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cancel.Cancel();
    };

    var secret = Handshake.NewSecret();
    await using var server = await BridgeServer.StartAsync(secret);
    var path = DiscoveryFile.Write(new Discovery(server.Url, Convert.ToBase64String(secret), Environment.ProcessId,
        DateTimeOffset.UtcNow), DiscoveryFile.DefaultFolder);

    try
    {
        Console.WriteLine($"Waiting for the plugin on {server.Url}");
        var link = await server.NextLinkAsync(cancel.Token);
        link.Send(json => json.WriteString("type", "run_started"));
        var key = ApiKeys.Find(ApiKeys.TypeSafeKey);
        await using var jev = key is null ? null : new JevClient(new JevOptions(key));
        await using var host = new HarnessHost(new HostOptions(link, args[1]) { Jev = jev, Say = Console.WriteLine });
        var result = await host.RunHarnessAsync(seconds, ct: cancel.Token);
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
    finally
    {
        File.Delete(path);
    }
}
