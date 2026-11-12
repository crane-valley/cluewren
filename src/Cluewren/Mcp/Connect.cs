using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cluewren.Mcp;

public sealed partial class Connect(Action<string> say, Func<string[], (int? Code, string Output)>? runner = null)
{
    const string Name = "cluewren";
    const string Mark = "cluewren connect";
    const string ClaudeDescriber = "frame-describer";
    const string CodexDescriber = "frame_describer";
    const string DescriberAbout = "Describes frames of a Cluewren episode (the cluewren `frames` tool) for an agent checking how "
        + "something in a game looks. Give it only the episode, the game times and open questions: never the claim, the request or what you "
        + "expect to see.";
    const string DescriberHow = "You are given an episode, game times and open questions. Call the cluewren `frames` tool with that "
        + "episode, start, end and count, then describe the frames you get, answering each question by its number from the frames alone. If "
        + "`frames` gives no pictures, say what it said instead and stop.";
    const string DescriberSystem = """
        You describe frames of a video game for someone who can't see them. You are not told what they are
        checking, and you don't guess it: you say what is visible, as exactly as you can.

        - Answer each question by its number, then the last one.
        - Motion: name what moves from frame to frame (which parts of a body or thing, which way, through what arc, from
          which frame to which), what everyday action it looks most like, and what stays still.
        - Effects, lights and materials: their colour, shape, size and place on screen, and the frames they show in.
        - Text and icons: quote text exactly where it is readable; where it isn't, say "unreadable", never a guess.
        - Say what you can't make out (too small, too dark, blurred, covered, off the edge). Say so when a frame is nearly
          black or blank.
        - Never judge: don't say whether anything is correct, working, broken, intended or a bug. Describe it.
        - At most 400 words.

        Last, after the questions: anything in these frames that looks unusual for a game's picture (a thing passing through
        another, a surface missing, stretched or flickering, a body in a pose a body can't take, something frozen while the
        rest moves, text cut off or overlapping): say what and in which frames; or say nothing like that shows.
        """;

    static readonly UTF8Encoding Utf8 = new(false);

    const string ApprovalLine = "default_tools_approval_mode = \"approve\"";
    const string ApprovalMark = $"# {Mark} --approve-tools: Codex calls these tools without asking you first";
    const string OldApprovalMark = "# (its tools reach only the game open in the editor and the game's memory folder)";
    static readonly string[] Owned = ["command", "args", "default_tools_approval_mode"];

    [GeneratedRegex("^    [A-Za-z_][A-Za-z0-9_]*=")]
    private static partial Regex EnvSetting();

    public static string ClaudeHome() => Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")?.Trim() is { Length: > 0 } home ? home
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    public static string CodexHome() => Environment.GetEnvironmentVariable("CODEX_HOME")?.Trim() is { Length: > 0 } home ? home
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    static string AgentsHome() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agents");

    public static string[] ServerCommand()
    {
        var process = Environment.ProcessPath ?? "dotnet";
        return Path.GetFileNameWithoutExtension(process) == Name ? [process, "mcp"]
            : [process, Assembly.GetEntryAssembly()?.Location ?? "", "mcp"];
    }

    const int CommandSeconds = 120;

    // cmd.exe parses its command line again whatever the quoting: % and ! expand, and & | < > ^ ( ) act as operators.
    static readonly char[] CmdSpecial = ['&', '|', '<', '>', '^', '%', '!', '"', '(', ')', '\r', '\n', '\0'];

    static readonly string[] Runnable = [".COM", ".EXE", ".BAT", ".CMD"];

    // Only full PATH entries: CreateProcess would try the current directory first, where a project could plant a claude.exe.
    static string? OnPath(string name)
    {
        var extensions = !OperatingSystem.IsWindows() ? [""]
            : (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(extension => Runnable.Contains(extension, StringComparer.OrdinalIgnoreCase)).ToArray();
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var folder = entry.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(folder))
                continue;
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(folder, name + extension);
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        return null;
    }

    static string Quoted(string arg) =>
        arg.Length > 0 && !arg.Any(c => char.IsWhiteSpace(c) || c is ',' or ';' or '=') && arg.IndexOfAny(CmdSpecial) < 0 ? arg
            : $"\"{arg}{new string('\\', arg.Length - arg.TrimEnd('\\').Length)}\"";

    static string Shown(IEnumerable<string> command) => string.Join(' ', command.Select(Quoted));

