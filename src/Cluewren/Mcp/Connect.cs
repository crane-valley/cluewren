using System.Diagnostics;
using System.Globalization;
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
    static readonly string[] OwnedKeys = ["command", "args", "default_tools_approval_mode"];

    // Codex 0.167.2 refuses each of these next to command ("... is not supported for stdio").
    static readonly string[] HttpOnly = ["url", "bearer_token_env_var", "bearer_token", "http_headers", "env_http_headers", "oauth_resource"];

    public static string ClaudeHome() => Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")?.Trim() is { Length: > 0 } home ? home
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    public static string CodexHome() => Environment.GetEnvironmentVariable("CODEX_HOME")?.Trim() is { Length: > 0 } home ? home
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    static string AgentsHome() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agents");

    public static string[] ServerCommand()
    {
        var process = Environment.ProcessPath ?? "dotnet";
        return Path.GetFileNameWithoutExtension(process).Equals(Name, StringComparison.OrdinalIgnoreCase) ? [process, "mcp"]
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

    const string TableMark = $"# {Mark}: Cluewren's MCP server, for the game open in your editor (`cluewren connect codex --remove` takes it out)";

    static string CodexTable(string[] command, bool approve) =>
        $"{TableMark}\n[mcp_servers.\"{Name}\"]\n{CommandLine(command)}\n{ArgsLine(command)}\n" + (approve ? $"{ApprovalMark}\n{ApprovalLine}\n" : "");

    static string NewLine(string text) => text.IndexOf('\n') is > 0 and var at && text[at - 1] == '\r' ? "\r\n" : "\n";

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

    // Anything it can't read whole stops the edit: otherwise a spelling of the cluewren table it missed would be written twice.
    static (int? Main, bool Sub) Tables(List<TomlLine> lines)
    {
        int? main = null;
        var sub = false;
        string[] table = [];
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Kind == Kind.Unread)
                throw new FormatException($"line {i + 1} isn't one connect reads");
            if (line.Kind == Kind.Header)
            {
                if (line.Path is not { } path)
                    throw new FormatException($"the table header on line {i + 1} isn't one connect reads");
                if (line.ArrayTable && path[0] == "mcp_servers")
                    throw new FormatException($"line {i + 1} makes mcp_servers an array of tables");
                table = path;
                if (!InTable(table))
                    continue;
                if (table.Length > 2)
                    sub = true;
                else if (main is not null)
                    throw new FormatException($"the {Name} server's table is there twice");
                else
                    main = i;
            }
            else if (line.Kind == Kind.Key && (table is [] && line.Path![0] == "mcp_servers" || table is ["mcp_servers"]))
                throw new FormatException($"line {i + 1} sets mcp_servers by a key, not by a table header");
        }
        if (lines[^1].Open)
            throw new FormatException("a value in it never ends");
        return (main, sub);
    }

    public sealed class NotCluewrensException(string why) : Exception(why);

    static string? BasicOrLiteral(string line, ref int at)
    {
        if (at >= line.Length || line.AsSpan(at).StartsWith("'''") || line.AsSpan(at).StartsWith("\"\"\""))
            return null;
        if (line[at] == '\'')
        {
            var end = line.IndexOf('\'', at + 1);
            if (end < 0)
                return null;
            var literal = line[(at + 1)..end];
            at = end + 1;
            return literal;
        }
        if (line[at] != '"')
            return null;
        var text = new StringBuilder();
        for (var i = at + 1; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                at = i + 1;
                return text.ToString();
            }
            if (line[i] != '\\')
            {
                text.Append(line[i]);
                continue;
            }
            if (++i == line.Length)
                return null;
            switch (line[i])
            {
                case 'b': text.Append('\b'); break;
                case 't': text.Append('\t'); break;
                case 'n': text.Append('\n'); break;
                case 'f': text.Append('\f'); break;
                case 'r': text.Append('\r'); break;
                case '"': text.Append('"'); break;
                case '\\': text.Append('\\'); break;
                case 'u' or 'U':
                    var digits = line[i] == 'u' ? 4 : 8;
                    if (i + digits >= line.Length
                        || !int.TryParse(line.AsSpan(i + 1, digits), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code)
                        || code is < 0 or > 0x10FFFF or (>= 0xD800 and <= 0xDFFF))
                        return null;
                    text.Append(char.ConvertFromUtf32(code));
                    i += digits;
                    break;
                default:
                    return null;
            }
        }
        return null;
    }

    static string[]? StringArray(string line, ref int at)
    {
        if (at >= line.Length || line[at] != '[')
            return null;
        at++;
        var items = new List<string>();
        while (true)
        {
            SkipSpace(line, ref at);
            if (at < line.Length && line[at] == ']')
            {
                at++;
                return [.. items];
            }
            if (BasicOrLiteral(line, ref at) is not { } item)
                return null;
            items.Add(item);
            SkipSpace(line, ref at);
            if (at < line.Length && line[at] == ',')
                at++;
            else if (at < line.Length && line[at] == ']')
            {
                at++;
                return [.. items];
            }
            else
                return null;
        }
    }

    static int ValueStart(string line)
    {
        var at = 0;
        KeyPath(line, ref at);
        at++;
        SkipSpace(line, ref at);
        return at;
    }

    // Cluewren's server runs what ServerCommand gives for an install, and nothing else: a .cmd wrapper or another host is not it.
    static bool Cluewrens(string command, string[] args)
    {
        var file = Path.GetFileName(command);
        return args is ["mcp"] && (file.Equals(Name, StringComparison.OrdinalIgnoreCase) || file.Equals($"{Name}.exe", StringComparison.OrdinalIgnoreCase))
            || args is [var dll, "mcp"] && (file.Equals("dotnet", StringComparison.OrdinalIgnoreCase) || file.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            && Path.GetFileName(dll).Equals("Cluewren.dll", StringComparison.OrdinalIgnoreCase);
    }

    readonly record struct Owned(int Command, int CommandStart, int CommandEnd, int Args, int ArgsStart, int ArgsEnd, int? Approval);

    // The messages name keys, never values: another server's command line may hold a key of its own.
    static Owned OwnedLines(List<TomlLine> lines, int header)
    {
        if (lines.Any(line => line.Kind == Kind.Header && line.Path is ["mcp_servers", Name, var key, ..] && HttpOnly.Contains(key)))
            throw new NotCluewrensException("it is an HTTP server");
        var end = lines.FindIndex(header + 1, line => line.Kind == Kind.Header);
        var found = new int?[OwnedKeys.Length];
        for (var i = header + 1; i < (end < 0 ? lines.Count : end); i++)
        {
            var line = lines[i];
            if (line.Kind != Kind.Key)
                continue;
            if (HttpOnly.Contains(line.Path![0]))
                throw new NotCluewrensException("it is an HTTP server");
            if (Array.IndexOf(OwnedKeys, line.Path[0]) is not (>= 0 and var key))
                continue;
            if (line.Path.Length > 1 || line.Open || found[key] is not null)
                throw new NotCluewrensException($"connect can't read its {OwnedKeys[key]} exactly");
            found[key] = i;
        }
        if (found[0] is not { } command)
            throw new NotCluewrensException("it has no command");
        if (found[1] is not { } args)
            throw new NotCluewrensException("it isn't Cluewren's");
        var commandLine = Plain(lines[command]);
        var argsLine = Plain(lines[args]);
        int commandStart = ValueStart(commandLine), commandEnd = commandStart, argsStart = ValueStart(argsLine), argsEnd = argsStart;
        if (BasicOrLiteral(commandLine, ref commandEnd) is not { } program || !Ends(commandLine, commandEnd)
            || StringArray(argsLine, ref argsEnd) is not { } arguments || !Ends(argsLine, argsEnd))
            throw new NotCluewrensException("connect can't read its command or args exactly");
        if (!Cluewrens(program, arguments))
            throw new NotCluewrensException("it isn't Cluewren's");
        return new(command, commandStart, commandEnd, args, argsStart, argsEnd, found[2]);
    }

    // Removes what connect owns and no other byte: each cluewren table up to its last key or value line (with blank and comment
    // lines between two of them), the marker comment directly above the server's table, and above that marker the line break
    // the append added: the blank line when the file ended with one, otherwise the break that ended the file's last line.
    static string Without(List<TomlLine> lines, int header)
    {
        var final = lines[^1].Text.Length == 0;
        var count = final ? lines.Count - 1 : lines.Count;
        var owned = new bool[count];
        for (var i = 0; i < count; i++)
        {
            if (lines[i].Kind != Kind.Header || lines[i].Path is not { } path || !InTable(path))
                continue;
            var last = i;
            for (var j = i + 1; j < count && lines[j].Kind != Kind.Header; j++)
                if (lines[j].Kind is not (Kind.Blank or Kind.Comment))
                    last = j;
            Array.Fill(owned, true, i, last - i + 1);
        }
        for (var i = 1; i < count; i++)
        {
            if (owned[i] || !owned[i - 1])
                continue;
            var j = i;
            while (j < count && !owned[j] && lines[j].Kind is Kind.Blank or Kind.Comment)
                j++;
            if (j < count && owned[j])
                Array.Fill(owned, true, i, j - i);
        }
        var marked = header > 0 && lines[header - 1].Kind == Kind.Comment && Plain(lines[header - 1]) == TableMark;
        var separated = marked && header > 1 && lines[header - 2].Kind == Kind.Blank && Plain(lines[header - 2]).Length == 0;
        if (marked)
            owned[header - 1] = true;
        if (separated)
            owned[header - 2] = true;
        var kept = Enumerable.Range(0, count).Where(i => !owned[i]).Select(i => lines[i].Text).ToList();
        if (kept.Count == 0)
            return "";
        var lastKept = Enumerable.Range(0, count).Last(i => !owned[i]);
        var terminated = lastKept < count - 1 || final;
        if (marked && !separated && lastKept == header - 2)
        {
            terminated = false;
            if (kept[^1].EndsWith('\r'))
                kept[^1] = kept[^1][..^1];
        }
        return string.Join('\n', kept) + (terminated ? "\n" : "");
    }

    public static string CodexConfig(string text, string[]? command, bool approve = false)
    {
        var lines = Scan(text);
        var (main, sub) = Tables(lines);
        if (main is not { } header)
        {
            if (sub)
                throw new NotCluewrensException("it has settings there but no table of its own");
            if (command is null)
                return text;
            var newLine = NewLine(text);
            return (text.Length > 0 ? text + newLine : "") + CodexTable(command, approve).Replace("\n", newLine);
        }
        var owned = OwnedLines(lines, header);
        if (command is null)
            return Without(lines, header);
        var approval = owned.Approval;
        var markedAbove = approval is { } a && lines[a - 1].Kind == Kind.Comment && Plain(lines[a - 1]) is ApprovalMark or OldApprovalMark;
        var cluewrens = markedAbove && Plain(lines[approval!.Value]) == ApprovalLine;
        var output = new List<string>();
        for (var i = 0; i < lines.Count; i++)
        {
            var plain = Plain(lines[i]);
            var cr = lines[i].Text.EndsWith('\r') ? "\r" : "";
            if (i == owned.Command)
                output.Add(plain[..owned.CommandStart] + JsonSerializer.Serialize(command[0]) + plain[owned.CommandEnd..] + cr);
            else if (i == owned.Args)
            {
                output.Add(plain[..owned.ArgsStart] + JsonSerializer.Serialize(command[1..]) + plain[owned.ArgsEnd..] + cr);
                if (approve && approval is null)
                    output.AddRange([ApprovalMark + cr, ApprovalLine + cr]);
            }
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
        }
        return string.Join('\n', output);
    }

    public sealed record CodexChange(string Path, byte[]? Before, byte[]? After, string Message, string ByHand, bool Refused);

    static readonly UTF8Encoding StrictUtf8 = new(false, true);

    static string LeftAsItIs(string path, string why, string byHand) => $"Left {path} as it is: {why}. {byHand}";

    static string Why(Exception e) => e.Message.TrimEnd('.');

    static bool FileFailure(Exception e) => e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException
        or OperationCanceledException or System.Security.SecurityException;

    public static CodexChange PlanCodex(string path, string[]? command, bool approve)
    {
        var lines = command is null ? "" : $"{CommandLine(command)}, {ArgsLine(command)}" + (approve ? $", {ApprovalLine}" : "");
        var byHand = command is null ? $"Remove the {Name} server by hand: codex mcp remove {Name}" : $"Set the {Name} server up there by hand: {lines}";
        byte[]? before;
        try
        {
            before = File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception e) when (FileFailure(e))
        {
            return new(path, null, null, LeftAsItIs(path, Why(e), byHand), byHand, true);
        }
        var bom = before is [0xEF, 0xBB, 0xBF, ..];
        string text;
        try
        {
            text = before is null ? "" : StrictUtf8.GetString(before, bom ? 3 : 0, before.Length - (bom ? 3 : 0));
        }
        catch (DecoderFallbackException)
        {
            return new(path, before, null, LeftAsItIs(path, "it isn't UTF-8 text", byHand), byHand, true);
        }
        try
        {
            var changed = CodexConfig(text, command, approve);
            return new(path, before, changed == text ? null : [.. (bom ? (byte[])[0xEF, 0xBB, 0xBF] : []), .. Utf8.GetBytes(changed)],
                $"{(command is null ? "Removed" : "Set up")} the {Name} MCP server in {path}" + (approve ? ": Codex calls its tools without asking you first" : ""),
                byHand, false);
        }
        catch (NotCluewrensException e)
        {
            return new(path, before, null, $"Left the {Name} MCP server in {path} as it is: {e.Message}"
                + (command is null ? "" : $". To have Cluewren's there, rename that server and connect again, or set it up there by hand: {lines}"), byHand, true);
        }
        catch (FormatException e)
        {
            return new(path, before, null, LeftAsItIs(path, e.Message, byHand), byHand, true);
        }
    }

    // The config can hold keys in MCP env settings, and a new file (a temporary copy, a backup) doesn't reliably carry the old
    // one's access rules, owner, mode, encryption or links, so connect edits the file itself through one handle.
    // A file that changed after it was read is left alone: writing what was computed from the old text would lose that change.
    public bool ApplyCodex(CodexChange change)
    {
        if (change.Refused)
        {
            say(change.Message);
            return false;
        }
        if (change.After is not { } after)
            return true;
        const string changed = "it changed while connect was reading it; run connect again";
        var path = change.Path;
        FileStream? file = null;
        var began = false;
        try
        {
            if (change.Before is null)
            {
                if (Path.GetDirectoryName(Path.GetFullPath(path)) is { } folder)
                    Directory.CreateDirectory(folder);
                var create = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, BufferSize = 0 };
                if (!OperatingSystem.IsWindows())
                    create.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                file = new FileStream(path, create);
            }
            else
            {
                file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 0);
                using var now = new MemoryStream();
                file.CopyTo(now);
                if (!now.ToArray().AsSpan().SequenceEqual(change.Before))
                {
                    say(LeftAsItIs(path, changed, change.ByHand));
                    return false;
                }
                file.Position = 0;
            }
            began = true;
            file.Write(after);
            file.SetLength(after.Length);
            file.Flush(true);
        }
        catch (Exception e) when (FileFailure(e))
        {
            if (began)
                say($"Writing {path} failed after it began: {Why(e)}. What the file holds now is not known; it may be damaged. Check it. {change.ByHand}");
            else
                say(LeftAsItIs(path, e is IOException && File.Exists(path) != (change.Before is not null) ? changed : Why(e), change.ByHand));
            return false;
        }
        finally
        {
            file?.Dispose();
        }
        say(change.Message);
        return true;
    }

    bool EditCodexConfig(string[]? command, bool approve) => ApplyCodex(PlanCodex(Path.Combine(CodexHome(), "config.toml"), command, approve));

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

    bool Put(string path, string text) => Tried(path, () => Write(path, text));

    bool Drop(string path) => Tried(path, () => Delete(path));

    bool Write(string path, string text)
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

    bool Delete(string path)
    {
        if (!File.Exists(path) || !Ours(path))
            return true;
        File.Delete(path);
        say($"Removed {path}");
        var folder = Path.GetDirectoryName(path)!;
        if (!Directory.EnumerateFileSystemEntries(folder).Any())
            Directory.Delete(folder);
        return true;
    }

    bool Tried(string path, Func<bool> step)
    {
        try
        {
            return step();
        }
        catch (Exception e) when (FileFailure(e))
        {
            say($"Couldn't finish with {path}: {Why(e)}");
            return false;
        }
    }

    sealed record ClaudeEntry(string Scope, string Type, string Command, string Args, bool NoEnvironment);

    enum Found { Exists, Absent, Unknown }

    // `claude mcp get` is a display: it joins args with spaces and doesn't show every setting, so connect only ever reads it to
    // decide, never to rebuild an entry from it.
    // The display is read line for line in the shape claude 2.1.322 prints (Issue only when the check failed, no Environment label
    // for an entry without env); any other line, one a value ran onto included, makes it unread.
    static ClaudeEntry? Entry(string output)
    {
        var lines = output.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        var at = 0;
        string? Next(string label)
        {
            if (at == lines.Length || lines[at] != $"  {label}:" && !lines[at].StartsWith($"  {label}: ", StringComparison.Ordinal))
                return null;
            var line = lines[at++];
            return line.Length > label.Length + 4 ? line[(label.Length + 4)..] : "";
        }
        if (lines[at++] != $"{Name}:" || Next("Scope") is not { } scope || Next("Status") is null)
            return null;
        Next("Issue");
        if (Next("Type") is not { } type || Next("Command") is not { } command || Next("Args") is not { } args)
            return null;
        var noEnvironment = true;
        if (at < lines.Length && lines[at] == "  Environment:")
            for (at++; at < lines.Length && EnvSetting().IsMatch(lines[at]); at++)
                noEnvironment = false;
        return at + 3 == lines.Length && lines[at] == "" && lines[at + 2] == ""
            && lines[at + 1].StartsWith($"To remove this server, run: claude mcp remove {Name} -s ", StringComparison.Ordinal)
            && lines[at + 1][$"To remove this server, run: claude mcp remove {Name} -s ".Length..] is { Length: > 0 } word && word.All(char.IsAsciiLetterLower)
            ? new(scope, type, command, args, noEnvironment) : null;
    }

    [GeneratedRegex("^    [A-Za-z_][A-Za-z0-9_]*=")]
    private static partial Regex EnvSetting();

    (Found State, ClaudeEntry? Entry, string Why) ReadClaude()
    {
        string[] get = ["claude", "mcp", "get", Name];
        var (code, output) = Run(get);
        if (code is null)
            return (Found.Unknown, null, output);
        if (code != 0)
            return output.TrimStart().StartsWith($"No MCP server named \"{Name}\"", StringComparison.Ordinal) ? (Found.Absent, null, "")
                : (Found.Unknown, null, $"`{Shown(get)}` exited with {code}");
        return Entry(output) is { } entry ? (Found.Exists, entry, "") : (Found.Unknown, null, $"`{Shown(get)}` printed something connect doesn't read");
    }

    static bool InUserScope(ClaudeEntry entry) => entry.Scope.StartsWith("User config", StringComparison.Ordinal);

    // Args shown as exactly `mcp` is one argument without spaces, so for this form alone the display can't stand for anything else.
    static bool Removable(ClaudeEntry entry) =>
        InUserScope(entry) && entry.NoEnvironment && entry.Type == "stdio" && entry.Args == "mcp" && Cluewrens(entry.Command, ["mcp"]);

    static string Unmoved(ClaudeEntry entry) =>
        !InUserScope(entry) ? $"the one Claude Code shows here is from its {entry.Scope}"
        : !entry.NoEnvironment ? "it has environment settings, which connect does not move"
        : Removable(entry) ? $"it runs {Shown([entry.Command, "mcp"])}, another Cluewren"
        : "connect can't tell that it is Cluewren's";

    public bool ClaudeServer(string[]? command)
    {
        string[] remove = ["claude", "mcp", "remove", "--scope", "user", Name];
        var (found, entry, why) = ReadClaude();
        if (found == Found.Unknown)
        {
            say($"Couldn't tell whether Claude Code has a {Name} MCP server ({why}), so nothing was changed. Check with `claude mcp get {Name}`"
                + (command is null ? $", then remove it by hand: {Shown(remove)}" : $"; if there is none, add it by hand: {Shown(Adding(command))}"));
            return false;
        }
        if (command is null)
            return found == Found.Absent || RemoveClaude(entry!, remove);
        if (found == Found.Absent)
        {
            var (code, output) = Run(Adding(command));
            if (code != 0)
            {
                say(code is null ? $"{output}. Add the server by hand: {Shown(Adding(command))}" : $"`claude mcp add` failed: {output.Trim()}");
                return false;
            }
            say($"Added the {Name} MCP server to Claude Code (user scope: every project)");
            return true;
        }
        if (InUserScope(entry!) && entry!.NoEnvironment && entry.Type == "stdio" && command is [var program, "mcp"] && entry.Command == program
            && entry.Args == "mcp")
        {
            say($"Claude Code's {Name} MCP server already runs this Cluewren");
            return true;
        }
        say($"Left Claude Code's {Name} MCP server as it is: {Unmoved(entry!)}. "
            + (Removable(entry!) ? "To run this Cluewren instead: cluewren connect claude --remove, then cluewren connect claude"
                : $"If you don't need it, remove it with {Shown(remove)}, then run cluewren connect claude"));
        return false;
    }

    static string[] Adding(string[] command) => ["claude", "mcp", "add", "--scope", "user", Name, "--", .. command];

    bool RemoveClaude(ClaudeEntry entry, string[] remove)
    {
        if (!Removable(entry))
        {
            say($"Left Claude Code's {Name} MCP server as it is: {Unmoved(entry)}. If you don't need it, remove it with {Shown(remove)}");
            return false;
        }
        var (code, output) = Run(remove);
        if (code == 0)
        {
            say($"Removed the {Name} MCP server from Claude Code");
            return true;
        }
        var failed = code is null ? output : $"`{Shown(remove)}` failed: {output.Trim()}";
        var (now, _, why) = ReadClaude();
        say(now switch
        {
            Found.Absent => $"{failed}; read again, Claude Code has no {Name} MCP server now",
            Found.Exists => $"{failed}; read again, Claude Code still has its {Name} MCP server. Remove it by hand: {Shown(remove)}",
            _ => $"{failed}; read again, connect couldn't tell whether it is still there ({why}). Check with `claude mcp get {Name}`",
        });
        return now == Found.Absent;
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
            ok &= Drop(Path.Combine(ClaudeHome(), "skills", Name, "SKILL.md"));
            ok &= Drop(Path.Combine(ClaudeHome(), "agents", $"{ClaudeDescriber}.md"));
        }
        else
        {
            ok = EditCodexConfig(null, false);
            ok &= Drop(Path.Combine(AgentsHome(), "skills", Name, "SKILL.md"));
            ok &= Drop(Path.Combine(CodexHome(), "agents", $"{CodexDescriber}.toml"));
        }
        return ok ? 0 : 3;
    }
}
