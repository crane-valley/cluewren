#include "PlaytestBrain.h"

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

FString FilePath()
{
	return FString(FPlatformProcess::UserSettingsDir()) / TEXT("PlaytestAgent") / TEXT("brain.json");
}

FString Read(const FString& Path, FAddress& Out)
{
	if (!FPaths::FileExists(Path))
	{
		return TEXT("No brain is running on this machine");
	}
	if (!OwnedByThisUser(FPaths::GetPath(Path)) || !OwnedByThisUser(Path))
	{
		return FString::Printf(TEXT("%s isn't owned by this user: refused"), *Path);
	}
	FString Text;
	if (!FFileHelper::LoadFileToString(Text, *Path))
	{
		return FString::Printf(TEXT("%s can't be read"), *Path);
	}
	const TSharedPtr<FJsonObject> Said = PlaytestJson::Parse(Text);
	if (!Said.IsValid())
	{
		return FString::Printf(TEXT("%s isn't a brain announcement"), *Path);
	}
	FString Secret, Url;
	double Pid = 0;
	Said->TryGetStringField(TEXT("secret"), Secret);
	Said->TryGetStringField(TEXT("url"), Url);
	Said->TryGetNumberField(TEXT("pid"), Pid);
	if (!FBase64::Decode(Secret, Out.Secret) || Out.Secret.Num() < SecretBytes)
	{
		return FString::Printf(TEXT("%s has no usable secret"), *Path);
	}
	if (Pid <= 0 || !FPlatformProcess::IsApplicationRunning((uint32)Pid))
	{
		return TEXT("The brain in brain.json isn't running");
	}
	return CheckUrl(Url, Out);
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
