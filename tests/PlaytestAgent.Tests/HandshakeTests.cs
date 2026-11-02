using PlaytestAgent.Bridge;

namespace PlaytestAgent.Tests;

public class HandshakeTests
{
    [Fact]
    public void Proofs_of_the_two_roles_differ()
    {
        var (secret, plugin, brain) = (Handshake.NewSecret(), Handshake.NewNonce(), Handshake.NewNonce());
        var brainProof = Handshake.Prove(secret, Handshake.BrainRole, plugin, brain);
        Assert.False(Handshake.Verify(secret, Handshake.PluginRole, plugin, brain, brainProof));
        Assert.True(Handshake.Verify(secret, Handshake.BrainRole, plugin, brain, brainProof));
    }

    [Fact]
    public void Proof_is_bound_to_both_nonces()
    {
        var (secret, plugin, brain) = (Handshake.NewSecret(), Handshake.NewNonce(), Handshake.NewNonce());
        var proof = Handshake.Prove(secret, Handshake.BrainRole, plugin, brain);
        Assert.False(Handshake.Verify(secret, Handshake.BrainRole, Handshake.NewNonce(), brain, proof));
        Assert.False(Handshake.Verify(secret, Handshake.BrainRole, plugin, Handshake.NewNonce(), proof));
    }
}
