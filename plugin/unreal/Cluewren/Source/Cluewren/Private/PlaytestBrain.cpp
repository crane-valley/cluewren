#include "PlaytestBrain.h"

#include "Dom/JsonObject.h"
#include "HAL/FileManager.h"
#include "HAL/PlatformProcess.h"
#include "Misc/Base64.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"
#include "PlaytestJson.h"

#include "Windows/AllowWindowsPlatformTypes.h"
#include <bcrypt.h>
#include <aclapi.h>
#include "Windows/HideWindowsPlatformTypes.h"

namespace PlaytestBrain
{
namespace
{
	constexpr int32 SecretBytes = 32;
	constexpr int32 IdBytes = 16;
	constexpr int32 MaxCodeShown = 32;
	constexpr int32 PolicyClose = 1008;

	const TCHAR* const ReleasesDiffer = TEXT("The cluewren command and this plugin are of different releases: install the same release of both");

	bool OwnerIsThisUser(const FString& Path)
	{
		PSID Owner = nullptr;
		PSECURITY_DESCRIPTOR Descriptor = nullptr;
		if (GetNamedSecurityInfoW(*Path, SE_FILE_OBJECT, OWNER_SECURITY_INFORMATION, &Owner, nullptr, nullptr, nullptr, &Descriptor) != ERROR_SUCCESS)
		{
			return false;
		}
		bool bMine = false;
		HANDLE Token = nullptr;
		if (OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &Token))
		{
			DWORD Size = 0;
			GetTokenInformation(Token, TokenUser, nullptr, 0, &Size);
			TArray<uint8> Buffer;
			Buffer.SetNumUninitialized(Size);
			if (GetTokenInformation(Token, TokenUser, Buffer.GetData(), Size, &Size))
			{
				bMine = EqualSid(Owner, reinterpret_cast<TOKEN_USER*>(Buffer.GetData())->User.Sid) != 0;
			}
			CloseHandle(Token);
		}
		LocalFree(Descriptor);
		return bMine;
	}

	// A link is refused outright: its owner says nothing about what it points at.
	bool OwnedByThisUser(const FString& Path)
	{
		const DWORD Attributes = GetFileAttributesW(*Path);
		return Attributes != INVALID_FILE_ATTRIBUTES && !(Attributes & FILE_ATTRIBUTE_REPARSE_POINT) && OwnerIsThisUser(Path);
	}
}

FString Folder()
{
	return FString(FPlatformProcess::UserSettingsDir()) / TEXT("Cluewren");
}

FString AdvertPath(const FString& In, uint32 Pid)
{
	return In / TEXT("editors") / FString::Printf(TEXT("%u.json"), Pid);
}

FString InvitePath(const FString& In, uint32 Pid)
{
	return In / TEXT("invites") / FString::Printf(TEXT("%u.json"), Pid);
}

const FString& EditorId()
{
	static const FString Id = []()
	{
		uint8 Random[IdBytes];
		verify(BCRYPT_SUCCESS(BCryptGenRandom(nullptr, Random, IdBytes, BCRYPT_USE_SYSTEM_PREFERRED_RNG)));
		return BytesToHex(Random, IdBytes).ToLower();
	}();
	return Id;
}

TSharedRef<FJsonObject> AdvertJson(const FAdvert& Advert)
{
	TSharedRef<FJsonObject> Said = MakeShared<FJsonObject>();
	Said->SetNumberField(TEXT("pid"), Advert.Pid);
	Said->SetStringField(TEXT("id"), Advert.Id);
	Said->SetStringField(TEXT("engine"), TEXT("unreal"));
	Said->SetStringField(TEXT("engine_version"), Advert.EngineVersion);
	Said->SetStringField(TEXT("plugin_version"), Advert.PluginVersion);
	Said->SetNumberField(TEXT("protocol_min"), Protocol);
	Said->SetNumberField(TEXT("protocol_max"), Protocol);
	Said->SetStringField(TEXT("project_dir"), Advert.ProjectDir);
	Said->SetStringField(TEXT("host"), Advert.bEditor ? TEXT("editor") : TEXT("game"));
	return Said;
}

bool WriteAdvert(const FString& In, const FAdvert& Advert)
{
	const FString Path = AdvertPath(In, Advert.Pid);
	// Renamed into place: the brain never reads half an advert.
	const FString Temp = Path + TEXT(".tmp");
	IFileManager& Files = IFileManager::Get();
	return Files.MakeDirectory(*FPaths::GetPath(Path), true)
		&& FFileHelper::SaveStringToFile(PlaytestJson::Write(AdvertJson(Advert)), *Temp, FFileHelper::EEncodingOptions::ForceUTF8WithoutBOM)
		&& Files.Move(*Path, *Temp, true);
}

