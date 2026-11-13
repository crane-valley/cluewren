// Finding the brain and proving who is who, the same as the brain's src/Cluewren/Bridge (DiscoveryFile.cs and
// Handshake.cs) and the Unity plugin's Discovery.cs and Handshake.cs. Nothing is decided on brain.json's word alone: its
// owner is checked before it is read, and its URL must name this machine.

#pragma once

#include "CoreMinimal.h"

class FJsonObject;

namespace PlaytestBrain
{
	constexpr int32 NonceBytes = 32;

	struct FAddress
	{
		FString Host;
		int32 Port = 0;
		FString Path;
		TArray<uint8> Secret;
	};

	/** %LOCALAPPDATA%\Cluewren\brain.json. */
	FString FilePath();

	/** Why the brain announced in Path can't be used by the project in ProjectDir, or "" with its address. */
	FString Read(const FString& Path, const FString& ProjectDir, FAddress& Out);

	FString ProjectDir();

	FString ProjectDirOf(const FString& Dir);

	/** Why a run announced in brain.json isn't this project's, or "". The same rule and words as the Unity and Godot
	 *  plugins: no `folders` (a brain before 0.1.1) is no filter; an unknown `match` is never a match. */
	FString ForAnotherProject(const FJsonObject& Said, const FString& ProjectDir);

	/** Why Url is refused, or "" with its parts. The only URL connected to: ws:// to 127.0.0.1 or [::1], with no userinfo. */
	FString CheckUrl(const FString& Url, FAddress& Out);

	TArray<uint8> NewNonce();

	/** HMAC-SHA256 of the role label, a zero byte, the plugin's nonce and the brain's. */
	TArray<uint8> Prove(const TArray<uint8>& Secret, const TCHAR* Role, const TArray<uint8>& PluginNonce, const TArray<uint8>& BrainNonce);

	bool Verify(const TArray<uint8>& Secret, const TCHAR* Role, const TArray<uint8>& PluginNonce, const TArray<uint8>& BrainNonce, const TArray<uint8>& Proof);

	TArray<uint8> HelloMac(const TArray<uint8>& Secret, const TArray<uint8>& PluginNonce, const FString& ProjectDir);

	inline const TCHAR* const BrainRole = TEXT("brain-proof");
	inline const TCHAR* const PluginRole = TEXT("plugin-proof");
	inline const TCHAR* const HelloRole = TEXT("hello");
}
