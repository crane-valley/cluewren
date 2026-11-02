## The mutual proof of brain.json's secret, the same as the brain's src/PlaytestAgent/Bridge/Handshake.cs: the secret
## never travels, and each MAC starts with a role label so one side's proof can't be reflected back as the other's.
extends RefCounted

const BRAIN_ROLE := "brain-proof"
const PLUGIN_ROLE := "plugin-proof"
const NONCE_BYTES := 32


static func new_nonce() -> PackedByteArray:
	return Crypto.new().generate_random_bytes(NONCE_BYTES)


static func prove(secret: PackedByteArray, role: String, plugin_nonce: PackedByteArray, brain_nonce: PackedByteArray) -> PackedByteArray:
	var input := role.to_ascii_buffer()
	input.append(0)
	input.append_array(plugin_nonce)
	input.append_array(brain_nonce)
	return Crypto.new().hmac_digest(HashingContext.HASH_SHA256, secret, input)


static func verify(secret: PackedByteArray, role: String, plugin_nonce: PackedByteArray, brain_nonce: PackedByteArray, proof: PackedByteArray) -> bool:
	return Crypto.new().constant_time_compare(prove(secret, role, plugin_nonce, brain_nonce), proof)
