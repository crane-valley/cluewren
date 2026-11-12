using Cluewren.Sandbox;

namespace Cluewren.Tools;

// A brain preparing the child's program folder, for the tests of that folder. The tests point USERPROFILE at a folder
// of their own; the verb refuses to touch anything if LocalApplicationData did not follow it.
static class Materialize
{
    public static async Task<int> Run(string[] args)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.Equals(Path.GetFullPath(local), Path.GetFullPath(args[1]), StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"LocalApplicationData is {local}, not {args[1]}");
            return 4;
        }
        try
        {
            Console.WriteLine(ChildRuntime.Materialize());
        }
        catch (SandboxException e)
        {
            Console.Error.WriteLine(e.Message);
            return 3;
        }
        if (args.Length < 4)
            return 0;
        while (!File.Exists(args[2]))
            await Task.Delay(50);
        await using var host = new HarnessHost(new HostOptions(new QuietLink(), args[3]) { Mode = SandboxMode.Contained, Say = _ => { } });
        Console.WriteLine((await host.ProbeAsync("40 + 2", seconds: 30)).Text.Trim());
        return 0;
    }
}
