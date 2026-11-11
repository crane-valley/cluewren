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

    [GeneratedRegex("""^\s*\[\s*mcp_servers\s*\.\s*("cluewren"|cluewren)\s*(\.[^\]]*)?\]\s*(#.*)?$""")]
    private static partial Regex Table();

    [GeneratedRegex(@"^\s*\[")]
    private static partial Regex Header();

    [GeneratedRegex("""^\s*mcp_servers\s*\.\s*("cluewren"|cluewren)\s*\.""")]
    private static partial Regex DottedKey();

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

    (int? Code, string Output) Run(string[] command)
    {
        if (runner is not null)
            return runner(command);
        if (OnPath(command[0]) is not { } program)
            return (null, $"Claude Code's `{command[0]}` command isn't on PATH");
        ProcessStartInfo info;
        if (Path.GetExtension(program).ToUpperInvariant() is ".CMD" or ".BAT")
        {
            string[] line = [program, .. command.Skip(1)];
            if (line.Any(arg => arg.IndexOfAny(CmdSpecial) >= 0))
                return (null, $"{program} runs through cmd.exe, which can't be given these arguments safely");
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

    static string CodexTable(string[] command) =>
        $"# {Mark}: Cluewren's MCP server, for the game open in your editor (`cluewren connect codex --remove` takes it out)\n"
        + $"[mcp_servers.\"{Name}\"]\ncommand = {JsonSerializer.Serialize(command[0])}\nargs = {JsonSerializer.Serialize(command[1..])}\n"
        + "# (its tools reach only the game open in the editor and the game's memory folder)\n"
        + "default_tools_approval_mode = \"approve\"\n";

    public static string WithoutTable(string text)
    {
        var kept = new List<string>();
        var skipping = false;
        foreach (var line in text.Split('\n'))
        {
            if (Table().IsMatch(line.TrimEnd('\r')))
            {
                skipping = true;
                while (kept.Count > 0 && kept[^1].TrimStart().StartsWith('#') && kept[^1].Contains(Mark, StringComparison.Ordinal))
                    kept.RemoveAt(kept.Count - 1);
                continue;
            }
            if (skipping && Header().IsMatch(line))
                skipping = false;
            if (!skipping)
                kept.Add(line);
        }
        return string.Join('\n', kept);
    }

    public static string CodexConfig(string text, string[]? command)
    {
        var kept = WithoutTable(text).TrimEnd('\n', '\r');
        if (command is not null && kept.Split('\n').Any(line => DottedKey().IsMatch(line)))
            throw new FormatException($"the {Name} server is set up there by a dotted key");
        return command is null ? (kept.Length > 0 ? kept + "\n" : "") : (kept.Length > 0 ? kept + "\n\n" : "") + CodexTable(command);
    }

    bool EditCodexConfig(string[]? command)
    {
        var path = Path.Combine(CodexHome(), "config.toml");
        var text = File.Exists(path) ? File.ReadAllText(path) : "";
        if (command is null && !text.Split('\n').Any(line => Table().IsMatch(line.TrimEnd('\r'))))
            return true;
        string changed;
        try
        {
            changed = CodexConfig(text, command);
        }
        catch (FormatException e)
        {
            say($"Couldn't change {path}: {e.Message}. Set the server up by hand: codex mcp add {Name} -- {string.Join(' ', ServerCommand())}");
            return false;
        }
        if (changed == text)
            return true;
        if (text.Length > 0)
            File.Copy(path, path + ".before-cluewren", overwrite: true);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, changed, Utf8);
        say($"{(command is null ? "Removed" : "Set up")} the {Name} MCP server in {path}");
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

    bool ClaudeServer(string[]? command)
    {
        var byHand = command is null ? $"Remove the server by hand: claude mcp remove --scope user {Name}"
            : $"Add the server by hand: claude mcp add --scope user {Name} -- {Shown(command)}";
        var (code, output) = Run(["claude", "mcp", "get", Name]);
        if (code is null)
        {
            say($"{output}. {byHand}");
            return false;
        }
        var there = code == 0;
        if (there && !output.Replace('\\', '/').Contains("cluewren", StringComparison.Ordinal))
        {
            say($"Left Claude Code's {Name} MCP server as it is: it isn't Cluewren's");
            return false;
        }
        if (there)
            Run(["claude", "mcp", "remove", "--scope", "user", Name]);
        if (command is null)
        {
            if (there)
                say($"Removed the {Name} MCP server from Claude Code");
            return true;
        }
        (code, output) = Run(["claude", "mcp", "add", "--scope", "user", Name, "--", .. command]);
        if (code is null)
        {
            say($"{output}. {byHand}");
            return false;
        }
        if (code != 0)
        {
            say($"`claude mcp add` failed: {output.Trim()}");
            return false;
        }
        say($"Added the {Name} MCP server to Claude Code (user scope: every project)");
        return true;
    }

    public int Add(string client)
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
            ok = EditCodexConfig(command);
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
            ok = EditCodexConfig(null);
            Drop(Path.Combine(AgentsHome(), "skills", Name, "SKILL.md"));
            Drop(Path.Combine(CodexHome(), "agents", $"{CodexDescriber}.toml"));
        }
        return ok ? 0 : 3;
    }
}
