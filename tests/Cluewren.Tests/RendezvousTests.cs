using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Cluewren.Bridge;

namespace Cluewren.Tests;

static class Advert
{
    public static string Write(string folder, int pid, string project, string engine = "unity", string id = "editor-1",
        int min = BridgeServer.Protocol, int max = BridgeServer.Protocol, string? pluginVersion = null)
    {
        var path = Path.Combine(Directory.CreateDirectory(Rendezvous.EditorsFolder(folder)).FullName, $"{pid}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["pid"] = pid, ["id"] = id, ["engine"] = engine, ["project_dir"] = project, ["protocol_min"] = min,
            ["protocol_max"] = max, ["plugin_version"] = pluginVersion,
        }));
        return path;
    }
}

sealed class OtherProcess : IDisposable
{
    readonly Process _process = Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Cluewren.Tools.exe"))
    {
        ArgumentList = { "idle" }, UseShellExecute = false, RedirectStandardInput = true,
    })!;

    public int Pid => _process.Id;

    // The process object stays open until Dispose, so its pid is not given to another process while a test uses it.
    public void End()
    {
        _process.Kill();
        _process.WaitForExit();
    }

    public void Dispose()
    {
        if (!_process.HasExited)
            End();
        _process.Dispose();
    }
}

public sealed class RendezvousTests : IDisposable
{
    const int Protocol = BridgeServer.Protocol;
    const string Url = "ws://127.0.0.1:1/v1/bridge";

    static readonly int Me = Environment.ProcessId;
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    readonly string _folder = Path.Combine(Path.GetTempPath(), "cluewren-tests-" + Guid.NewGuid().ToString("N"));
    readonly string _projects = Directory.CreateTempSubdirectory("cluewren-rendezvous").FullName;
    readonly List<OtherProcess> _others = [];

