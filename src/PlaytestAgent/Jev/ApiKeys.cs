using PlaytestAgent.Bridge;

namespace PlaytestAgent.Jev;

public static class ApiKeys
{
    public const string TypeSafeKey = "TYPESAFE_API_KEY";

    public static string KeysFile => Path.Combine(DiscoveryFile.DefaultFolder, "keys.env");

    public static string? Find(string name, string? file = null)
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(name)?.Trim();
        if (!string.IsNullOrEmpty(fromEnvironment))
            return fromEnvironment;
        var path = file ?? KeysFile;
        if (!File.Exists(path))
            return null;
        foreach (var line in File.ReadLines(path))
        {
            var separator = line.IndexOf('=');
            if (separator > 0 && line.AsSpan(0, separator).Trim().SequenceEqual(name) && !line.TrimStart().StartsWith('#'))
                return line[(separator + 1)..].Trim() is { Length: > 0 } value ? value : null;
        }
        return null;
    }
}
