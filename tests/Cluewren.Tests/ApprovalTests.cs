using System.Text.Json.Nodes;
using Cluewren.Mcp;

namespace Cluewren.Tests;

public class CodexApprovalTests
{
    static readonly string[] Command = ["C:/tools/cluewren.exe", "mcp"];

    static string Lines(params string[] lines) => string.Join('\n', lines) + "\n";

    const string Header = "# cluewren connect: Cluewren's MCP server, for the game open in your editor (`cluewren connect codex --remove` takes it out)";

    // Exactly what `cluewren connect codex` 0.1.0 appended.
    static readonly string Written010 = Lines(
        Header,
        "[mcp_servers.\"cluewren\"]",
        "command = \"C:/tools/cluewren.exe\"",
        "args = [\"mcp\"]",
        "# (its tools reach only the game open in the editor and the game's memory folder)",
        "default_tools_approval_mode = \"approve\"");

    [Fact]
    public void Codex_is_told_to_approve_the_tools_only_when_asked()
    {
        Assert.Equal(Lines(Header, "[mcp_servers.\"cluewren\"]", "command = \"C:/tools/cluewren.exe\"", "args = [\"mcp\"]"),
            Connect.CodexConfig("", Command));
        var approved = Connect.CodexConfig("", Command, approve: true);
        Assert.EndsWith("args = [\"mcp\"]\n# cluewren connect --approve-tools: Codex calls these tools without asking you first\n"
            + "default_tools_approval_mode = \"approve\"\n", approved);
        Assert.Equal(approved, Connect.CodexConfig(approved, Command, approve: true));
    }

    [Fact]
    public void A_plain_rerun_takes_back_the_approval_cluewren_wrote_and_nothing_else()
    {
        Assert.Equal(Lines(Header, "[mcp_servers.\"cluewren\"]", "command = \"C:/tools/cluewren.exe\"", "args = [\"mcp\"]"),
            Connect.CodexConfig(Written010, Command));
        var approved = Connect.CodexConfig("model = \"gpt\"\n", Command, approve: true);
        Assert.Equal(Connect.CodexConfig("model = \"gpt\"\n", Command), Connect.CodexConfig(approved, Command));
    }