    public void Dispose()
    {
        foreach (var other in _others)
            other.Dispose();
        foreach (var folder in (string[])[_folder, _projects])
        {
            try
            {
                if (Directory.Exists(folder))
                    Directory.Delete(folder, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    string Project(string relative) => Directory.CreateDirectory(Path.Combine(_projects, relative)).FullName;

    OtherProcess Other()
    {
        var other = new OtherProcess();
        _others.Add(other);
        return other;
    }

    int Dead()
    {
        var gone = Other();
        gone.End();
        return gone.Pid;
    }

    static EditorAdvert Editor(int pid, string project, string engine = "unity", string id = "editor-1", int min = Protocol,
        int max = Protocol, string? pluginVersion = null) => new(pid, id, engine, project, min, max, PluginVersion: pluginVersion);

    static Invite By(int brain, long? born = null) =>
        new(Url, "c2VjcmV0", brain, born ?? Rendezvous.Born(brain), DateTimeOffset.UnixEpoch, Protocol, "editor-1");

    [Fact]
    public void An_advert_is_read_with_its_engine_in_lower_case_and_the_time_it_was_written()
    {
        var foo = Project("Foo");
        Assert.Empty(Rendezvous.Editors(_folder));
        var path = Path.Combine(Directory.CreateDirectory(Rendezvous.EditorsFolder(_folder)).FullName, $"{Me}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["pid"] = Me, ["id"] = "a1", ["engine"] = "Unity", ["project_dir"] = foo, ["protocol_min"] = 1, ["protocol_max"] = 2,
            ["engine_version"] = "6000.0.1f1", ["plugin_version"] = "0.2.0", ["host"] = "editor",
        }));
        var read = Assert.Single(Rendezvous.Editors(_folder));
        Assert.Equal(new EditorAdvert(Me, "a1", "unity", foo, 1, 2, "6000.0.1f1", "0.2.0", "editor")
        {
            Written = File.GetLastWriteTimeUtc(path),
        }, read);
        Assert.Equal($"unity {foo}", read.Described);
    }

    [Fact]
    public void An_advert_whose_process_is_gone_is_deleted()
    {
        var foo = Project("Foo");
        var left = Advert.Write(_folder, Dead(), foo);
        var mine = Advert.Write(_folder, Me, foo);
        Assert.Equal(Me, Assert.Single(Rendezvous.Editors(_folder)).Pid);
        Assert.False(File.Exists(left));
        Assert.True(File.Exists(mine));
    }

    [Fact]
    public void An_advert_older_than_the_process_that_has_its_pid_is_deleted()
    {
        var path = Advert.Write(_folder, Me, Project("Foo"));
        var started = Rendezvous.StartOf(Me)!.Value;
        File.SetLastWriteTimeUtc(path, started.AddSeconds(-2));
        Assert.Single(Rendezvous.Editors(_folder));
        File.SetLastWriteTimeUtc(path, started.AddMinutes(-1));
        Assert.Empty(Rendezvous.Editors(_folder));
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("""{"pid":""")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"pid":PID,"id":"a","engine":"unity","project_dir":"","protocol_min":2,"protocol_max":2}""")]
    [InlineData("""{"pid":PID,"id":"","engine":"unity","project_dir":"C:\\Games\\Foo","protocol_min":2,"protocol_max":2}""")]
    [InlineData("""{"pid":PID,"id":"a","project_dir":"C:\\Games\\Foo","protocol_min":2,"protocol_max":2}""")]
    [InlineData("""{"pid":1,"id":"a","engine":"unity","project_dir":"C:\\Games\\Foo","protocol_min":2,"protocol_max":2}""")]
    public void A_file_that_is_no_whole_advert_of_its_process_is_skipped_and_left_for_the_next_look(string content)
    {
        var path = Path.Combine(Directory.CreateDirectory(Rendezvous.EditorsFolder(_folder)).FullName, $"{Me}.json");
        File.WriteAllText(path, content.Replace("PID", Me.ToString()));
        Assert.Empty(Rendezvous.Editors(_folder));
        Assert.True(File.Exists(path));
        Advert.Write(_folder, Me, Project("Foo"));
        Assert.Single(Rendezvous.Editors(_folder));
    }

    [Fact]
    public void A_file_not_named_by_a_pid_and_an_advert_too_large_are_left_alone()
    {
        var foo = Project("Foo");
        var large = Advert.Write(_folder, Me, foo, id: new string('x', 64 * 1024));
        var named = Path.Combine(Rendezvous.EditorsFolder(_folder), "editor.json");
        File.Copy(Advert.Write(_folder, Other().Pid, foo), named);
        Assert.Equal(_others[0].Pid, Assert.Single(Rendezvous.Editors(_folder)).Pid);
        Assert.True(File.Exists(large) && File.Exists(named));
    }

    [Fact]
    public void With_no_editor_open_the_choice_says_so_and_is_not_final()
    {
        using var scope = ProjectScope.Related(Project("Foo"));
        Assert.Equal(new Rendezvous.Choice(null, "No editor with the Cluewren plugin is open", false),
            Rendezvous.Choose(scope, [], _folder, Protocol));
    }

    [Fact]
    public void Editors_of_other_projects_only_are_named_and_the_choice_is_not_final()
    {
        var (bar, baz) = (Project("Bar"), Project("Baz"));
        using var scope = ProjectScope.Related(Project("Foo"));
        var choice = Rendezvous.Choose(scope,
            [Editor(4, bar), Editor(8, baz, "godot"), Editor(12, @"\\server\share\Foo", "unreal")], _folder, Protocol);
        Assert.Equal(new Rendezvous.Choice(null,
            $@"No open editor has that project. Open: unity {bar}; godot {baz}; unreal \\server\share\Foo", false), choice);
    }

    [Fact]
    public void An_editor_of_the_project_whose_plugin_speaks_another_protocol_is_final()
    {
        var foo = Project("Foo");
        using var scope = ProjectScope.Related(foo);
        var choice = Rendezvous.Choose(scope, [Editor(4, foo, min: 1, max: 1, pluginVersion: "0.1.0")], _folder, Protocol);
        Assert.Null(choice.Chosen);
        Assert.True(choice.Final);
        Assert.Contains($"The Cluewren plugin in {foo} (0.1.0) speaks protocol 1-1 and this cluewren speaks {Protocol}", choice.Problem);

        Assert.Contains("(unknown version) speaks protocol 3-4",
            Rendezvous.Choose(scope, [Editor(4, foo, min: 3, max: 4)], _folder, Protocol).Problem);

        var spoken = Rendezvous.Choose(scope, [Editor(4, foo, min: 1, max: 1), Editor(8, foo, min: 1, max: 3, pluginVersion: "other-build")], _folder, Protocol);
        using var folder = spoken.Chosen!.Folder;
        Assert.Equal(8, spoken.Chosen.Editor.Pid);
    }

    [Fact]
    public void Editors_of_two_projects_inside_a_related_scope_are_final_and_ask_for_project()
    {
        var games = Project("Games");
        var (foo, bar) = (Project(@"Games\Foo"), Project(@"Games\Bar"));
        EditorAdvert[] editors = [Editor(4, foo), Editor(8, bar, "godot")];
        using (var related = ProjectScope.Related(games))
        {
            var choice = Rendezvous.Choose(related, editors, _folder, Protocol);
            Assert.Equal(new Rendezvous.Choice(null, $"Editors of more than one project are open here: unity {foo}; godot {bar}. "
                + "Pass `project` with the folder of the one to play", true), choice);
        }
        using var exact = ProjectScope.ExactlyAt(bar);
        var named = Rendezvous.Choose(exact, editors, _folder, Protocol);
        using var folder = named.Chosen!.Folder;
        Assert.Equal(8, named.Chosen.Editor.Pid);
        Assert.True(folder.Holds(bar));
    }

    [Fact]
    public void Of_two_adverts_of_one_project_the_one_written_last_is_chosen()
    {
        var foo = Project("Foo");
        var now = DateTime.UtcNow;
        using var scope = ProjectScope.Related(foo);
        var choice = Rendezvous.Choose(scope,
        [
            Editor(4, foo, id: "old") with { Written = now.AddSeconds(-30) },
            Editor(8, foo.ToUpperInvariant() + @"\", id: "new") with { Written = now },
            Editor(12, foo, id: "older") with { Written = now.AddMinutes(-5) },
        ], _folder, Protocol);
        using var folder = choice.Chosen!.Folder;
        Assert.Equal(new Rendezvous.Choice(choice.Chosen, null, true), choice);
        Assert.Equal("new", choice.Chosen.Editor.Id);
        Assert.True(folder.Holds(foo));
    }

    [Fact]
    public void An_editor_whose_invite_is_a_live_other_brains_is_busy()
    {
        var foo = Project("Foo");
        var brain = Other();
        Rendezvous.WriteInvite(_folder, Me, By(brain.Pid));
        using var scope = ProjectScope.Related(foo);
        Assert.Equal(new Rendezvous.Choice(null,
                $"The editor of {foo} is already in a run of another agent session (cluewren process {brain.Pid})", true),
            Rendezvous.Choose(scope, [Editor(Me, foo)], _folder, Protocol));

        var free = Rendezvous.Choose(scope, [Editor(brain.Pid, foo)], _folder, Protocol);
        using var folder = free.Chosen!.Folder;
        Assert.Equal(brain.Pid, free.Chosen.Editor.Pid);
    }

    [Fact]
    public void This_brains_own_invite_does_not_make_the_editor_busy()
    {
        var foo = Project("Foo");
        var editor = Editor(Me, foo);
        Rendezvous.Invite(_folder, editor, Url, [1, 2, 3], Protocol);
        using var scope = ProjectScope.Related(foo);
        var choice = Rendezvous.Choose(scope, [editor], _folder, Protocol);
        using var folder = choice.Chosen!.Folder;
        Assert.Null(choice.Problem);
    }

    [Fact]
    public void A_dead_brains_invite_is_replaced()
    {
        var foo = Project("Foo");
        var editor = Editor(Me, foo);
        var reused = Other();
        using var scope = ProjectScope.Related(foo);
        foreach (var left in new[] { By(Dead(), born: 1234), By(reused.Pid, Rendezvous.Born(reused.Pid) - 1000), By(reused.Pid, born: 0) })
        {
            var path = Rendezvous.InvitePath(_folder, Me);
            if (File.Exists(path))
                File.Delete(path);
            Rendezvous.WriteInvite(_folder, Me, left);
            Assert.False(Rendezvous.Alive(left));
            var choice = Rendezvous.Choose(scope, [editor], _folder, Protocol);
            using var folder = choice.Chosen!.Folder;
            var invitation = Rendezvous.Invite(_folder, editor, Url, [1, 2, 3], Protocol);
            Assert.Equal(path, invitation.Path);
            Assert.Equal(invitation.Invite, Rendezvous.ReadInvite(path));
            Assert.Equal(Me, invitation.Invite.Pid);
        }
    }

    // Before, an invite written in the second of the one it replaced had the same time for a plugin that counts
    // whole seconds, which took it for the one it had refused and never read it.
    [Fact]
    public void An_invite_that_replaces_one_is_written_in_a_later_second()
    {
        var path = Rendezvous.InvitePath(_folder, Me);
        Rendezvous.WriteInvite(_folder, Me, By(Dead(), born: 1234));
        var first = File.GetLastWriteTimeUtc(path);
        Rendezvous.WriteInvite(_folder, Me, By(Environment.ProcessId, Rendezvous.Born(Environment.ProcessId)));
        var second = File.GetLastWriteTimeUtc(path);
        Assert.True(second.Ticks / TimeSpan.TicksPerSecond > first.Ticks / TimeSpan.TicksPerSecond, $"{first:O} then {second:O}");
    }

    [Fact]
    public void Inviting_is_refused_while_a_live_brain_holds_the_editor()
    {
        var brain = Other();
        var theirs = By(brain.Pid);
        var path = Rendezvous.WriteInvite(_folder, Me, theirs);
        var refused = Assert.Throws<RendezvousException>(
            () => Rendezvous.Invite(_folder, Editor(Me, Project("Foo")), Url, [1, 2, 3], Protocol));
        Assert.Equal($"That editor is already in a run of another agent session (cluewren process {brain.Pid})", refused.Message);
        Assert.Equal(theirs, Rendezvous.ReadInvite(path));
        Assert.Equal([path], Directory.GetFiles(Rendezvous.InvitesFolder(_folder)));
        Assert.Equal(path, Rendezvous.WriteInvite(_folder, Me, theirs));
    }

    [Fact]
    public async Task Inviting_waits_for_a_brain_that_holds_the_lock_beside_the_invite_and_leaves_no_lock_behind()
    {
        var editor = Editor(Me, Project("Foo"));
        var path = Rendezvous.InvitePath(_folder, Me);
        Directory.CreateDirectory(Rendezvous.InvitesFolder(_folder));
        Task<Invitation> inviting;
        using (new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            inviting = Task.Run(() => Rendezvous.Invite(_folder, editor, Url, [1, 2, 3], Protocol));
            await Task.Delay(300);
            Assert.False(inviting.IsCompleted);
            Assert.False(File.Exists(path));
        }
        Assert.Equal(path, (await inviting.WaitAsync(Patience)).Path);
        Assert.Equal([path], Directory.GetFiles(Rendezvous.InvitesFolder(_folder)));
    }

    [Fact]
    public async Task A_lock_held_past_the_wait_refuses_the_invite_and_leaves_a_withdrawal_undone()
    {
        var editor = Editor(Me, Project("Foo"));
        var mine = Rendezvous.Invite(_folder, editor, Url, [1, 2, 3], Protocol);
        using (new FileStream(mine.Path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose))
        {
            var withdrawing = Task.Run(() => Rendezvous.Withdraw(mine.Path, mine.Invite));
            var refused = await Assert.ThrowsAsync<RendezvousException>(
                () => Task.Run(() => Rendezvous.Invite(_folder, editor, Url, [4, 5, 6], Protocol)).WaitAsync(Patience));
            Assert.Equal("That editor is being invited by another agent session", refused.Message);
            await withdrawing.WaitAsync(Patience);
        }
        Assert.Equal(mine.Invite, Rendezvous.ReadInvite(mine.Path));
        Assert.Equal([mine.Path], Directory.GetFiles(Rendezvous.InvitesFolder(_folder)));
    }

    [Fact]
    public void An_invite_names_this_brain_and_the_advert_it_answers()
    {
        var editor = Editor(Me, Project("Foo"), id: "the-advert");
        byte[] secret = [9, 8, 7, 6];
        var before = DateTimeOffset.UtcNow;
        var invitation = Rendezvous.Invite(_folder, editor, Url, secret, Protocol);
        Assert.Equal(Rendezvous.InvitePath(_folder, Me), invitation.Path);
        Assert.Equal(Path.Combine(_folder, "invites", $"{Me}.json"), invitation.Path);
        Assert.Same(editor, invitation.Editor);

        var json = JsonNode.Parse(File.ReadAllText(invitation.Path))!.AsObject();
        Assert.Equal(["born", "editor_id", "pid", "protocol", "secret", "started", "url"],
            json.Select(field => field.Key).Order(StringComparer.Ordinal));
        Assert.Equal(Url, json["url"]!.GetValue<string>());
        Assert.Equal(secret, Convert.FromBase64String(json["secret"]!.GetValue<string>()));
        Assert.Equal(Me, json["pid"]!.GetValue<int>());
        Assert.Equal(new DateTimeOffset(Rendezvous.StartOf(Me)!.Value).ToUnixTimeMilliseconds(), json["born"]!.GetValue<long>());
        Assert.Equal(Protocol, json["protocol"]!.GetValue<int>());
        Assert.Equal("the-advert", json["editor_id"]!.GetValue<string>());
        Assert.InRange(json["started"]!.GetValue<DateTimeOffset>(), before, DateTimeOffset.UtcNow);

        Assert.Equal(invitation.Invite, Rendezvous.ReadInvite(invitation.Path));
        Assert.True(Rendezvous.Alive(invitation.Invite));
        Assert.Equal([invitation.Path], Directory.GetFiles(Rendezvous.InvitesFolder(_folder)));
    }

    [Fact]
    public void Withdrawing_leaves_an_invite_another_brain_has_written_since()
    {
        var mine = Rendezvous.Invite(_folder, Editor(Me, Project("Foo")), Url, [1, 2, 3], Protocol);
        File.Delete(mine.Path);
        var theirs = By(Other().Pid);
        Rendezvous.WriteInvite(_folder, Me, theirs);
        Rendezvous.Withdraw(mine.Path, mine.Invite);
        Assert.Equal(theirs, Rendezvous.ReadInvite(mine.Path));
        Rendezvous.Withdraw(mine.Path, theirs);
        Assert.False(File.Exists(mine.Path));
        Rendezvous.Withdraw(mine.Path, theirs);
        Assert.Empty(Directory.GetFiles(Rendezvous.InvitesFolder(_folder)));
        Rendezvous.Withdraw(Rendezvous.InvitePath(Path.Combine(_folder, "missing"), Me), theirs);
    }

    [Fact]
    public void Only_the_owner_has_access_to_an_invite_and_its_folders()
    {
        var path = Rendezvous.Invite(_folder, Editor(Me, Project("Foo")), Url, [1, 2, 3], Protocol).Path;
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
                     new DirectoryInfo(Rendezvous.InvitesFolder(_folder)).GetAccessControl(),
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

    static async Task<string> Next(Channel<string> reasons)
    {
        using var patience = new CancellationTokenSource(Patience);
        return await reasons.Reader.ReadAsync(patience.Token);
    }

    [Fact]
    public async Task Choosing_waits_until_an_editor_of_the_project_adverts()
    {
        var foo = Project("Foo");
        using var scope = ProjectScope.Related(foo);
        var reasons = Channel.CreateUnbounded<string>();
        var choosing = Rendezvous.ChooseAsync(_folder, scope, Protocol, reason => reasons.Writer.TryWrite(reason));
        Assert.Equal("No editor with the Cluewren plugin is open", await Next(reasons));
        Assert.False(choosing.IsCompleted);
        Advert.Write(_folder, Me, foo, id: "late");
        var chosen = await choosing.WaitAsync(Patience);
        using var folder = chosen.Folder;
        Assert.Equal((Me, "late"), (chosen.Editor.Pid, chosen.Editor.Id));
        Assert.True(folder.Holds(foo));
        Assert.False(reasons.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Choosing_gives_up_at_the_deadline_with_the_last_reason_it_had()
    {
        var bar = Project("Bar");
        using var scope = ProjectScope.Related(Project("Foo"));
        using var deadline = new CancellationTokenSource();
        var reasons = Channel.CreateUnbounded<string>();
        var choosing = Rendezvous.ChooseAsync(_folder, scope, Protocol, reason => reasons.Writer.TryWrite(reason), deadline.Token);
        Assert.Equal("No editor with the Cluewren plugin is open", await Next(reasons));
        Advert.Write(_folder, Me, bar);
        var named = await Next(reasons);
        Assert.Equal($"No open editor has that project. Open: unity {bar}", named);
        deadline.Cancel();
        var gaveUp = await Assert.ThrowsAsync<RendezvousException>(() => choosing.WaitAsync(Patience));
        Assert.Equal(named, gaveUp.Message);
    }

    [Fact]
    public async Task Choosing_does_not_wait_on_a_final_reason()
    {
        var foo = Project("Foo");
        Advert.Write(_folder, Me, foo, min: 1, max: 1);
        using var scope = ProjectScope.Related(foo);
        var said = new List<string>();
        var final = await Assert.ThrowsAsync<RendezvousException>(
            () => Rendezvous.ChooseAsync(_folder, scope, Protocol, said.Add).WaitAsync(Patience));
        Assert.Contains("speaks protocol 1-1", final.Message);
        Assert.Empty(said);
    }
}
