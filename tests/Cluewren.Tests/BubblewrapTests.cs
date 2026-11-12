using Cluewren.Sandbox;

namespace Cluewren.Tests;

public sealed class BubblewrapTests
{
    // Catches bwrap taken from a relative or empty PATH entry, which resolves against wherever the brain was started.
    [Fact]
    public void Bubblewrap_comes_only_from_an_absolute_path_entry_and_by_its_full_path()
    {
        var name = "playtest-bwrap-" + Guid.NewGuid().ToString("N");
        var near = Directory.CreateDirectory(Path.Combine(Environment.CurrentDirectory, name)).FullName;
        var far = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), name)).FullName;
        try
        {
            File.WriteAllText(Path.Combine(near, "bwrap"), "");
            File.WriteAllText(Path.Combine(far, "bwrap"), "");
            var relative = Path.GetRelativePath(Environment.CurrentDirectory, near);
            Assert.Equal(name, relative);
            Assert.Equal(Path.Combine(far, "bwrap"), SandboxStarter.FindBubblewrap(string.Join(Path.PathSeparator, "", relative, far)));
            Assert.Equal(Path.Combine(far, "bwrap"), SandboxStarter.FindBubblewrap(Path.Combine(far, "x", "..")));
            Assert.Null(SandboxStarter.FindBubblewrap(string.Join(Path.PathSeparator, "", relative)));
            Assert.Null(SandboxStarter.FindBubblewrap(null));
        }
        finally
        {
            Directory.Delete(near, true);
            Directory.Delete(far, true);
        }
    }
}