    [Fact]
    public void An_approval_line_cluewren_did_not_write_is_kept()
    {
        var mine = Lines("[mcp_servers.\"cluewren\"]", "command = \"C:/tools/cluewren.exe\"", "args = [\"mcp\"]",
            "default_tools_approval_mode = \"approve\"");
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
            "[mcp_servers.\"cluewren\"]",
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
    public void A_line_inside_a_multi_line_value_is_not_taken_for_a_table()
    {
        var notes = Lines("notes = \"\"\"", "[mcp_servers.\"cluewren\"]", "command = \"mine\"", "\"\"\"", "list = [", "  [1, 2],", "]");
        Assert.Equal(notes + "\n" + Connect.CodexConfig("", Command), Connect.CodexConfig(notes, Command));
        Assert.Equal(notes, Connect.CodexConfig(notes, null));
    }

    [Theory]
    [InlineData("[mcp_servers.cluewren]\ncommand = \"x\"\n[mcp_servers.\"cluewren\"]\nargs = []\n")]
    [InlineData("[mcp_servers]\ncluewren = { command = \"x\" }\n")]
    [InlineData("mcp_servers = { cluewren = { command = \"x\" } }\n")]
    [InlineData("[[mcp_servers.cluewren]]\ncommand = \"x\"\n")]
    [InlineData("[mcp_servers.\"cluewren\\u0020\"]\ncommand = \"x\"\n")]
    [InlineData("[mcp_servers.cluewren]\ncommand = \"x\"\nnotes = '''\nnever closed\n")]
    public void A_shape_this_editor_cannot_read_is_left_to_the_user(string text)
    {
        Assert.Throws<FormatException>(() => Connect.CodexConfig(text, Command));
        Assert.Throws<FormatException>(() => Connect.CodexConfig(text, Command, approve: true));
        Assert.Throws<FormatException>(() => Connect.CodexConfig(text, null));
    }

    [Theory]
    [InlineData("[mcp_servers.cluewren]\ncommand = \"x\"\nargs = [\n  \"mcp\",\n]\n")]
    [InlineData("[mcp_servers.cluewren]\ncommand = \"\"\"\nx\"\"\"\n")]
    [InlineData("[mcp_servers.cluewren]\ncommand = \"x\"\ncommand = \"y\"\n")]
    [InlineData("[mcp_servers.cluewren]\ncommand = \"x\"\nthis line = isn't toml\n")]
    [InlineData("[mcp_servers.cluewren]\ncommand.path = \"x\"\n")]
    public void Command_or_args_this_editor_cannot_rewrite_are_left_to_the_user(string text)
    {
        Assert.Throws<FormatException>(() => Connect.CodexConfig(text, Command));
        Assert.Throws<FormatException>(() => Connect.CodexConfig(text.Replace("\"x\"", "\"C:/tools/cluewren.exe\""), Command));
    }

    [Theory]
    [InlineData("[mcp_servers.\"cluewren\"]\nurl = \"https://example.com/mcp\"\n")]
    [InlineData("[mcp_servers.\"cluewren\"]\ncommand = \"C:/tools/cluewren.exe\"\nargs = [\"mcp\"]\n[mcp_servers.cluewren.http_headers]\nX = \"y\"\n")]
    [InlineData("[mcp_servers.\"cluewren\"]\ncommand = \"node\"\nargs = [\"C:/srv/cluewren.js\"]\n")]
    [InlineData("[mcp_servers.\"cluewren\"]\ncommand = \"C:/tools/cluewren.exe\"\nargs = [\"serve\"]\n")]
    [InlineData("[mcp_servers.\"cluewren\"]\ncommand = \"C:/tools/cluewren-helper.exe\"\nargs = [\"mcp\"]\n")]
    [InlineData("[mcp_servers.\"cluewren\"]\ncommand = \"C:/tools/cluewren.exe\"\nargs = [\n  \"mcp\",\n]\n")]
    [InlineData("[mcp_servers.\"cluewren\"]\nenabled = false\n")]
    [InlineData("[mcp_servers.cluewren.env]\nKEY = \"v\"\n")]
    public void Remove_leaves_a_server_that_is_not_cluewrens(string text)
    {
        Assert.Throws<Connect.NotCluewrensException>(() => Connect.CodexConfig(text, null));
    }

    [Theory]
    [InlineData("[mcp_servers.\"cluewren\"]\ncommand = \"node\"\nargs = [\"C:/srv/cluewren.js\"]\n")]
    [InlineData("[mcp_servers.\"cluewren\"]\ncommand = \"C:/tools/cluewren.exe\"\nargs = [\"serve\"]\n")]
    [InlineData("[mcp_servers.\"cluewren\"]\ncommand = \"C:/tools/cluewren-helper.exe\"\nargs = [\"mcp\"]\n")]
    [InlineData("[mcp_servers.\"cluewren\"]\ncommand = \"C:/tools/cluewren.exe\"\n")]
    [InlineData("[mcp_servers.\"cluewren\"]\ncommand = \"C:/tools/cluewren.exe\"\nargs = ['mcp']\n")]
    public void Connect_leaves_a_users_own_server_named_cluewren(string text)
    {
        Assert.Throws<Connect.NotCluewrensException>(() => Connect.CodexConfig(text, Command));
        Assert.Throws<Connect.NotCluewrensException>(() => Connect.CodexConfig(text, Command, approve: true));
    }

    [Theory]
    [InlineData("command = \"C:/Program Files/dotnet/dotnet.exe\"\nargs = [\"C:/src/bin/cluewren.dll\", \"mcp\"]\n")]
    [InlineData("command = 'D:\\Other\\Cluewren.exe'\nargs = [\"mcp\"]\n")]
    public void Connect_rewrites_cluewrens_own_table_in_either_install_shape(string owned)
    {
        Assert.Equal(Lines("[mcp_servers.\"cluewren\"]", "command = \"C:/tools/cluewren.exe\"", "args = [\"mcp\"]", "cwd = \"C:/Games\""),
            Connect.CodexConfig("[mcp_servers.\"cluewren\"]\n" + owned + "cwd = \"C:/Games\"\n", Command));
    }

    [Theory]
    [InlineData("[mcp_servers.\"cluewren\"]\ncommand = 'C:\\tools\\Cluewren.exe'\nargs = [\"mcp\"]\n")]
    [InlineData("[mcp_servers.\"cluewren\"]\ncommand = \"C:/Program Files/dotnet/dotnet.exe\"\nargs = [\"C:/src/bin/cluewren.dll\", \"mcp\"]\n[mcp_servers.cluewren.env]\nCLUEWREN_SANDBOX = \"required\"\n")]
    public void Remove_takes_out_cluewrens_server_in_either_install_shape(string text)
    {
        Assert.Equal("model = \"m\"\n", Connect.CodexConfig("model = \"m\"\n\n" + text, null));
    }

    [Theory]
    [InlineData("[mcp_servers.\"cluewren\"]\nurl = \"https://example.com/mcp\"\n")]
    [InlineData("[mcp_servers.\"cluewren\"]\ncommand = \"x\"\nbearer_token_env_var = \"TOKEN\"\n")]
    [InlineData("[mcp_servers.\"cluewren\"]\ncommand = \"x\"\nenv_http_headers = { X = \"Y\" }\n")]
    [InlineData("[mcp_servers.\"cluewren\"]\ncommand = \"x\"\n[mcp_servers.\"cluewren\".http_headers]\nX = \"y\"\n")]
    [InlineData("[mcp_servers.cluewren.http_headers]\nX = \"y\"\n")]
    [InlineData("[mcp_servers.\"cluewren\"]\nenabled = false\n")]
    public void A_server_that_is_not_a_stdio_server_is_left_to_the_user(string text)
    {
        Assert.Throws<FormatException>(() => Connect.CodexConfig(text, Command));
        Assert.Throws<FormatException>(() => Connect.CodexConfig(text, Command, approve: true));
    }

    [Fact]
    public void Remove_takes_out_the_table_its_sub_tables_and_its_mark()
    {
        var text = Lines("model = \"gpt\"", "") + Written010
            + Lines("", "[mcp_servers.cluewren.env]", "CLUEWREN_SANDBOX = \"required\"", "", "[mcp_servers.other]", "command = \"x\"");
        Assert.Equal(Lines("model = \"gpt\"", "", "[mcp_servers.other]", "command = \"x\""), Connect.CodexConfig(text, null));
        Assert.Equal("model = \"gpt\"\n", Connect.CodexConfig("model = \"gpt\"\n", null));
    }
}

public class ClaudeRegistrationTests
{
    static readonly string[] Command = [@"C:\Users\me\.dotnet\tools\cluewren.exe", "mcp"];

