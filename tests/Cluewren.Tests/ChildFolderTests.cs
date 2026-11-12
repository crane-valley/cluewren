using System.Diagnostics;
using System.Security.Cryptography;

namespace Cluewren.Tests;

// Each brain here is a Cluewren.Tools process whose USERPROFILE is a folder of the test's own, so the real
// %LOCALAPPDATA%\Cluewren-child is never touched.
public sealed class ChildFolderTests : IDisposable
{
    static readonly string[] Files =
        ["Cluewren.Child.dll", "Cluewren.Child.runtimeconfig.json", "Cluewren.Child.deps.json", "Cluewren.Kit.dll"];

    static readonly string Version = Convert.ToHexStringLower(SHA256.HashData(
        Files.SelectMany(name => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, name))).ToArray()))[..16];

    readonly string _profile = Path.Combine(Path.GetTempPath(), "playtest-profile-" + Guid.NewGuid().ToString("N"));
    readonly List<Process> _brains = [];

    public ChildFolderTests() => Directory.CreateDirectory(Local);

    string Local => Path.Combine(_profile, "AppData", "Local");

    string Root => Path.Combine(Local, "Cluewren-child");

    string Current => Path.Combine(Root, Version);

    public void Dispose()
    {
        foreach (var brain in _brains)
        {
            try
            {
                brain.Kill(entireProcessTree: true);
                brain.WaitForExit(5000);
            }
            catch (InvalidOperationException)
            {
            }
            brain.Dispose();
        }
        try
        {
            Directory.Delete(_profile, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    Process Brain(string? build = null, params string[] more)
    {
        var start = new ProcessStartInfo(Path.Combine(build ?? AppContext.BaseDirectory, "Cluewren.Tools.exe"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("materialize");
        start.ArgumentList.Add(Local);
        foreach (var argument in more)
            start.ArgumentList.Add(argument);
        start.Environment["USERPROFILE"] = _profile;
        start.Environment["LOCALAPPDATA"] = Local;
        var brain = Process.Start(start)!;
        _brains.Add(brain);
        return brain;
    }

    static async Task<(int Code, string Output, string Error)> Finish(Process brain)
    {
        var output = brain.StandardOutput.ReadToEndAsync();
        var error = brain.StandardError.ReadToEndAsync();
        await brain.WaitForExitAsync();
        if (brain.ExitCode == 4)
            throw new InconclusiveException("LocalApplicationData does not follow USERPROFILE here: " + await error);
        return (brain.ExitCode, (await output).Trim(), (await error).Trim());
    }

    static void Copy(string folder, IEnumerable<string> names)
    {
        Directory.CreateDirectory(folder);
        foreach (var name in names)
            File.Copy(Path.Combine(AppContext.BaseDirectory, name), Path.Combine(folder, name));
    }

    static void AssertComplete(string folder)
    {
        Assert.True(File.Exists(Path.Combine(folder, ".ready")), $"no .ready in {folder}");
        foreach (var name in Files)
            Assert.Equal(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, name)), File.ReadAllBytes(Path.Combine(folder, name)));
    }

    string[] Entries() => [.. new DirectoryInfo(Root).GetDirectories().Select(entry => entry.Name).Order(StringComparer.Ordinal)];

    static async Task AssertGets(Process brain, string folder)
    {
        var (code, output, error) = await Finish(brain);
        Assert.True(code == 0, $"exit {code}: {error}");
        Assert.Equal(folder, output);
    }

    // Catches brains copying into the same folder at once: the lock that guarded it held within one process only.
    [InconclusiveFact]
    public async Task Brains_building_the_same_missing_version_at_once_all_get_one_complete_folder()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Brain()).ToList().Select(Finish));
        Assert.All(results, result => Assert.Equal((0, Current), (result.Code, result.Output)));
        AssertComplete(Current);
        Assert.Equal([Version], Entries());
    }

    // Catches an incomplete folder replaced at once, under a 0.1.0 brain that is still copying into it.
    [InconclusiveFact]
    public async Task An_incomplete_folder_that_completes_within_the_wait_is_kept()
    {
        Copy(Current, Files.Take(3));
        File.WriteAllText(Path.Combine(Current, "kept"), "");
        var brain = Brain();
        await Task.Delay(3000);
        Copy(Current, Files.Skip(3));
        File.WriteAllText(Path.Combine(Current, ".ready"), "");
        await AssertGets(brain, Current);
        Assert.True(File.Exists(Path.Combine(Current, "kept")));
        AssertComplete(Current);
    }

    // Catches a folder with its marker but without all four files being used as it is: no child starts from it.
    [InconclusiveFact]
    public async Task A_folder_that_stays_incomplete_is_replaced_after_the_wait()
    {
        Copy(Current, Files.Take(1));
        File.WriteAllText(Path.Combine(Current, ".ready"), "");
        File.WriteAllText(Path.Combine(Current, "kept"), "");
        var clock = Stopwatch.StartNew();
        var (code, output, error) = await Finish(Brain());
        Assert.True(code == 0, error);
        Assert.Equal(Current, output);
        Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(10), $"replaced after {clock.Elapsed}");
        AssertComplete(Current);
        Assert.False(File.Exists(Path.Combine(Current, "kept")));
        Assert.Equal([Version], Entries());
    }

    // Catches a start that copies over, or deletes around, an incomplete folder that something still has open.
    [InconclusiveFact]
    public async Task An_incomplete_folder_that_something_holds_fails_the_start_and_names_the_folder()
    {
        Copy(Current, Files.Take(1));
        var held = Path.Combine(Current, Files[0]);
        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var (code, _, error) = await Finish(Brain());
            Assert.Equal(3, code);
            Assert.Contains(Current, error);
        }
        Assert.True(File.Exists(held));
    }

    // Catches old versions piling up: before 0.1.1 nothing ever removed them.
    [InconclusiveFact]
    public async Task Other_versions_and_leftovers_are_removed_and_other_folders_kept()
    {
        Copy(Path.Combine(Root, "0123456789abcdef"), Files);
        File.WriteAllText(Path.Combine(Root, "0123456789abcdef", ".ready"), "");
        Copy(Path.Combine(Root, Version + ".new-" + Guid.NewGuid().ToString("N")), Files.Take(2));
        Copy(Path.Combine(Root, "0123456789abcdef.old-" + Guid.NewGuid().ToString("N")), Files.Take(1));
        Directory.CreateDirectory(Path.Combine(Root, "notes"));
        await AssertGets(Brain(), Current);
        Assert.Equal([Version, "notes"], Entries());
    }

    // Catches pruning that deletes file by file: a version another brain holds would keep its marker and lose the rest.
    [InconclusiveFact]
    public async Task Another_version_that_a_brain_holds_is_left_whole()
    {
        var other = Path.Combine(Root, "fedcba9876543210");
        Copy(other, Files);
        File.WriteAllText(Path.Combine(other, ".ready"), "");
        using (new FileStream(Path.Combine(other, ".ready"), FileMode.Open, FileAccess.Read, FileShare.Read))
            await AssertGets(Brain(), Current);
        AssertComplete(other);
    }

    // Catches a brain that holds nothing on its folder between choosing it and starting a child: another version's
    // pruning took the folder away.
    [InconclusiveFact]
    public async Task A_brain_waiting_between_its_lease_and_its_child_keeps_its_folder()
    {
        var build = Path.Combine(_profile, "other-build");
        Directory.CreateDirectory(build);
        foreach (var file in Directory.GetFiles(AppContext.BaseDirectory))
            File.Copy(file, Path.Combine(build, Path.GetFileName(file)));
        File.AppendAllText(Path.Combine(build, "Cluewren.Child.runtimeconfig.json"), "\n");
        var go = Path.Combine(_profile, "go");
        var waiting = Brain(build, go, Path.Combine(_profile, "run"));
        var folder = (await waiting.StandardOutput.ReadLineAsync())?.Trim();
        if (folder is null)
            Assert.Fail("the waiting brain said nothing: " + await waiting.StandardError.ReadToEndAsync());
        Assert.Equal(Root, Path.GetDirectoryName(folder));
        Assert.NotEqual(Current, folder);
        await AssertGets(Brain(), Current);
        Assert.True(File.Exists(Path.Combine(folder, ".ready")), "the other version's pruning took the waiting brain's folder");
        File.WriteAllText(go, "");
        var (code, output, error) = await Finish(waiting);
        Assert.True(code == 0, error);
        Assert.Contains("42", output);
    }
}
