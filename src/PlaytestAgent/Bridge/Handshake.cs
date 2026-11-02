using System.Security.Cryptography;
using System.Text;

namespace PlaytestAgent.Bridge;

public static class Handshake
{
    public const string BrainRole = "brain-proof";
    public const string PluginRole = "plugin-proof";
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
}
