using System.Diagnostics;
using System.Globalization;

namespace Cluewren.Tools;

// The uncapped control for the CPU ceiling test: processors' worth of CPU time this process got per wall second.
static class Spin
{
    public static int Run(double seconds, int threads)
    {
        var used = Process.GetCurrentProcess().TotalProcessorTime;
        var clock = Stopwatch.StartNew();
        var spinners = Enumerable.Range(0, threads).Select(_ => new Thread(() =>
        {
            while (clock.Elapsed.TotalSeconds < seconds)
            {
            }
        })).ToList();
        foreach (var spinner in spinners)
            spinner.Start();
        foreach (var spinner in spinners)
            spinner.Join();
        var share = (Process.GetCurrentProcess().TotalProcessorTime - used).TotalSeconds / clock.Elapsed.TotalSeconds;
        Console.WriteLine(share.ToString("F3", CultureInfo.InvariantCulture));
        return 0;
    }
}