    static string Get(string scope, string command, string args, params string[] env) =>
        string.Join('\n', new[]
        {
            "cluewren:", $"  Scope: {scope}", "  Status: \u00d7 Failed to connect", "  Issue: CONNECTION_CLOSED: Connection closed",
            "  Type: stdio", $"  Command: {command}", args.Length > 0 ? $"  Args: {args}" : "  Args:", "  Environment:",
        }.Concat(env.Select(setting => "    " + setting))
            .Concat(["", $"To remove this server, run: claude mcp remove cluewren -s {(scope.StartsWith("User") ? "user" : "local")}", ""]));

    const string User = "User config (available in all your projects)";

    internal static readonly string Saved = Path.Combine(Bridge.DiscoveryFile.DefaultFolder, "claude-cluewren-entry.json");

    // The file lives in this machine's real Cluewren folder: one a real connect left there is its user's only copy of those settings.
    internal sealed class UnlessSavedFactAttribute : FactAttribute
    {
        public UnlessSavedFactAttribute()
        {
            if (File.Exists(Saved))
                Skip = $"{Saved} is there already (left by a real connect); this test would write over it";
        }
    }

    sealed class Claude(string get, int getCode = 0, Func<string[], int>? add = null, string addSays = "")
    {
        public readonly List<string> Calls = [];
        public readonly List<string> Said = [];