void RemoveAdvert(const FString& In, uint32 Pid)
{
	IFileManager::Get().Delete(*AdvertPath(In, Pid), false, true, true);
}

uint64 InviteWritten(const FString& Path)
{
	WIN32_FILE_ATTRIBUTE_DATA Info;
	if (!GetFileAttributesExW(*Path, GetFileExInfoStandard, &Info))
	{
		return 0;
	}
	return (uint64(Info.ftLastWriteTime.dwHighDateTime) << 32) | Info.ftLastWriteTime.dwLowDateTime;
}

EInvite ReadInvite(const FString& Path, const FString& Invited, FAddress& Out, FString& OutStatus)
{
	OutStatus.Reset();
	if (!OwnedByThisUser(FPaths::GetPath(Path)) || !OwnedByThisUser(Path))
	{
		if (!FPaths::FileExists(Path))
		{
			OutStatus = FString::Printf(TEXT("%s can't be read"), *Path);
			return EInvite::Unreadable;
		}
		OutStatus = FString::Printf(TEXT("%s isn't owned by this user: refused"), *Path);
		return EInvite::NotThisUsers;
	}
	FString Text;
	if (!FFileHelper::LoadFileToString(Text, *Path))
	{
		OutStatus = FString::Printf(TEXT("%s can't be read"), *Path);
		return EInvite::Unreadable;
	}
	const TSharedPtr<FJsonObject> Said = PlaytestJson::Parse(Text);
	if (!Said.IsValid())
	{
		OutStatus = FString::Printf(TEXT("%s isn't an invite"), *Path);
		return EInvite::NotAnInvite;
	}
	FString Id, Secret, Url;
	double Speaks = 0, Pid = 0;
	Said->TryGetStringField(TEXT("editor_id"), Id);
	Said->TryGetNumberField(TEXT("protocol"), Speaks);
	Said->TryGetStringField(TEXT("secret"), Secret);
	Said->TryGetNumberField(TEXT("pid"), Pid);
	Said->TryGetStringField(TEXT("url"), Url);
	if (!Id.Equals(Invited, ESearchCase::CaseSensitive))
	{
		return EInvite::OtherEditor;
	}
	if (Speaks != Protocol)
	{
		OutStatus = ReleasesDiffer;
		return EInvite::OtherRelease;
	}
	TArray<uint8> Key;
	if (!FBase64::Decode(Secret, Key) || Key.Num() < SecretBytes)
	{
		OutStatus = FString::Printf(TEXT("%s has no usable secret"), *Path);
		return EInvite::NoSecret;
	}
	if (Pid <= 0 || !FPlatformProcess::IsApplicationRunning((uint32)Pid))
	{
		OutStatus = RunEnded;
		return EInvite::Ended;
	}
	FAddress Brain;
	OutStatus = CheckUrl(Url, Brain);
	if (!OutStatus.IsEmpty())
	{
		return EInvite::BadUrl;
	}
	Brain.Secret = MoveTemp(Key);
	Brain.Pid = (uint32)Pid;
	Out = MoveTemp(Brain);
	return EInvite::Taken;
}

bool Alive(const FAddress& Brain)
{
	return FPlatformProcess::IsApplicationRunning(Brain.Pid);
}

FString RefusalText(const FString& Code)
{
	if (Code == TEXT("protocol"))
	{
		return ReleasesDiffer;
	}
	if (Code == TEXT("not_authenticated"))
	{
		return TEXT("The brain did not accept this editor's proof of the invite");
	}
	if (Code == TEXT("project"))
	{
		return TEXT("The run is for another project than this editor's");
	}
	if (Code == TEXT("resume"))
	{
		return TEXT("The session this editor tried to resume belongs to another project or engine");
	}
	// Whoever sent the code has proved nothing yet: what isn't made as a code is not shown, so that none carries a message.
	bool bCode = !Code.IsEmpty() && Code.Len() <= MaxCodeShown;
	for (int32 i = 0; i < Code.Len() && bCode; ++i)
	{
		const TCHAR C = Code[i];
		bCode = (C >= TEXT('a') && C <= TEXT('z')) || (C >= TEXT('0') && C <= TEXT('9')) || C == TEXT('_');
	}
	return bCode ? FString::Printf(TEXT("The brain refused the connection (%s)"), *Code) : FString(Unproven);
}

FString RefusalOfClose(int32 Status, const FString& Reason)
{
	return Status == PolicyClose ? RefusalText(Reason) : FString();
}

FString ProjectDir()
{
	return ProjectDirOf(FPaths::ProjectDir());
}

FString ProjectDirOf(const FString& Dir)
{
	FString Full = FPaths::ConvertRelativePathToFull(Dir);
	FPaths::NormalizeDirectoryName(Full);
	return Full;
}

