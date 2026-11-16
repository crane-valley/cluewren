// Finding the brain and proving who is who, the same as the brain's src/Cluewren/Bridge (Rendezvous.cs and Handshake.cs):
// this process advertises itself, the brain answers with an invite. Nothing is decided on the invite's word alone: its
// owner is checked before it is read, and its URL must name this machine.

#pragma once

#include "CoreMinimal.h"

class FJsonObject;

namespace PlaytestBrain
{
	constexpr int32 Protocol = 2;
	constexpr int32 NonceBytes = 32;

	struct FAddress
	{
		FString Host;
		int32 Port = 0;
		FString Path;
		TArray<uint8> Secret;
		uint32 Pid = 0;
	};

	struct FAdvert
	{
		uint32 Pid = 0;
		FString Id;
		FString EngineVersion;
		FString PluginVersion;
		FString ProjectDir;
		bool bEditor = false;
	};

	enum class EInvite : uint8
	{
		Taken,
		NotThisUsers,
		Unreadable,
		NotAnInvite,
		OtherEditor,
		OtherRelease,
		NoSecret,
		Ended,
		BadUrl,
	};

	/** %LOCALAPPDATA%\Cluewren. */
	FString Folder();

	FString AdvertPath(const FString& In, uint32 Pid);

	FString InvitePath(const FString& In, uint32 Pid);

	/** 32 lowercase hex characters, made once per process: an invite names the advert it answers by it. */
	const FString& EditorId();

	TSharedRef<FJsonObject> AdvertJson(const FAdvert& Advert);

	bool WriteAdvert(const FString& In, const FAdvert& Advert);

	void RemoveAdvert(const FString& In, uint32 Pid);

	/** When the invite at Path was last written, 0 without one. Windows' own 100 ns count: the engine's file times stop
	 *  at the second, and an invite written in the second of the one before it would pass for the same. */
	uint64 InviteWritten(const FString& Path);

	/** OutStatus stays "" for OtherEditor: an invite written for a process that had this pid before is left alone
	 *  without a word. */
	EInvite ReadInvite(const FString& Path, const FString& Invited, FAddress& Out, FString& OutStatus);

	bool Alive(const FAddress& Brain);

	/** A text of this plugin's own for the code of a refusal: the brain's message is never shown before it has proved
	 *  itself. */
	FString RefusalText(const FString& Code);

	/** The refusal a close before the welcome stands for, "" for none: a refusal's close carries its code as the reason, for
	 *  the error frame may not have come through. */
	FString RefusalOfClose(int32 Status, const FString& Reason);

	FString ProjectDir();

	FString ProjectDirOf(const FString& Dir);

	/** Why Url is refused, or "" with its parts. The only URL connected to: ws:// to 127.0.0.1 or [::1], with no userinfo. */
	FString CheckUrl(const FString& Url, FAddress& Out);

	TArray<uint8> NewNonce();

	/** HMAC-SHA256 of the role label, a zero byte, the plugin's nonce and the brain's. */
	TArray<uint8> Prove(const TArray<uint8>& Secret, const TCHAR* Role, const TArray<uint8>& PluginNonce, const TArray<uint8>& BrainNonce);

	bool Verify(const TArray<uint8>& Secret, const TCHAR* Role, const TArray<uint8>& PluginNonce, const TArray<uint8>& BrainNonce, const TArray<uint8>& Proof);

	TArray<uint8> HelloMac(const TArray<uint8>& Secret, const TArray<uint8>& PluginNonce, const FString& ProjectDir);

	void Sign(FJsonObject& Hello, const TArray<uint8>& Secret, const TArray<uint8>& PluginNonce, const FString& ProjectDir);

	inline const TCHAR* const BrainRole = TEXT("brain-proof");
	inline const TCHAR* const PluginRole = TEXT("plugin-proof");
	inline const TCHAR* const HelloRole = TEXT("hello");
	inline const TCHAR* const RunEnded = TEXT("The run this editor was invited to has ended");
	inline const TCHAR* const Unproven = TEXT("it didn't prove it is this machine's brain");
}