        public (int? Code, string Output) Run(string[] command)
        {
            Calls.Add(string.Join('|', command));
            return command[2] switch
            {
                "get" => (getCode, get),
                "add" => (add?.Invoke(command) ?? 0, addSays),
                _ => (0, ""),
            };
        }

        public bool Register(string[]? command) => new Connect(Said.Add, Run).ClaudeServer(command);
    }

    const string Getting = "claude|mcp|get|cluewren";
    const string Removing = "claude|mcp|remove|--scope|user|cluewren";

    [Fact]
    public void The_same_command_already_registered_is_neither_removed_nor_added()
    {
        var claude = new Claude(Get(User, Command[0], "mcp", "CLUEWREN_SANDBOX=required"));
        Assert.True(claude.Register(Command));
        Assert.Equal([Getting], claude.Calls);
    }

    [UnlessSavedFact]
    public void An_older_cluewren_is_replaced_with_its_environment_carried_over()
    {
        var claude = new Claude(Get(User, @"C:\Tools\Clue wren\cluewren.exe", "mcp", "CLUEWREN_SANDBOX=required", @"PATHS=C:\x;D:\y", "EQ=x=y"));
        Assert.True(claude.Register(Command));
        Assert.Equal([Getting, Removing,
            $@"claude|mcp|add|--scope|user|cluewren|-e|CLUEWREN_SANDBOX=required|-e|PATHS=C:\x;D:\y|-e|EQ=x=y|--|{Command[0]}|mcp"], claude.Calls);
        Assert.DoesNotContain(claude.Said, said => said.Contains("required"));
        Assert.False(File.Exists(Saved));
    }

    [UnlessSavedFact]
    public void A_failed_add_puts_the_old_entry_back()
    {
        var claude = new Claude(Get(User, @"C:\Program Files\dotnet\dotnet.exe", @"C:\src\cluewren\bin\cluewren.dll mcp", "CLUEWREN_SANDBOX=required"),
            add: command => command.Contains(Command[0]) ? 1 : 0);
        Assert.False(claude.Register(Command));
        Assert.Equal([Getting, Removing,
            $"claude|mcp|add|--scope|user|cluewren|-e|CLUEWREN_SANDBOX=required|--|{Command[0]}|mcp",
            @"claude|mcp|add|--scope|user|cluewren|-e|CLUEWREN_SANDBOX=required|--|C:\Program Files\dotnet\dotnet.exe|C:\src\cluewren\bin\cluewren.dll|mcp"],
            claude.Calls);
        Assert.Contains("back as it was", claude.Said[^1]);
        Assert.False(File.Exists(Saved));
    }