FString CheckUrl(const FString& Url, FAddress& Out)
{
	const FString Refused = FString::Printf(TEXT("%s isn't ws:// on this machine: refused"), *Url);
	FString Rest;
	if (!Url.StartsWith(TEXT("ws://"), ESearchCase::CaseSensitive))
	{
		return Refused;
	}
	Rest = Url.Mid(5);
	int32 Slash = INDEX_NONE;
	if (!Rest.FindChar(TEXT('/'), Slash))
	{
		return Refused;
	}
	const FString Authority = Rest.Left(Slash);
	FString Port;
	if (Authority.StartsWith(TEXT("127.0.0.1:"), ESearchCase::CaseSensitive))
	{
		Out.Host = TEXT("127.0.0.1");
		Port = Authority.Mid(10);
	}
	else if (Authority.StartsWith(TEXT("[::1]:"), ESearchCase::CaseSensitive))
	{
		Out.Host = TEXT("[::1]");
		Port = Authority.Mid(6);
	}
	else
	{
		return Refused;  // (userinfo lands here too: it comes before the host)
	}
	if (Port.IsEmpty() || !Port.IsNumeric() || Port.Contains(TEXT(".")) || Port.Contains(TEXT("-")))
	{
		return Refused;
	}
	Out.Port = FCString::Atoi(*Port);
	Out.Path = Rest.Mid(Slash);
	return Out.Port > 0 && Out.Port < 65536 ? FString() : Refused;
}

TArray<uint8> NewNonce()
{
	TArray<uint8> Nonce;
	Nonce.SetNumUninitialized(NonceBytes);
	verify(BCRYPT_SUCCESS(BCryptGenRandom(nullptr, Nonce.GetData(), NonceBytes, BCRYPT_USE_SYSTEM_PREFERRED_RNG)));
	return Nonce;
}

TArray<uint8> Prove(const TArray<uint8>& Secret, const TCHAR* Role, const TArray<uint8>& PluginNonce, const TArray<uint8>& BrainNonce)
{
	const FTCHARToUTF8 Label(Role);
	TArray<uint8> Input;
	Input.Reserve(Label.Length() + 1 + PluginNonce.Num() + BrainNonce.Num());
	Input.Append(reinterpret_cast<const uint8*>(Label.Get()), Label.Length());
	Input.Add(0);
	Input.Append(PluginNonce);
	Input.Append(BrainNonce);
	TArray<uint8> Mac;
	Mac.SetNumUninitialized(32);
	verify(BCRYPT_SUCCESS(BCryptHash(BCRYPT_HMAC_SHA256_ALG_HANDLE, const_cast<uint8*>(Secret.GetData()), Secret.Num(), Input.GetData(), Input.Num(), Mac.GetData(), Mac.Num())));
	return Mac;
}

TArray<uint8> HelloMac(const TArray<uint8>& Secret, const TArray<uint8>& PluginNonce, const FString& ProjectDir)
{
	const FTCHARToUTF8 Label(HelloRole);
	const FTCHARToUTF8 Project(*ProjectDir);
	TArray<uint8> Input;
	Input.Reserve(Label.Length() + 1 + PluginNonce.Num() + 1 + Project.Length());
	Input.Append(reinterpret_cast<const uint8*>(Label.Get()), Label.Length());
	Input.Add(0);
	Input.Append(PluginNonce);
	Input.Add(0);
	Input.Append(reinterpret_cast<const uint8*>(Project.Get()), Project.Length());
	TArray<uint8> Mac;
	Mac.SetNumUninitialized(32);
	verify(BCRYPT_SUCCESS(BCryptHash(BCRYPT_HMAC_SHA256_ALG_HANDLE, const_cast<uint8*>(Secret.GetData()), Secret.Num(), Input.GetData(), Input.Num(), Mac.GetData(), Mac.Num())));
	return Mac;
}

void Sign(FJsonObject& Hello, const TArray<uint8>& Secret, const TArray<uint8>& PluginNonce, const FString& ProjectDir)
{
	Hello.SetNumberField(TEXT("protocol"), Protocol);
	Hello.SetStringField(TEXT("nonce"), FBase64::Encode(PluginNonce));
	Hello.SetStringField(TEXT("project_dir"), ProjectDir);
	Hello.SetStringField(TEXT("mac"), FBase64::Encode(HelloMac(Secret, PluginNonce, ProjectDir)));
}

bool Verify(const TArray<uint8>& Secret, const TCHAR* Role, const TArray<uint8>& PluginNonce, const TArray<uint8>& BrainNonce, const TArray<uint8>& Proof)
{
	const TArray<uint8> Expected = Prove(Secret, Role, PluginNonce, BrainNonce);
	if (Proof.Num() != Expected.Num())
	{
		return false;
	}
	uint8 Difference = 0;
	for (int32 i = 0; i < Expected.Num(); ++i)
	{
		Difference |= Expected[i] ^ Proof[i];
	}
	return Difference == 0;
}
}
