using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Cluewren.Mcp;

namespace Cluewren.Tests;

sealed class SymbolicLinkFactAttribute : FactAttribute
{
    public SymbolicLinkFactAttribute()
    {
        var folder = Directory.CreateTempSubdirectory("link-probe").FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, "target"), "");
            File.CreateSymbolicLink(Path.Combine(folder, "link"), Path.Combine(folder, "target"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Skip = $"this account can't create a symbolic link here: {e.Message}";
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}

sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
            Skip = "Unix file modes; runs where the suite runs on Linux or macOS";
    }
}

public class CodexApprovalTests
{
    static readonly string[] Command = ["C:/tools/cluewren.exe", "mcp"];

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateHardLinkW(string link, string existing, IntPtr security);

    [DllImport("libc", SetLastError = true)]
    static extern int link(string existing, string link);

    static void HardLink(string name, string existing) =>
        Assert.True(OperatingSystem.IsWindows() ? CreateHardLinkW(name, existing, IntPtr.Zero) : link(existing, name) == 0);

    static string Lines(params string[] lines) => string.Join('\n', lines) + "\n";

    const string Header = "# cluewren connect: Cluewren's MCP server, for the game open in your editor (`cluewren connect codex --remove` takes it out)";

    const string Table = "[mcp_servers.\"cluewren\"]";

    // Exactly what `cluewren connect codex` 0.1.0 appended.
    static readonly string Written010 = Lines(
        Header,
        Table,
        "command = \"C:/tools/cluewren.exe\"",
        "args = [\"mcp\"]",
        "# (its tools reach only the game open in the editor and the game's memory folder)",
        "default_tools_approval_mode = \"approve\"");

    [Fact]
    public void Codex_is_told_to_approve_the_tools_only_when_asked()
    {
        Assert.Equal(Lines(Header, Table, "command = \"C:/tools/cluewren.exe\"", "args = [\"mcp\"]"), Connect.CodexConfig("", Command));
        var approved = Connect.CodexConfig("", Command, approve: true);
        Assert.EndsWith("args = [\"mcp\"]\n# cluewren connect --approve-tools: Codex calls these tools without asking you first\n"
            + "default_tools_approval_mode = \"approve\"\n", approved);
        Assert.Equal(approved, Connect.CodexConfig(approved, Command, approve: true));
    }

    [Fact]
    public void A_plain_rerun_takes_back_the_approval_cluewren_wrote_and_nothing_else()
    {
        Assert.Equal(Lines(Header, Table, "command = \"C:/tools/cluewren.exe\"", "args = [\"mcp\"]"), Connect.CodexConfig(Written010, Command));
        var approved = Connect.CodexConfig("model = \"gpt\"\n", Command, approve: true);
        Assert.Equal(Connect.CodexConfig("model = \"gpt\"\n", Command), Connect.CodexConfig(approved, Command));
        Assert.Equal(Lines(Table, "command = \"C:/tools/cluewren.exe\"", "args = [\"mcp\"]",
                "# cluewren connect --approve-tools: Codex calls these tools without asking you first", "default_tools_approval_mode = \"approve\""),
            Connect.CodexConfig(Lines(Table, "command = \"C:/tools/cluewren.exe\"", "args = [\"mcp\"]"), Command, approve: true));
    }

