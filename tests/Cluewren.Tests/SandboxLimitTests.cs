using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Cluewren.Sandbox;
using Xunit.Abstractions;

namespace Cluewren.Tests;

[Collection("sandbox")]
public sealed class SandboxLimitTests(ITestOutputHelper output) : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "playtest-limits-" + Guid.NewGuid().ToString("N"));
    readonly StillGame _game = new();

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    HarnessHost Host(SandboxMode mode, long memory = 2L << 30) =>
        new(new HostOptions(_game, Path.Combine(_root, "run")) { Mode = mode, MemoryLimitBytes = memory, Say = _ => { } });

    static double Share(string text) =>
        double.Parse(Regex.Match(text, @"SHARE (\d+\.\d+)").Groups[1].Value, CultureInfo.InvariantCulture);

    // Catches a job without the CPU ceiling, or a ceiling on the wrong basis (a share of one processor, not of the machine).
    [InconclusiveFact]
    public async Task A_harness_child_gets_about_two_processors_worth_of_cpu_however_many_threads_it_spins()
    {
        var threads = Environment.ProcessorCount;
        if (threads <= 2)
            throw new InconclusiveException($"{threads} processors: no ceiling is set at two or fewer");
        await using var host = Host(SandboxMode.Contained);
        var result = await host.ProbeAsync($$"""
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var used = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
            var spinners = new List<System.Threading.Thread>();
            for (var i = 0; i < {{threads}}; i++)
                spinners.Add(new System.Threading.Thread(() => { while (clock.Elapsed.TotalSeconds < 5) { } }));
            foreach (var spinner in spinners)
                spinner.Start();
            foreach (var spinner in spinners)
                spinner.Join();
            var share = (System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime - used).TotalSeconds / clock.Elapsed.TotalSeconds;
            Console.WriteLine("SHARE " + share.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
            """, seconds: 60);
        Assert.Equal("appcontainer", host.Sandbox);
        var capped = Share(result.Text);
        // After the capped child, not beside it: run together, the control takes processors the capped child would get.
        using var control = Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Cluewren.Tools.exe"))
        {
            ArgumentList = { "spin", "5", threads.ToString(CultureInfo.InvariantCulture) },
            UseShellExecute = false,
            RedirectStandardOutput = true,
        })!;
        var uncapped = double.Parse((await control.StandardOutput.ReadToEndAsync()).Trim(), CultureInfo.InvariantCulture);
        await control.WaitForExitAsync();
        output.WriteLine($"processors {threads}: capped child {capped:F3}, uncapped control {uncapped:F3} processors' worth");
        if (uncapped < 3)
            throw new InconclusiveException(
                $"the uncapped control got {uncapped:F2} processors' worth, not clearly above the ceiling of 2 (capped child {capped:F2})");
        Assert.InRange(capped, 1.2, 2.6);
    }

    // Catches a plain child started before its job exists: when the job cannot be made, that child was left running.
    [Fact]
    public async Task A_plain_child_whose_job_cannot_be_made_leaves_no_process()
    {
        var before = Children.Of(Environment.ProcessId);
        await using (var host = Host(SandboxMode.Process, memory: 0))
            await Assert.ThrowsAsync<Win32Exception>(() => host.ProbeAsync("1"));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        var left = Children.Of(Environment.ProcessId).Except(before).ToList();
        while (left.Count > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
            left = Children.Of(Environment.ProcessId).Except(before).ToList();
        }
        Assert.Empty(left);
    }

    // Catches the plain child losing the job's limits now that it is created natively: it must stay one process.
    [Fact]
    public async Task A_plain_child_is_in_its_job()
    {
        await using var host = Host(SandboxMode.Process);
        var result = await host.ProbeAsync("""
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(@"C:\Windows\System32\whoami.exe") { UseShellExecute = false }); Console.WriteLine("ALLOWED"); }
            catch (Exception e) { Console.WriteLine("REFUSED " + e.GetType().Name); }
            """, seconds: 30);
        Assert.Equal("process", host.Sandbox);
        Assert.Contains("REFUSED", result.Text);
    }
}