    [UnlessSavedFact]
    public void An_earlier_saved_entry_is_never_written_over()
    {
        var there = File.Exists(Saved);
        try
        {
            if (!there)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Saved)!);
                File.WriteAllText(Saved, "earlier");
            }
            var claude = new Claude(Get(User, @"C:\Tools\cluewren.exe", "mcp", "KEY=v"));
            Assert.False(claude.Register(Command));
            Assert.Equal([Getting], claude.Calls);
            Assert.Contains(Saved, claude.Said[^1]);
            Assert.Equal("earlier", File.ReadAllText(Saved));
        }
        finally
        {
            if (!there)
                File.Delete(Saved);
        }
    }

    [UnlessSavedFact]
    public void No_environment_value_is_printed_and_one_that_could_not_be_put_back_is_kept_for_the_user_alone()
    {
        var there = File.Exists(Saved);
        try
        {
            var claude = new Claude(Get(User, @"C:\Tools\cluewren.exe", "mcp", "KEY=s3cret"), add: _ => 1,
                addSays: "Invalid environment variable format: KEY=s3cret");
            Assert.False(claude.Register(Command));
            Assert.Equal(4, claude.Calls.Count);
            Assert.DoesNotContain(claude.Said, said => said.Contains("s3cret"));
            Assert.Contains(Saved, claude.Said[^1]);
            Assert.Contains(@"-e KEY=<value> -- C:\Tools\cluewren.exe mcp", claude.Said[^1]);
            var saved = JsonNode.Parse(File.ReadAllText(Saved))!;
            Assert.Equal(@"C:\Tools\cluewren.exe", saved["command"]!.GetValue<string>());
            Assert.Equal("mcp", saved["args"]![0]!.GetValue<string>());
            Assert.Equal("s3cret", saved["env"]!["KEY"]!.GetValue<string>());
            if (OperatingSystem.IsWindows())
            {
                var rules = new FileInfo(Saved).GetAccessControl();
                Assert.True(rules.AreAccessRulesProtected);
            }
        }
        finally
        {
            if (!there)
                File.Delete(Saved);
        }
    }

    public static TheoryData<string> Unsafe => new()
    {
        Get(User, @"C:\Tools\cluewren.exe", "mcp", "CLUEWREN_SANDBOX=required", "TWO LINES=a").Replace("    TWO LINES=a", "b"),
        Get(User, @"C:\Tools\cluewren.exe", "mcp").Replace("  Environment:\n", ""),
        Get(User, @"C:\Tools\cluewren.exe", "mcp", "KEY=a") + "\n  Command: C:\\x.exe\n",
        Get(User, @"C:\Tools\cluewren.exe", "mcp", "KEY=a") + "Warning: something else\n",
        Get("Local config (private to you in this project)", @"C:\Tools\cluewren.exe", "mcp"),
        "something this version of claude prints instead\n",
        Get(User, @"C:\Tools\cluewren.exe", "mcp", "CLUEWREN_SANDBOX=${POLICY}"),
        Get(User, @"C:\Tools\cluewren.exe", "mcp", "NOTE=a b"),
        Get(User, @"C:\Tools\cluewren.exe", "mcp", "NAME=\u00fc"),
        Get(User, @"C:\Tools\cluewren.exe", "mcp", "LONG=" + new string('x', 601)),
    };

    [Theory]
    [MemberData(nameof(Unsafe))]
    public void An_entry_that_cannot_be_carried_over_is_not_removed_and_the_commands_are_printed(string get)
    {
        var claude = new Claude(get);
        Assert.False(claude.Register(Command));
        Assert.Equal([Getting], claude.Calls);
        Assert.Contains("claude mcp remove --scope user cluewren", claude.Said[^1]);
        Assert.Contains($"claude mcp add --scope user cluewren", claude.Said[^1]);
        Assert.Contains(Command[0], claude.Said[^1]);
        Assert.DoesNotContain("required", claude.Said[^1]);
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\cmd.exe", "/c exit 0")]
    [InlineData(@"C:\tools\cluewren-helper.exe", "mcp")]
    [InlineData(@"C:\tools\cluewren.exe", "serve")]
    public void A_users_own_server_named_cluewren_is_left_alone(string command, string args)
    {
        var claude = new Claude(Get(User, command, args));
        Assert.False(claude.Register(Command));
        Assert.False(claude.Register(null));
        Assert.Equal([Getting, Getting], claude.Calls);
    }

    [Fact]
    public void Remove_takes_out_only_cluewrens_entry()
    {
        var claude = new Claude(Get(User, @"C:\Tools\cluewren.exe", "mcp"));
        Assert.True(claude.Register(null));
        Assert.Equal([Getting, Removing], claude.Calls);
        var none = new Claude("No MCP server named \"cluewren\". Configured servers: other", getCode: 1);
        Assert.True(none.Register(null));
        Assert.Equal([Getting], none.Calls);
    }

    [Fact]
    public void A_server_not_registered_yet_is_added()
    {
        var claude = new Claude("No MCP server named \"cluewren\". Run `claude mcp add` to add one.", getCode: 1);
        Assert.True(claude.Register(Command));
        Assert.Equal([Getting, $"claude|mcp|add|--scope|user|cluewren|--|{Command[0]}|mcp"], claude.Calls);
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