    [Fact]
    public void An_approval_line_cluewren_did_not_write_is_kept()
    {
        var mine = Lines(Table, "command = \"C:/tools/cluewren.exe\"", "args = [\"mcp\"]", "default_tools_approval_mode = \"approve\"");
        Assert.Equal(mine, Connect.CodexConfig(mine, Command));
        var edited = Written010.Replace("= \"approve\"", "= \"prompt\"");
        Assert.Equal(edited, Connect.CodexConfig(edited, Command));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Everything_but_command_and_args_survives_a_rerun_byte_for_byte(string newline)
    {
        string Text(string command, bool approval) => string.Join(newline, new[]
        {
            "model = \"gpt-5\"",
            "",
            Header,
            Table,
            $"command = \"{command}\"",
            "args = [\"mcp\"]",
            "cwd = \"C:/Games/Foo\"  # where my game is",
            "startup_timeout_sec = 30",
            "enabled_tools = [\"start_run\", 'probe']",
        }.Concat(approval
            ? ["# (its tools reach only the game open in the editor and the game's memory folder)", "default_tools_approval_mode = \"approve\""]
            : []).Concat(
        [
            "",
            "[mcp_servers.\"cluewren\".env]",
            "CLUEWREN_SANDBOX = \"required\"",
            "",
            "[mcp_servers.cluewren.tools.probe]",
            "approval_mode = \"prompt\"",
            "",
            "[profiles.a]",
            "model = \"b\"",
            "",
        ]));
        Assert.Equal(Text("C:/tools/cluewren.exe", false), Connect.CodexConfig(Text("C:/old/cluewren.exe", true), Command));
    }

    [Fact]
    public void A_rewritten_line_keeps_its_key_and_its_comment()
    {
        Assert.Equal(Lines(Table, "  \"command\" = \"C:/tools/cluewren.exe\"   # mine", "args=[\"mcp\"] # also mine"),
            Connect.CodexConfig(Lines(Table, "  \"command\" = 'C:\\old\\cluewren.exe'   # mine", "args=[ 'mcp', ] # also mine"), Command));
    }

    [Fact]
    public void A_line_inside_a_multi_line_value_is_not_taken_for_a_table()
    {
        var notes = Lines("notes = \"\"\"", Table, "command = \"mine\"", "\"\"\"", "list = [", "  [1, 2],", "]");
        Assert.Equal(notes + "\n" + Connect.CodexConfig("", Command), Connect.CodexConfig(notes, Command));
        Assert.Equal(notes, Connect.CodexConfig(notes, null));
    }

    // Regression: a spelling of the table the reader skipped was appended to as if there were none, giving Codex two tables.
    [Theory]
    [InlineData("[mcp_servers.\"clue\\u0077ren\"]\ncommand = \"node\"\n")]
    [InlineData("[mcp_servers.\"cluewren\\u0020\"]\ncommand = \"x\"\n")]
    [InlineData("[profiles.\"a\\u0062\"]\nmodel = \"x\"\n")]
    [InlineData("[mcp_servers.cluewren]\ncommand = \"x\"\n[mcp_servers.\"cluewren\"]\nargs = []\n")]
    [InlineData("[mcp_servers]\ncluewren = { command = \"x\" }\n")]
    [InlineData("[mcp_servers]\nother = { command = \"x\" }\n")]
    [InlineData("mcp_servers = { cluewren = { command = \"x\" } }\n")]
    [InlineData("mcp_servers.other.command = \"x\"\n")]
    [InlineData("[[mcp_servers.cluewren]]\ncommand = \"x\"\n")]
    [InlineData("[[mcp_servers]]\nname = \"x\"\n")]
    [InlineData("model = \"m\"\nthis line = isn't toml\n")]
    [InlineData("[mcp_servers.cluewren]\ncommand = \"x\"\nnotes = '''\nnever closed\n")]
    public void A_file_the_reader_cannot_read_whole_is_not_changed(string text)
    {
        Assert.Throws<FormatException>(() => Connect.CodexConfig(text, Command));
        Assert.Throws<FormatException>(() => Connect.CodexConfig(text, Command, approve: true));
        Assert.Throws<FormatException>(() => Connect.CodexConfig(text, null));
    }

    // Regression: a wrapper or another host named like Cluewren was taken for it and rewritten or removed.
    [Theory]
    [InlineData("command = \"node\"\nargs = [\"C:/srv/cluewren.js\"]")]
    [InlineData("command = \"C:/tools/cluewren.cmd\"\nargs = [\"mcp\"]")]
    [InlineData("command = \"C:/tools/cluewren.bat\"\nargs = [\"mcp\"]")]
    [InlineData("command = \"C:/tools/cluewren.exe\"\nargs = [\"serve\"]")]
    [InlineData("command = \"C:/tools/cluewren.exe\"\nargs = [\"mcp\", \"--x\"]")]
    [InlineData("command = \"C:/tools/cluewren-helper.exe\"\nargs = [\"mcp\"]")]
    [InlineData("command = \"C:/tools/cluewren.exe\"")]
    [InlineData("command = \"C:/tools/Cluewren.exe\"\nargs = [\"C:/tools/Cluewren.dll\", \"mcp\"]")]
    [InlineData("command = \"dotnet\"\nargs = [\"C:/tools/Other.dll\", \"mcp\"]")]
    [InlineData("command = \"C:/tools/cluewren.exe\"\nargs = [\n  \"mcp\",\n]")]
    [InlineData("command = \"C:\\q\\cluewren.exe\"\nargs = [\"mcp\"]")]
    [InlineData("command = \"C:/tools/cluewren.exe\"\ncommand = \"C:/tools/cluewren.exe\"\nargs = [\"mcp\"]")]
    [InlineData("command.path = \"x\"\nargs = [\"mcp\"]")]
    [InlineData("url = \"https://example.com/mcp\"")]
    [InlineData("command = \"C:/tools/cluewren.exe\"\nargs = [\"mcp\"]\nbearer_token_env_var = \"TOKEN\"")]
    [InlineData("command = \"C:/tools/cluewren.exe\"\nargs = [\"mcp\"]\nenv_http_headers = { X = \"Y\" }")]
    [InlineData("command = \"C:/tools/cluewren.exe\"\nargs = [\"mcp\"]\n[mcp_servers.cluewren.http_headers]\nX = \"y\"")]
    [InlineData("enabled = false")]
    public void A_cluewren_table_that_is_not_cluewrens_is_left_as_it_is(string body)
    {
        var text = Lines(Table, body);
        Assert.Throws<Connect.NotCluewrensException>(() => Connect.CodexConfig(text, Command));
        Assert.Throws<Connect.NotCluewrensException>(() => Connect.CodexConfig(text, Command, approve: true));
        Assert.Throws<Connect.NotCluewrensException>(() => Connect.CodexConfig(text, null));
    }

    [Fact]
    public void Settings_for_cluewren_without_a_table_of_their_own_are_left_as_they_are()
    {
        var text = Lines("[mcp_servers.cluewren.env]", "KEY = \"v\"");
        Assert.Throws<Connect.NotCluewrensException>(() => Connect.CodexConfig(text, Command));
        Assert.Throws<Connect.NotCluewrensException>(() => Connect.CodexConfig(text, null));
    }

    // Regression: the reason for leaving a server named its command line, which can hold a key of its own.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void What_connect_says_about_another_server_names_none_of_its_settings(bool adding)
    {
        var text = Lines(Table, "command = \"C:/secret/tool.exe\"", "args = [\"--token=abc123\"]", "[mcp_servers.cluewren.env]", "KEY = \"s3cret\"");
        var said = Record.Exception(() => Connect.CodexConfig(text, adding ? Command : null))!.Message;
        Assert.DoesNotContain("secret", said);
        Assert.DoesNotContain("abc123", said);
        Assert.DoesNotContain("s3cret", said);
    }

    [Theory]
    [InlineData("command = \"C:/Program Files/dotnet/dotnet.exe\"\nargs = [\"C:/src/bin/cluewren.dll\", \"mcp\"]")]
    [InlineData("command = 'dotnet'\nargs = ['C:\\src\\bin\\Cluewren.DLL', 'mcp']")]
    [InlineData("command = \"D:/Other/dotnet\"\nargs = [\"cluewren.dll\", \"mcp\"]")]
    [InlineData("command = 'D:\\Other\\Cluewren.EXE'\nargs = [\"mcp\"]")]
    [InlineData("command = \"/opt/cluewren/cluewren\"\nargs = [\"mcp\"]")]
    [InlineData("command = \"C:\\u002Ftools\\u002Fcluewren.exe\"\nargs = [\"\\u006Dcp\"]")]
    public void Cluewrens_own_table_is_rewritten_in_each_install_form(string owned)
    {
        Assert.Equal(Lines(Table, "command = \"C:/tools/cluewren.exe\"", "args = [\"mcp\"]", "cwd = \"C:/Games\""),
            Connect.CodexConfig(Lines(Table, owned, "cwd = \"C:/Games\""), Command));
        Assert.Equal("model = \"m\"\n\n", Connect.CodexConfig(Lines("model = \"m\"", "", Table, owned, "[mcp_servers.cluewren.env]", "K = \"v\""), null));
    }

    // Regression: a line of a multi-line string that read like connect's marker was deleted with the table, leaving the string open.
    [Fact]
    public void Remove_deletes_no_line_of_a_string_that_reads_like_the_marker()
    {
        var instructions = Lines("developer_instructions = \"\"\"", "# cluewren connect\"\"\"");
        Assert.Equal(instructions, Connect.CodexConfig(instructions + Lines("[mcp_servers.cluewren]", "command = \"cluewren\"", "args = [\"mcp\"]"), null));
        var notes = Lines("notes = \"\"\"", Header, "\"\"\"");
        Assert.Equal(notes, Connect.CodexConfig(notes + Lines(Table, "command = \"cluewren\"", "args = [\"mcp\"]"), null));
    }

    // Regression: appending trimmed the file's own trailing line breaks, and removing normalized the blank lines that were left.
    [Theory]
    [InlineData("model = \"x\"\n")]
    [InlineData("model = \"x\"\r\n")]
    [InlineData("model = \"x\"")]
    [InlineData("a = 1\r\nb = 2")]
    [InlineData("model = \"x\"\n\n")]
    [InlineData("a = 1\r\n\r\n")]
    [InlineData("\n")]
    [InlineData("")]
    [InlineData("# a comment\n[profiles.a]\nmodel = \"b\"\n\n\n")]
    public void Add_then_remove_gives_back_the_same_bytes(string text)
    {
        foreach (var approve in new[] { false, true })
        {
            var added = Connect.CodexConfig(text, Command, approve);
            Assert.StartsWith(text, added);
            if (text.Contains("\r\n"))
                Assert.DoesNotContain("\n", added.Replace("\r\n", ""));
            Assert.Equal(text, Connect.CodexConfig(added, null));
        }
    }

    [Fact]
    public void Remove_leaves_what_was_written_after_the_table()
    {
        var added = Connect.CodexConfig("model = \"x\"\n", Command) + "\n# mine\n[profiles.a]\nx = 1\n";
        Assert.Equal("model = \"x\"\n\n# mine\n[profiles.a]\nx = 1\n", Connect.CodexConfig(added, null));
    }

    static string TempConfig(string? text)
    {
        var folder = Directory.CreateTempSubdirectory("codex-home").FullName;
        var path = Path.Combine(folder, "config.toml");
        if (text is not null)
            File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void A_change_is_written_into_the_file_itself_and_nothing_beside_it()
    {
        var path = TempConfig("model = \"m\"\n");
        var otherName = Path.Combine(Directory.CreateTempSubdirectory("codex-link").FullName, "other.toml");
        try
        {
            HardLink(otherName, path);
            var said = new List<string>();
            Assert.True(new Connect(said.Add).ApplyCodex(Connect.PlanCodex(path, Command, false)));
            Assert.Equal("model = \"m\"\n\n" + Connect.CodexConfig("", Command), File.ReadAllText(path));
            Assert.Equal(File.ReadAllText(path), File.ReadAllText(otherName));
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
            Assert.True(new Connect(said.Add).ApplyCodex(Connect.PlanCodex(path, Command, false)));
            Assert.Single(said);
            Assert.True(new Connect(said.Add).ApplyCodex(Connect.PlanCodex(path, null, false)));
            Assert.Equal("model = \"m\"\n"u8.ToArray(), File.ReadAllBytes(path));
            Assert.Equal("model = \"m\"\n"u8.ToArray(), File.ReadAllBytes(otherName));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
            Directory.Delete(Path.GetDirectoryName(otherName)!, true);
        }
    }

    [Fact]
    public void A_config_that_is_not_there_yet_is_created()
    {
        var path = TempConfig(null);
        try
        {
            Assert.True(new Connect(_ => { }).ApplyCodex(Connect.PlanCodex(path, Command, false)));
            Assert.Equal(Connect.CodexConfig("", Command), File.ReadAllText(path));
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    // Regression: what was computed from the text first read was written over an edit made in between.
    [Fact]
    public void A_config_that_changed_while_connect_read_it_is_not_written()
    {
        var path = TempConfig("model = \"m\"\n");
        try
        {
            var said = new List<string>();
            var change = Connect.PlanCodex(path, Command, false);
            File.WriteAllText(path, "model = \"edited meanwhile\"\n");
            Assert.False(new Connect(said.Add).ApplyCodex(change));
            Assert.Equal("model = \"edited meanwhile\"\n", File.ReadAllText(path));
            Assert.Contains("changed while connect was reading it", said[^1]);
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    // Regression: the replace swapped a linked config.toml (a dotfiles setup) for a plain file, cutting it off from its target.
    [SymbolicLinkFact]
    public void A_config_that_is_a_link_stays_a_link_and_its_target_is_written()
    {
        var folder = Directory.CreateTempSubdirectory("codex-home").FullName;
        try
        {
            var target = Path.Combine(folder, "dotfiles.toml");
            File.WriteAllText(target, "model = \"m\"\n");
            var path = Path.Combine(folder, "config.toml");
            File.CreateSymbolicLink(path, target);
            Assert.True(new Connect(_ => { }).ApplyCodex(Connect.PlanCodex(path, Command, false)));
            Assert.Equal(target, new FileInfo(path).LinkTarget);
            Assert.Equal("model = \"m\"\n\n" + Connect.CodexConfig("", Command), File.ReadAllText(target));
            Assert.Equal(["config.toml", "dotfiles.toml"], Directory.GetFiles(folder).Select(file => Path.GetFileName(file)).Order());
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    // Regression: the temporary file took the umask's mode and replaced a 0600 config, leaving it readable to other users.
    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public void A_written_config_is_never_readable_by_more_than_before()
    {
        var path = TempConfig("model = \"m\"\n");
        try
        {
            const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            File.SetUnixFileMode(path, ownerOnly);
            Assert.True(new Connect(_ => { }).ApplyCodex(Connect.PlanCodex(path, Command, false)));
            Assert.Equal(ownerOnly, File.GetUnixFileMode(path));
            File.Delete(path);
            Assert.True(new Connect(_ => { }).ApplyCodex(Connect.PlanCodex(path, Command, false)));
            Assert.Equal(ownerOnly, File.GetUnixFileMode(path));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    // Regression: a config held open by another program made connect throw, or was left without the lines to set by hand.
    [Fact]
    public void A_config_in_use_is_left_as_it_is_with_the_lines_to_set_by_hand()
    {
        var path = TempConfig("model = \"m\"\n");
        try
        {
            foreach (var share in (FileShare[])[FileShare.None, FileShare.Read])
            {
                var said = new List<string>();
                using (new FileStream(path, FileMode.Open, FileAccess.Read, share))
                    Assert.False(new Connect(said.Add).ApplyCodex(Connect.PlanCodex(path, Command, false)));
                Assert.Equal("model = \"m\"\n", File.ReadAllText(path));
                Assert.StartsWith($"Left {path} as it is: ", said[^1]);
                Assert.Contains("command = \"C:/tools/cluewren.exe\"", said[^1]);
            }
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Fact]
    public void A_config_connect_leaves_is_not_written_and_says_why()
    {
        var text = Lines(Table, "command = \"node\"", "args = [\"server.js\"]");
        var path = TempConfig(text);
        try
        {
            var said = new List<string>();
            Assert.False(new Connect(said.Add).ApplyCodex(Connect.PlanCodex(path, Command, false)));
            Assert.Equal(text, File.ReadAllText(path));
            Assert.Contains("isn't Cluewren's", said[^1]);
            Assert.Contains("command = \"C:/tools/cluewren.exe\"", said[^1]);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }
}

public class ClaudeRegistrationTests
{
    static readonly string[] Command = [@"C:\Users\me\.dotnet\tools\cluewren.exe", "mcp"];

    const string User = "User config (available in all your projects)";

    const string Absent = "No MCP server named \"cluewren\". Run `claude mcp add` to add one.";

    static string Get(string scope, string command, string args, params string[] env) =>
        string.Join('\n', new[]
        {
            "cluewren:", $"  Scope: {scope}", "  Status: \u00d7 Failed to connect", "  Issue: CONNECTION_CLOSED: Connection closed",
            "  Type: stdio", $"  Command: {command}", args.Length > 0 ? $"  Args: {args}" : "  Args:", "  Environment:",
        }.Concat(env.Select(setting => "    " + setting))
            .Concat(["", $"To remove this server, run: claude mcp remove cluewren -s {(scope.StartsWith("User") ? "user" : "local")}", ""]));

    static (int?, string) Shows(string scope, string command, string args, params string[] env) => (0, Get(scope, command, args, env));

    sealed class Claude(params (int? Code, string Output)[] gets)
    {
        public readonly List<string> Calls = [];
        public readonly List<string> Said = [];
        public (int? Code, string Output) Removing { get; init; } = (0, "Removed MCP server cluewren from user config");
        public (int? Code, string Output) Adding { get; init; } = (0, "Added stdio MCP server cluewren");
        int _read;

        (int? Code, string Output) Run(string[] command)
        {
            Calls.Add(string.Join('|', command));
            return command[2] switch
            {
                "get" => gets[Math.Min(_read++, gets.Length - 1)],
                "remove" => Removing,
                "add" => Adding,
                _ => (null, "unexpected"),
            };
        }

        public bool Register(string[]? command) => new Connect(Said.Add, Run).ClaudeServer(command);
    }

    const string Getting = "claude|mcp|get|cluewren";
    const string Removing = "claude|mcp|remove|--scope|user|cluewren";
    static readonly string Adding = $"claude|mcp|add|--scope|user|cluewren|--|{Command[0]}|mcp";

    [Fact]
    public void A_server_that_is_explicitly_absent_is_added()
    {
        var claude = new Claude((1, Absent));
        Assert.True(claude.Register(Command));
        Assert.Equal([Getting, Adding], claude.Calls);
    }

    [Fact]
    public void The_same_command_already_registered_is_neither_removed_nor_added()
    {
        var claude = new Claude(Shows(User, Command[0], "mcp"));
        Assert.True(claude.Register(Command));
        Assert.Equal([Getting], claude.Calls);
    }

    public static TheoryData<int?, string> Unknown => new()
    {
        { null, "Claude Code's `claude` command isn't on PATH" },
        { 1, "Error: the configuration file is corrupted" },
        { 0, "something this version of claude prints instead\n" },
        { 0, Get(User, @"C:\Tools\cluewren.exe", "mcp") + "  Command: C:\\x.exe\n" },
    };

    // As claude 2.1.322 printed them: a server whose check passed (no Issue line), and one whose entry has no env key.
    const string Connected = "cluewren:\n  Scope: User config (available in all your projects)\n  Status: \u2714 Connected\n  Type: stdio\n"
        + "  Command: C:\\Users\\me\\.dotnet\\tools\\cluewren.exe\n  Args: mcp\n  Environment:\n\nTo remove this server, run: claude mcp remove cluewren -s user\n";
    const string NoEnvKey = "cluewren:\n  Scope: User config (available in all your projects)\n  Status: \u2718 Failed to connect\n"
        + "  Issue: CONNECTION_CLOSED: Connection closed\n  Type: stdio\n  Command: C:\\Tools\\cluewren.exe\n  Args: mcp\n\n"
        + "To remove this server, run: claude mcp remove cluewren -s user\n";

    [Fact]
    public void The_displays_the_cli_prints_are_read()
    {
        var connected = new Claude((0, Connected));
        Assert.True(connected.Register(Command));
        Assert.Equal([Getting], connected.Calls);
        var bare = new Claude((0, NoEnvKey));
        Assert.True(bare.Register(null));
        Assert.Equal([Getting, Removing], bare.Calls);
    }

    public static TheoryData<string> Misshapen => new()
    {
        Get(User, @"C:\Tools\cluewren.exe", "mcp").Replace("  Command: C:\\Tools\\cluewren.exe\n", "  Command: C:\\Tools\\cluewren.exe\nextra\n"),
        Get(User, @"C:\Tools\cluewren.exe", "mcp").Replace("  Args: mcp\n", "  Args: mcp\nextra\n"),
        Get(User, @"C:\Tools\cluewren.exe", "mcp", "K=v").Replace("    K=v\n", "    K=v\nextra\n"),
        Get(User, @"C:\Tools\cluewren.exe", "mcp").Replace("  Type: stdio\n", "  Type: stdio\n  Tools: 3\n"),
        Get(User, @"C:\Tools\cluewren.exe", "mcp").Replace("\n\nTo remove", "\nTo remove"),
        Get(User, @"C:\Tools\cluewren.exe", "mcp") + "Warning: something else\n",
    };

    // Regression: lines outside the labels were skipped, so a value that ran onto a next line read as exactly `mcp`.
    [Theory]
    [MemberData(nameof(Misshapen))]
    public void A_display_with_a_line_out_of_its_shape_is_not_read(string output)
    {
        var removing = new Claude((0, output));
        Assert.False(removing.Register(null));
        Assert.Equal([Getting], removing.Calls);
        Assert.Contains("Couldn't tell", removing.Said[^1]);
        var adding = new Claude((0, output));
        Assert.False(adding.Register(Command));
        Assert.Equal([Getting], adding.Calls);
        Assert.Contains("Couldn't tell", adding.Said[^1]);
    }

    // Regression: any failed read was taken for "no entry", and an entry was added or removed on a guess.
    [Theory]
    [MemberData(nameof(Unknown))]
    public void When_connect_cannot_tell_what_is_there_it_changes_nothing(int? code, string output)
    {
        var adding = new Claude((code, output));
        Assert.False(adding.Register(Command));
        Assert.Equal([Getting], adding.Calls);
        Assert.Contains("Couldn't tell", adding.Said[^1]);
        var removing = new Claude((code, output));
        Assert.False(removing.Register(null));
        Assert.Equal([Getting], removing.Calls);
    }

    public static TheoryData<string, string, string, string[]> Kept => new()
    {
        { User, @"C:\Old Place\cluewren.exe", "mcp", [] },
        { User, @"C:\Old Place\cluewren.exe", "mcp", ["CLUEWREN_SANDBOX=required"] },
        { User, Command[0], "mcp", ["CLUEWREN_SANDBOX=required"] },
        { User, @"C:\Program Files\dotnet\dotnet.exe", @"C:\src\bin\cluewren.dll mcp", [] },
        { User, @"C:\Windows\System32\cmd.exe", "/c exit 0", [] },
        { User, @"C:\tools\cluewren.cmd", "mcp", [] },
        { "Local config (private to you in this project)", Command[0], "mcp", [] },
    };

    // Regression: an existing entry was removed and added again, losing settings the display doesn't show.
    [Theory]
    [MemberData(nameof(Kept))]
    public void An_existing_entry_that_is_not_this_one_is_never_replaced(string scope, string command, string args, string[] env)
    {
        var claude = new Claude(Shows(scope, command, args, env));
        Assert.False(claude.Register(Command));
        Assert.Equal([Getting], claude.Calls);
        Assert.Contains("Left Claude Code's cluewren MCP server as it is", claude.Said[^1]);
        Assert.Contains("cluewren connect claude", claude.Said[^1]);
    }

    [Fact]
    public void An_older_cluewren_is_named_with_the_way_to_move_to_this_one()
    {
        var claude = new Claude(Shows(User, @"C:\Old\cluewren.exe", "mcp"));
        Assert.False(claude.Register(Command));
        Assert.Contains(@"it runs C:\Old\cluewren.exe mcp, another Cluewren", claude.Said[^1]);
        Assert.Contains("cluewren connect claude --remove, then cluewren connect claude", claude.Said[^1]);
    }

    // Regression: the reason named the other entry's command, which can hold a key of its own; env values were echoed.
    [Fact]
    public void Nothing_connect_says_names_another_entrys_command_args_or_environment()
    {
        var get = Shows(User, @"C:\secret\tool.exe", "--token=abc123", "KEY=s3cret");
        var dll = Shows(User, @"C:\secret\dotnet.exe", @"C:\secret\Cluewren.dll mcp");
        foreach (var shown in new[] { get, dll })
        {
            var adding = new Claude(shown);
            Assert.False(adding.Register(Command));
            var removing = new Claude(shown);
            Assert.False(removing.Register(null));
            Assert.All(adding.Said.Concat(removing.Said), said =>
            {
                Assert.DoesNotContain("secret", said);
                Assert.DoesNotContain("abc123", said);
                Assert.DoesNotContain("s3cret", said);
            });
        }
    }

    [Fact]
    public void Remove_takes_out_cluewrens_own_entry()
    {
        var claude = new Claude(Shows(User, @"C:\Tools\cluewren.exe", "mcp"));
        Assert.True(claude.Register(null));
        Assert.Equal([Getting, Removing], claude.Calls);
        var bare = new Claude(Shows(User, "cluewren", "mcp"));
        Assert.True(bare.Register(null));
        Assert.Equal([Getting, Removing], bare.Calls);
    }

    [Fact]
    public void Remove_with_nothing_there_does_nothing()
    {
        var claude = new Claude((1, Absent));
        Assert.True(claude.Register(null));
        Assert.Equal([Getting], claude.Calls);
    }

    public static TheoryData<string, string, string, string[]> NotRemovable => new()
    {
        { User, @"C:\Tools\cluewren.exe", "mcp", ["CLUEWREN_SANDBOX=required"] },
        { User, @"C:\Program Files\dotnet\dotnet.exe", @"C:\src\bin\cluewren.dll mcp", [] },
        { User, @"C:\tools\cluewren.cmd", "mcp", [] },
        { User, @"C:\tools\cluewren.exe", "serve", [] },
        { User, @"C:\Windows\System32\cmd.exe", "/c exit 0", [] },
        { "Local config (private to you in this project)", @"C:\Tools\cluewren.exe", "mcp", [] },
    };

    // Regression: entries with settings of their own, a .cmd wrapper and the dll form were removed as Cluewren's.
    [Theory]
    [MemberData(nameof(NotRemovable))]
    public void Remove_leaves_an_entry_it_cannot_prove_is_cluewrens_alone(string scope, string command, string args, string[] env)
    {
        var claude = new Claude(Shows(scope, command, args, env));
        Assert.False(claude.Register(null));
        Assert.Equal([Getting], claude.Calls);
        Assert.Contains("claude mcp remove --scope user cluewren", claude.Said[^1]);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    [InlineData(null, false)]
    public void A_remove_that_reports_failure_is_followed_by_one_more_read(int? second, bool gone)
    {
        (int?, string) after = second switch { 1 => (1, Absent), 0 => Shows(User, @"C:\Tools\cluewren.exe", "mcp"), _ => (null, "timed out") };
        var claude = new Claude(Shows(User, @"C:\Tools\cluewren.exe", "mcp"), after) { Removing = (1, "boom") };
        Assert.Equal(gone, claude.Register(null));
        Assert.Equal([Getting, Removing, Getting], claude.Calls);
        Assert.Contains(second switch { 1 => "has no cluewren MCP server now", 0 => "still has its cluewren MCP server", _ => "couldn't tell" },
            claude.Said[^1]);
    }

    [Fact]
    public void A_failed_add_is_reported()
    {
        var claude = new Claude((1, Absent)) { Adding = (1, "MCP server cluewren already exists in user config") };
        Assert.False(claude.Register(Command));
        Assert.Equal([Getting, Adding], claude.Calls);
        Assert.Contains("already exists", claude.Said[^1]);
    }
}

public class ToolAnnotationTests
{
    [Fact]
    public void Each_tool_has_a_title_and_says_whether_it_acts_on_the_game_or_the_outside_world()
    {
        var expected = new Dictionary<string, (bool ReadOnly, bool Destructive, bool OpenWorld)>
        {
            ["start_run"] = (false, true, true), ["wait"] = (true, false, false), ["probe"] = (false, true, true),
            ["use_harness"] = (false, true, false), ["check_harness"] = (false, true, true), ["run_harness"] = (false, true, true),
            ["snapshot"] = (false, true, false), ["restart_level"] = (false, true, false), ["frames"] = (true, false, false),
            ["report"] = (false, true, false),
        };
        var tools = McpServer.ToolList().Select(tool => tool!.AsObject()).ToList();
        Assert.Equal(expected.Keys.Order(), tools.Select(tool => tool["name"]!.GetValue<string>()).Order());
        foreach (var tool in tools)
        {
            var name = tool["name"]!.GetValue<string>();
            var hints = tool["annotations"]!.AsObject();
            Assert.False(string.IsNullOrWhiteSpace(tool["title"]?.GetValue<string>()), name);
            Assert.Equal(tool["title"]!.GetValue<string>(), hints["title"]?.GetValue<string>());
            Assert.Equal(expected[name], (hints["readOnlyHint"]!.GetValue<bool>(), hints["destructiveHint"]!.GetValue<bool>(),
                hints["openWorldHint"]!.GetValue<bool>()));
        }
    }
}