    string? Refusal(string[] command)
    {
        if (runner is not null)
            return null;
        if (OnPath(command[0]) is not { } program)
            return $"Claude Code's `{command[0]}` command isn't on PATH";
        return Path.GetExtension(program).ToUpperInvariant() is ".CMD" or ".BAT" && command.Skip(1).Prepend(program).Any(arg => arg.IndexOfAny(CmdSpecial) >= 0)
            ? $"{program} runs through cmd.exe, which can't be given these arguments safely" : null;
    }

    (int? Code, string Output) Run(string[] command)
    {
        if (runner is not null)
            return runner(command);
        if (Refusal(command) is { } refused)
            return (null, refused);
        var program = OnPath(command[0])!;
        ProcessStartInfo info;
        if (Path.GetExtension(program).ToUpperInvariant() is ".CMD" or ".BAT")
        {
            string[] line = [program, .. command.Skip(1)];
            info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe")) { Arguments = $"/d /s /c \"{Shown(line)}\"" };
        }
        else
        {
            info = new ProcessStartInfo(program);
            foreach (var arg in command.Skip(1))
                info.ArgumentList.Add(arg);
        }
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.UseShellExecute = false;
        try
        {
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (Task.WhenAll(output, error, process.WaitForExitAsync()).Wait(TimeSpan.FromSeconds(CommandSeconds)))
                return (process.ExitCode, output.Result + error.Result);
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
            }
            return (null, $"`{Shown(command)}` didn't finish within {CommandSeconds} s and was stopped");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (null, e.Message);
        }
    }

    public static string SkillText() => $"{PlaytestRun.SkillText().TrimEnd('\n', '\r')}\n\n<!-- {Mark}: Cluewren's skill -->\n";

    public static string DescriberInstructions() => $"{DescriberSystem}\n\n{DescriberHow}";

    public static string ClaudeDescriberText() =>
        $"---\nname: {ClaudeDescriber}\ndescription: {JsonSerializer.Serialize(DescriberAbout)}\ntools: mcp__{Name}__frames\nmodel: opus\n---\n\n"
        + $"{DescriberInstructions()}\n\n<!-- {Mark}: Cluewren's frame describer -->\n";

    public static string CodexDescriberText() =>
        $"# {Mark}: Cluewren's frame describer\nname = {JsonSerializer.Serialize(CodexDescriber)}\n"
        + $"description = {JsonSerializer.Serialize(DescriberAbout)}\ndeveloper_instructions = '''\n{DescriberInstructions()}\n'''\n";

    static string CommandLine(string[] command) => $"command = {JsonSerializer.Serialize(command[0])}";

    static string ArgsLine(string[] command) => $"args = {JsonSerializer.Serialize(command[1..])}";

    static string CodexTable(string[] command, bool approve) =>
        $"# {Mark}: Cluewren's MCP server, for the game open in your editor (`cluewren connect codex --remove` takes it out)\n"
        + $"[mcp_servers.\"{Name}\"]\n{CommandLine(command)}\n{ArgsLine(command)}\n" + (approve ? $"{ApprovalMark}\n{ApprovalLine}\n" : "");

    enum Kind { Blank, Comment, Header, Key, Inside, Unread }

    readonly record struct TomlLine(string Text, Kind Kind, string[]? Path, bool ArrayTable, bool Open);

    static string Plain(TomlLine line) => line.Text.TrimEnd('\r');

    static bool InTable(string[] path) => path is ["mcp_servers", Name, ..];

    static void SkipSpace(string line, ref int at)
    {
        while (at < line.Length && line[at] is ' ' or '\t')
            at++;
    }

    static bool Ends(string line, int at)
    {
        SkipSpace(line, ref at);
        return at == line.Length || line[at] == '#';
    }

    static string[]? KeyPath(string line, ref int at)
    {
        var parts = new List<string>();
        while (true)
        {
            SkipSpace(line, ref at);
            if (at == line.Length)
                return null;
            if (line[at] is '"' or '\'')
            {
                var end = line.IndexOf(line[at], at + 1);
                if (end < 0 || line[at] == '"' && line.AsSpan(at, end - at).Contains('\\'))
                    return null;
                parts.Add(line[(at + 1)..end]);
                at = end + 1;
            }
            else
            {
                var start = at;
                while (at < line.Length && (char.IsAsciiLetterOrDigit(line[at]) || line[at] is '_' or '-'))
                    at++;
                if (at == start)
                    return null;
                parts.Add(line[start..at]);
            }
            SkipSpace(line, ref at);
            if (at == line.Length || line[at] != '.')
                return [.. parts];
            at++;
        }
    }

    static int AfterQuote(string line, int at, char quote)
    {
        for (; at < line.Length; at++)
        {
            if (quote == '"' && line[at] == '\\')
                at++;
            else if (line[at] == quote)
                return at + 1;
        }
        return -1;
    }

    static int AfterTriple(string line, int at, char quote)
    {
        for (; at + 2 < line.Length; at++)
        {
            if (quote == '"' && line[at] == '\\')
                at++;
            else if (line[at] == quote && line[at + 1] == quote && line[at + 2] == quote)
            {
                var end = at + 3;
                while (end < line.Length && end < at + 5 && line[end] == quote)
                    end++;
                return end;
            }
        }
        return -1;
    }

    static bool Value(string line, int at, ref char? open, ref int depth)
    {
        while (at < line.Length)
        {
            if (open is { } quote)
            {
                var end = AfterTriple(line, at, quote);
                if (end < 0)
                    return true;
                open = null;
                at = end;
                continue;
            }
            var c = line[at];
            if (c == '#')
                return true;
            if (c is '"' or '\'')
            {
                if (at + 2 < line.Length && line[at + 1] == c && line[at + 2] == c)
                {
                    open = c;
                    at += 3;
                    continue;
                }
                at = AfterQuote(line, at + 1, c);
                if (at < 0)
                    return false;
                continue;
            }
            if (c is '[' or '{')
                depth++;
            else if ((c is ']' or '}') && --depth < 0)
            {
                depth = 0;
                return false;
            }
            at++;
        }
        return true;
    }

    // Line by line, knowing which lines start inside a multi-line string or array: a "[" there is no table header.
    static List<TomlLine> Scan(string text)
    {
        var lines = new List<TomlLine>();
        char? open = null;
        var depth = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var at = 0;
            string[]? path = null;
            Kind kind;
            if (open is not null || depth > 0)
                kind = Kind.Inside;
            else
            {
                SkipSpace(line, ref at);
                if (at == line.Length || line[at] == '#')
                {
                    lines.Add(new(raw, at == line.Length ? Kind.Blank : Kind.Comment, null, false, false));
                    continue;
                }
                if (line[at] == '[')
                {
                    var arrayTable = at + 1 < line.Length && line[at + 1] == '[';
                    at += arrayTable ? 2 : 1;
                    path = KeyPath(line, ref at);
                    var close = arrayTable ? "]]" : "]";
                    if (path is null || !line.AsSpan(at).StartsWith(close) || !Ends(line, at + close.Length))
                        path = null;
                    lines.Add(new(raw, Kind.Header, path, arrayTable, false));
                    continue;
                }
                path = KeyPath(line, ref at);
                if (path is null || at == line.Length || line[at] != '=')
                {
                    lines.Add(new(raw, Kind.Unread, null, false, false));
                    continue;
                }
                kind = Kind.Key;
                at++;
            }
            if (!Value(line, at, ref open, ref depth))
                kind = Kind.Unread;
            lines.Add(new(raw, kind, kind == Kind.Key ? path : null, false, open is not null || depth > 0));
        }
        return lines;
    }

    static int? ServerTable(List<TomlLine> lines)
    {
        int? main = null;
        string[]? table = [];
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var named = line.Text.Contains(Name, StringComparison.OrdinalIgnoreCase);
            if (line.Kind == Kind.Header)
            {
                table = line.Path;
                if (table is null && named)
                    throw new FormatException($"a table header naming the {Name} server there isn't one this editor reads");
                if (table is null || !InTable(table))
                    continue;
                if (line.ArrayTable)
                    throw new FormatException($"the {Name} server is written there as an array of tables");
                if (table.Length > 2)
                    continue;
                if (main is not null)
                    throw new FormatException($"the {Name} server's table is there twice");
                main = i;
            }
            else if (line.Kind == Kind.Key && table is not null)
            {
                string[] full = [.. table, .. line.Path!];
                if (!InTable(table) && InTable(full))
                    throw new FormatException($"the {Name} server is set up there by a dotted key");
                if (full is ["mcp_servers"])
                    throw new FormatException("mcp_servers is written there as one inline table");
            }
            else if (line.Kind is Kind.Unread or Kind.Key && named)
                throw new FormatException($"a line naming the {Name} server there isn't one this editor reads");
        }
        if (lines[^1].Open)
            throw new FormatException("a value in it never ends");
        return main;
    }

    static string Without(List<TomlLine> lines)
    {
        var kept = new List<string>();
        var skipping = false;
        foreach (var line in lines)
        {
            if (line.Kind == Kind.Header)
            {
                skipping = line.Path is { } path && InTable(path);
                while (skipping && kept.Count > 0 && kept[^1].TrimStart().StartsWith('#') && kept[^1].Contains(Mark, StringComparison.Ordinal))
                    kept.RemoveAt(kept.Count - 1);
            }
            if (!skipping)
                kept.Add(line.Text);
        }
        var text = string.Join('\n', kept).TrimEnd('\n', '\r');
        return text.Length > 0 ? text + "\n" : "";
    }

    public static string CodexConfig(string text, string[]? command, bool approve = false)
    {
        var lines = Scan(text);
        var main = ServerTable(lines);
        if (command is null)
            return lines.Any(line => line.Kind == Kind.Header && line.Path is { } path && InTable(path)) ? Without(lines) : text;
        if (main is not { } header)
        {
            var kept = text.TrimEnd('\n', '\r');
            return (kept.Length > 0 ? kept + "\n\n" : "") + CodexTable(command, approve);
        }
        var end = lines.FindIndex(header + 1, line => line.Kind == Kind.Header);
        var found = new int?[Owned.Length];
        for (var i = header + 1; i < (end < 0 ? lines.Count : end); i++)
        {
            var line = lines[i];
            if (line.Kind == Kind.Unread)
                throw new FormatException($"a line in the {Name} server's table isn't one this editor reads");
            if (line.Kind != Kind.Key || Array.IndexOf(Owned, line.Path![0]) is not (>= 0 and var key))
                continue;
            if (line.Path.Length > 1 || line.Open || found[key] is not null)
                throw new FormatException($"the {Name} server's {Owned[key]} is written there in a way this editor leaves alone");
            found[key] = i;
        }
        var cr = lines[header].Text.EndsWith('\r') ? "\r" : "";
        var approval = found[2];
        var markedAbove = approval is { } a && Plain(lines[a - 1]) is ApprovalMark or OldApprovalMark;
        var cluewrens = markedAbove && Plain(lines[approval!.Value]) == ApprovalLine;
        var output = new List<string>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (i == found[0])
                output.Add(CommandLine(command) + cr);
            else if (i == found[1])
                output.Add(ArgsLine(command) + cr);
            else if (cluewrens && !approve && (i == approval || i == approval - 1))
                continue;
            else if (i == approval && approve && !cluewrens)
            {
                if (!markedAbove)
                    output.Add(ApprovalMark + cr);
                output.Add(ApprovalLine + cr);
            }
            else
                output.Add(lines[i].Text);
            if (i != header)
                continue;
            if (found[0] is null)
                output.Add(CommandLine(command) + cr);
            if (found[1] is null)
                output.Add(ArgsLine(command) + cr);
            if (approve && approval is null)
                output.AddRange([ApprovalMark + cr, ApprovalLine + cr]);
        }
        return string.Join('\n', output);
    }

    bool EditCodexConfig(string[]? command, bool approve)
    {
        var path = Path.Combine(CodexHome(), "config.toml");
        var text = File.Exists(path) ? File.ReadAllText(path) : "";
        string changed;
        try
        {
            changed = CodexConfig(text, command, approve);
        }
        catch (FormatException e)
        {
            say($"Left {path} as it is: {e.Message}. " + (command is null ? $"Remove the {Name} server by hand: codex mcp remove {Name}"
                : $"Set the {Name} server up there by hand: {CommandLine(command)}, {ArgsLine(command)}{(approve ? $", {ApprovalLine}" : "")}"));
            return false;
        }
        if (changed == text)
            return true;
        if (text.Length > 0)
            File.Copy(path, path + ".before-cluewren", overwrite: true);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, changed, Utf8);
        say($"{(command is null ? "Removed" : "Set up")} the {Name} MCP server in {path}"
            + (approve ? ": Codex calls its tools without asking you first" : ""));
        return true;
    }

    static bool Ours(string path)
    {
        try
        {
            return File.ReadAllText(path).Contains(Mark, StringComparison.Ordinal);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return true;
        }
    }

    bool Put(string path, string text)
    {
        if (!Ours(path))
        {
            say($"Left {path} as it is: it isn't Cluewren's (rename it to have Cluewren's written there)");
            return false;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, Utf8);
        say($"Wrote {path}");
        return true;
    }

    void Drop(string path)
    {
        if (!File.Exists(path) || !Ours(path))
            return;
        File.Delete(path);
        say($"Removed {path}");
        var folder = Path.GetDirectoryName(path)!;
        if (!Directory.EnumerateFileSystemEntries(folder).Any())
            Directory.Delete(folder);
    }

    sealed record ClaudeEntry(string Scope, string Type, string Command, string Args, string[]? Env);

    // Env is null when `claude mcp get` doesn't show the settings so that they can be read back exactly: a value over several
    // lines, or anything after them but the closing hint.
    static ClaudeEntry? Entry(string output)
    {
        var lines = output.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        var labels = new Dictionary<string, string>();
        string[]? env = null;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i] == "  Environment:")
            {
                if (!labels.TryAdd("Environment", ""))
                    return null;
                var settings = new List<string>();
                var readable = true;
                for (; i + 1 < lines.Length && lines[i + 1].Length > 0; i++)
                    if (EnvSetting().IsMatch(lines[i + 1]))
                        settings.Add(lines[i + 1][4..]);
                    else
                        readable = false;
                if (readable && lines.Skip(i + 1).All(line => line.Length == 0 || line.StartsWith("To remove this server", StringComparison.Ordinal)))
                    env = [.. settings];
                continue;
            }
            foreach (var label in (string[])["Scope", "Type", "Command", "Args"])
                if ((lines[i] == $"  {label}:" || lines[i].StartsWith($"  {label}: ", StringComparison.Ordinal))
                    && !labels.TryAdd(label, lines[i].Length > label.Length + 4 ? lines[i][(label.Length + 4)..] : ""))
                    return null;
        }
        return labels.TryGetValue("Scope", out var scope) && labels.TryGetValue("Type", out var type)
            && labels.TryGetValue("Command", out var command) && labels.TryGetValue("Args", out var args)
            ? new(scope, type, command, args, env) : null;
    }

    // Cluewren's entry is one that runs what ServerCommand gives for some install: cluewren(.exe) mcp, or a host with Cluewren.dll mcp.
    static string[]? CluewrenCommand(ClaudeEntry entry)
    {
        if (entry.Type != "stdio")
            return null;
        if (Path.GetFileNameWithoutExtension(entry.Command).Equals(Name, StringComparison.OrdinalIgnoreCase) && entry.Args == "mcp")
            return [entry.Command, "mcp"];
        if (entry.Args.EndsWith(" mcp", StringComparison.Ordinal)
            && Path.GetFileName(entry.Args[..^4]).Equals("Cluewren.dll", StringComparison.OrdinalIgnoreCase))
            return [entry.Command, entry.Args[..^4], "mcp"];
        return null;
    }

    static string[] Adding(string[] command, string[] env) =>
        ["claude", "mcp", "add", "--scope", "user", Name, .. env.SelectMany(setting => (string[])["-e", setting]), "--", .. command];

    // Values are left out: an MCP server's environment often holds a key, and this goes to the terminal while the entry still has them.
    static string ByHand(string[] command, string[]? env) =>
        Shown(["claude", "mcp", "add", "--scope", "user", Name])
        + (env is null ? " -e NAME=<value> (for each of its environment settings)"
            : string.Concat(env.Select(setting => $" -e {setting[..setting.IndexOf('=')]}=<value>")))
        + $" -- {Shown(command)}";

    public bool ClaudeServer(string[]? command)
    {
        string[] remove = ["claude", "mcp", "remove", "--scope", "user", Name];
        var (code, output) = Run(["claude", "mcp", "get", Name]);
        if (code is null)
        {
            say($"{output}. " + (command is null ? $"Remove the server by hand: {Shown(remove)}" : $"Add the server by hand: {ByHand(command, [])}"));
            return false;
        }
        bool Removed()
        {
            var (done, said) = Run(remove);
            if (done != 0)
                say($"`{Shown(remove)}` failed: {said.Trim()}");
            return done == 0;
        }
        if (code != 0)
        {
            if (command is null)
                return true;
            (code, output) = Run(Adding(command, []));
            if (code != 0)
            {
                say(code is null ? $"{output}. Add the server by hand: {ByHand(command, [])}" : $"`claude mcp add` failed: {output.Trim()}");
                return false;
            }
            say($"Added the {Name} MCP server to Claude Code (user scope: every project)");
            return true;
        }
        var entry = Entry(output);
        if (entry is null || !entry.Scope.StartsWith("User config", StringComparison.Ordinal))
        {
            say($"Left Claude Code's {Name} MCP server as it is: "
                + (entry is null ? $"what `claude mcp get {Name}` shows isn't something this reads" : $"the one Claude Code shows here is from its {entry.Scope}")
                + $". By hand: {Shown(remove)}" + (command is null ? "" : $" (if there is one in user scope), then {ByHand(command, null)}"));
            return false;
        }
        if (CluewrenCommand(entry) is not { } old)
        {
            say($"Left Claude Code's {Name} MCP server as it is: it isn't Cluewren's (it runs {entry.Command})");
            return false;
        }
        if (command is null)
        {
            if (!Removed())
                return false;
            say($"Removed the {Name} MCP server from Claude Code");
            return true;
        }
        if (entry.Command == command[0] && entry.Args == string.Join(' ', command[1..]))
        {
            say($"Claude Code's {Name} MCP server already runs this Cluewren");
            return true;
        }
        var adding = Adding(command, entry.Env ?? []);
        var restoring = Adding(old, entry.Env ?? []);
        if ((entry.Env is null ? "its environment settings can't be read back exactly" : Refusal(remove) ?? Refusal(adding) ?? Refusal(restoring))
            is { } why)
        {
            say($"Left Claude Code's {Name} MCP server as it is (it runs {Shown(old)}): {why}. To run this Cluewren instead: {Shown(remove)}, "
                + $"then {ByHand(command, entry.Env)}");
            return false;
        }
        if (!Removed())
            return false;
        (code, output) = Run(adding);
        if (code == 0)
        {
            say($"Claude Code's {Name} MCP server ran {Shown(old)}; it runs this Cluewren now, with the same environment settings");
            return true;
        }
        var (back, again) = Run(restoring);
        // With the entry gone, this line is the only copy of its settings left, so it carries the values.
        say($"`claude mcp add` failed: {output.Trim()}. " + (back == 0 ? $"Claude Code's {Name} MCP server is back as it was ({Shown(old)})"
            : $"Putting it back failed too: {again.Trim()}. Add it back by hand: {Shown(restoring)}"));
        return false;
    }

    public int Add(string client, bool approveTools = false)
    {
        var command = ServerCommand();
        bool ok;
        if (client == "claude")
        {
            ok = ClaudeServer(command);
            ok &= Put(Path.Combine(ClaudeHome(), "skills", Name, "SKILL.md"), SkillText());
            ok &= Put(Path.Combine(ClaudeHome(), "agents", $"{ClaudeDescriber}.md"), ClaudeDescriberText());
        }
        else
        {
            ok = EditCodexConfig(command, approveTools);
            ok &= Put(Path.Combine(AgentsHome(), "skills", Name, "SKILL.md"), SkillText());
            ok &= Put(Path.Combine(CodexHome(), "agents", $"{CodexDescriber}.toml"), CodexDescriberText());
        }
        var what = client == "claude" ? "Claude Code" : "Codex";
        say($"{what} is set up for Cluewren{(ok ? "" : ", but for what is said above")}. Open the game's project in its editor, start a new "
            + $"{what} session, and ask it, e.g.: \"Use Cluewren to verify that the door opens with the red key.\"");
        return ok ? 0 : 3;
    }

    public int Remove(string client)
    {
        bool ok;
        if (client == "claude")
        {
            ok = ClaudeServer(null);
            Drop(Path.Combine(ClaudeHome(), "skills", Name, "SKILL.md"));
            Drop(Path.Combine(ClaudeHome(), "agents", $"{ClaudeDescriber}.md"));
        }
        else
        {
            ok = EditCodexConfig(null, false);
            Drop(Path.Combine(AgentsHome(), "skills", Name, "SKILL.md"));
            Drop(Path.Combine(CodexHome(), "agents", $"{CodexDescriber}.toml"));
        }
        return ok ? 0 : 3;
    }
}
