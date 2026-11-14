using System.Security.Cryptography;
using System.Text;

namespace Cluewren.Bridge;

public static class Handshake
{
    public const string BrainRole = "brain-proof";
    public const string PluginRole = "plugin-proof";
    public const string HelloRole = "hello";
    public const int NonceBytes = 32;

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(32);

    public static byte[] NewNonce() => RandomNumberGenerator.GetBytes(NonceBytes);

    public static byte[] Prove(ReadOnlySpan<byte> secret, string role, ReadOnlySpan<byte> pluginNonce,
        ReadOnlySpan<byte> brainNonce)
    {
        var label = Encoding.ASCII.GetBytes(role);
        var input = new byte[label.Length + 1 + pluginNonce.Length + brainNonce.Length];
        label.CopyTo(input, 0);
        pluginNonce.CopyTo(input.AsSpan(label.Length + 1));
        brainNonce.CopyTo(input.AsSpan(label.Length + 1 + pluginNonce.Length));
        return HMACSHA256.HashData(secret, input);
    }

    public static bool Verify(ReadOnlySpan<byte> secret, string role, ReadOnlySpan<byte> pluginNonce,
        ReadOnlySpan<byte> brainNonce, ReadOnlySpan<byte> proof) =>
        CryptographicOperations.FixedTimeEquals(Prove(secret, role, pluginNonce, brainNonce), proof);

    public static byte[] ProveHello(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> pluginNonce, string projectDir)
    {
        var label = Encoding.ASCII.GetBytes(HelloRole);
        var project = Encoding.UTF8.GetBytes(projectDir);
        var input = new byte[label.Length + 1 + pluginNonce.Length + 1 + project.Length];
        label.CopyTo(input, 0);
        pluginNonce.CopyTo(input.AsSpan(label.Length + 1));
        project.CopyTo(input, label.Length + 1 + pluginNonce.Length + 1);
        return HMACSHA256.HashData(secret, input);
    }

    public static bool VerifyHello(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> pluginNonce, string projectDir,
        ReadOnlySpan<byte> mac) =>
        CryptographicOperations.FixedTimeEquals(ProveHello(secret, pluginNonce, projectDir), mac);
}
