// The mutual proof of brain.json's secret, the same as the brain's src/PlaytestAgent/Bridge/Handshake.cs: the secret
// never travels, and each MAC starts with a role label so one side's proof can't be reflected back as the other's.

using System.Security.Cryptography;
using System.Text;

namespace Autopilot.Playtest
{
    public static class Handshake
    {
        public const string BrainRole = "brain-proof";
        public const string PluginRole = "plugin-proof";
        public const int NonceBytes = 32;

        public static byte[] NewNonce()
        {
            var nonce = new byte[NonceBytes];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(nonce);
            return nonce;
        }

        public static byte[] Prove(byte[] secret, string role, byte[] pluginNonce, byte[] brainNonce)
        {
            var label = Encoding.ASCII.GetBytes(role);
            var input = new byte[label.Length + 1 + pluginNonce.Length + brainNonce.Length];
            label.CopyTo(input, 0);
            pluginNonce.CopyTo(input, label.Length + 1);
            brainNonce.CopyTo(input, label.Length + 1 + pluginNonce.Length);
            using (var mac = new HMACSHA256(secret)) return mac.ComputeHash(input);
        }

        public static bool Verify(byte[] secret, string role, byte[] pluginNonce, byte[] brainNonce, byte[] proof)
        {
            return CryptographicOperations.FixedTimeEquals(Prove(secret, role, pluginNonce, brainNonce), proof);
        }
    }
}
