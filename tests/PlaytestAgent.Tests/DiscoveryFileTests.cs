using System.Security.AccessControl;
using System.Security.Principal;
using PlaytestAgent.Bridge;

namespace PlaytestAgent.Tests;

public sealed class DiscoveryFileTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), "playtest-agent-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Round_trips()
    {
        var written = new Discovery("ws://127.0.0.1:1/v1/bridge", "c2VjcmV0", 7, DateTimeOffset.UnixEpoch);
        var path = DiscoveryFile.Write(written, _folder);
        Assert.Equal(written, DiscoveryFile.Read(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Only_the_owner_has_access()
    {
        var path = DiscoveryFile.Write(new Discovery("u", "s", 1, DateTimeOffset.UnixEpoch), _folder);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            return;
        }
#pragma warning disable CA1416
        var me = WindowsIdentity.GetCurrent().User!;
        foreach (var security in new FileSystemSecurity[]
                 {
                     new FileInfo(path).GetAccessControl(),
                     new DirectoryInfo(_folder).GetAccessControl(),
                 })
        {
            Assert.True(security.AreAccessRulesProtected);
            var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>().ToList();
            Assert.NotEmpty(rules);
            Assert.All(rules, rule => Assert.Equal(me, rule.IdentityReference));
        }
#pragma warning restore CA1416
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }
}
