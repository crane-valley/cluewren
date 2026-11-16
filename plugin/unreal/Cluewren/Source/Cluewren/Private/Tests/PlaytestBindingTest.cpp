// How an editor and a brain find each other and what the editor takes from it (the advert, the invite, the hello, a
// refusal, a resumed session), with the same known answers as the Unity and Godot tests: Automation RunTests Cluewren.Binding
#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "Dom/JsonObject.h"
#include "Dom/JsonValue.h"
#include "HAL/FileManager.h"
#include "HAL/PlatformMisc.h"
#include "HAL/PlatformProcess.h"
#include "Misc/Base64.h"
#include "Misc/FileHelper.h"
#include "Misc/Guid.h"
#include "Misc/Paths.h"
#include "PlaytestBrain.h"
#include "PlaytestJson.h"
#include "PlaytestSubsystem.h"

#include "Windows/AllowWindowsPlatformTypes.h"
#include <aclapi.h>
#include "Windows/HideWindowsPlatformTypes.h"

namespace
{
	using PlaytestBrain::EInvite;

	// What an elevated administrator writes is owned by the Administrators group, which the plugin rightly refuses: a
	// test's own invite is given to this process's token user, the one PlaytestBrain's check compares with.
	bool MakeMine(const FString& Path)
	{
		HANDLE Token = nullptr;
		if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &Token))
		{
			return false;
		}
		DWORD Size = 0;
		GetTokenInformation(Token, TokenUser, nullptr, 0, &Size);
		TArray<uint8> Buffer;
		Buffer.SetNumUninitialized(Size);
		bool bMine = GetTokenInformation(Token, TokenUser, Buffer.GetData(), Size, &Size) != 0;
		CloseHandle(Token);
		if (!bMine)
		{
			return false;
		}
		const PSID Me = reinterpret_cast<TOKEN_USER*>(Buffer.GetData())->User.Sid;
		// Already this user's where the user is no administrator, and then the folder may not let its owner be set at all.
		PSID Owner = nullptr;
		PSECURITY_DESCRIPTOR Descriptor = nullptr;
		if (GetNamedSecurityInfoW(*Path, SE_FILE_OBJECT, OWNER_SECURITY_INFORMATION, &Owner, nullptr, nullptr, nullptr, &Descriptor) == ERROR_SUCCESS)
		{
			bMine = EqualSid(Owner, Me) != 0;
			LocalFree(Descriptor);
			if (bMine)
			{
				return true;
			}
		}
		return SetNamedSecurityInfoW(const_cast<TCHAR*>(*Path), SE_FILE_OBJECT, OWNER_SECURITY_INFORMATION, Me, nullptr, nullptr, nullptr) == ERROR_SUCCESS;
	}

	const TCHAR* const KnownMac = TEXT("b0e5b16410e69ae2e7ab99cb08045c3b0f6ac8cf250e9898d151a05c964a31a3");
	const TCHAR* const KnownProject = TEXT("C:\\Users\\Dev\\Projects\\Gr\u00f6\u00dfe");
	const TCHAR* const OtherRelease = TEXT("The cluewren command and this plugin are of different releases: install the same release of both");

	TArray<uint8> Range(int32 From, int32 Count = 32)
	{
		TArray<uint8> Bytes;
		for (int32 i = 0; i < Count; ++i)
		{
			Bytes.Add((uint8)(From + i));
		}
		return Bytes;
	}

	FString TestFolder()
	{
		return FPaths::ConvertRelativePathToFull(FPaths::ProjectIntermediateDir() / TEXT("CluewrenBindingTest") / FGuid::NewGuid().ToString());
	}

	TSharedRef<FJsonObject> Invite(const FString& EditorId, int32 Protocol, const TArray<uint8>& Secret, uint32 Pid, const TCHAR* Url)
	{
		TSharedRef<FJsonObject> Said = MakeShared<FJsonObject>();
		Said->SetStringField(TEXT("url"), Url);
		Said->SetStringField(TEXT("secret"), FBase64::Encode(Secret));
		Said->SetNumberField(TEXT("pid"), Pid);
		Said->SetNumberField(TEXT("born"), 1);
		Said->SetStringField(TEXT("started"), TEXT("2026-01-01T00:00:00+00:00"));
		Said->SetNumberField(TEXT("protocol"), Protocol);
		Said->SetStringField(TEXT("editor_id"), EditorId);
		return Said;
	}

	uint32 NoProcess()
	{
		for (uint32 Pid = 0x7ffffff0; Pid > 0x7fffff00; Pid -= 4)
		{
			if (!FPlatformProcess::IsApplicationRunning(Pid))
			{
				return Pid;
			}
		}
		return 0;
	}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FCluewrenBindingHelloTest, "Cluewren.Binding.Hello",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FCluewrenBindingHelloTest::RunTest(const FString& Parameters)
{
	const TArray<uint8> Mac = PlaytestBrain::HelloMac(Range(0), Range(32), KnownProject);
	TestEqual(TEXT("the hello's MAC"), BytesToHex(Mac.GetData(), Mac.Num()).ToLower(), FString(KnownMac));

	TSharedRef<FJsonObject> Hello = MakeShared<FJsonObject>();
	PlaytestBrain::Sign(*Hello, Range(0), Range(32), KnownProject);
	TestEqual(TEXT("the hello speaks protocol 2"), Hello->GetNumberField(TEXT("protocol")), 2.0);
	TestEqual(TEXT("the hello's nonce"), Hello->GetStringField(TEXT("nonce")), FBase64::Encode(Range(32)));
	TestEqual(TEXT("the hello's project_dir"), Hello->GetStringField(TEXT("project_dir")), FString(KnownProject));
	TestEqual(TEXT("the hello's mac"), Hello->GetStringField(TEXT("mac")), FBase64::Encode(Mac));
	return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FCluewrenBindingDriveRootTest, "Cluewren.Binding.DriveRoot",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FCluewrenBindingDriveRootTest::RunTest(const FString& Parameters)
{
	TestEqual(TEXT("a drive root keeps its separator"), PlaytestBrain::ProjectDirOf(TEXT("C:/")), FString(TEXT("C:/")));
	TestEqual(TEXT("a folder loses its trailing separator"), PlaytestBrain::ProjectDirOf(TEXT("C:/Games/Foo/")), FString(TEXT("C:/Games/Foo")));
	return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FCluewrenBindingAdvertTest, "Cluewren.Binding.Advert",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FCluewrenBindingAdvertTest::RunTest(const FString& Parameters)
{
	const FString Id = PlaytestBrain::EditorId();
	TestEqual(TEXT("the id is 32 characters"), Id.Len(), 32);
	for (const TCHAR C : Id)
	{
		TestTrue(TEXT("the id is lowercase hex"), (C >= TEXT('0') && C <= TEXT('9')) || (C >= TEXT('a') && C <= TEXT('f')));
	}
	TestEqual(TEXT("the id is made once per process"), PlaytestBrain::EditorId(), Id);

	const FString Folder = TestFolder();
	PlaytestBrain::FAdvert Advert;
	Advert.Pid = 1234;
	Advert.Id = Id;
	Advert.EngineVersion = TEXT("5.8.0");
	Advert.PluginVersion = TEXT("0.2.0");
	Advert.ProjectDir = KnownProject;
	Advert.bEditor = true;
	TestEqual(TEXT("the advert's file"), PlaytestBrain::AdvertPath(Folder, 1234), Folder / TEXT("editors") / TEXT("1234.json"));
	TestEqual(TEXT("the invite's file"), PlaytestBrain::InvitePath(Folder, 1234), Folder / TEXT("invites") / TEXT("1234.json"));
	for (int32 Time = 0; Time < 2; ++Time)
	{
		TestTrue(TEXT("the advert is written, and written over"), PlaytestBrain::WriteAdvert(Folder, Advert));
	}
	TArray<uint8> Raw;
	FFileHelper::LoadFileToArray(Raw, *PlaytestBrain::AdvertPath(Folder, 1234));
	TestTrue(TEXT("the advert has no byte order mark"), Raw.Num() > 0 && Raw[0] == '{');
	FString Text;
	FFileHelper::LoadFileToString(Text, *PlaytestBrain::AdvertPath(Folder, 1234));
	const TSharedPtr<FJsonObject> Said = PlaytestJson::Parse(Text);
	if (TestTrue(TEXT("the advert is a JSON object"), Said.IsValid()))
	{
		TestEqual(TEXT("nothing but the advert's fields"), Said->Values.Num(), 9);
		TestEqual(TEXT("pid"), Said->GetNumberField(TEXT("pid")), 1234.0);
		TestEqual(TEXT("id"), Said->GetStringField(TEXT("id")), Id);
		TestEqual(TEXT("engine"), Said->GetStringField(TEXT("engine")), FString(TEXT("unreal")));
		TestEqual(TEXT("engine_version"), Said->GetStringField(TEXT("engine_version")), FString(TEXT("5.8.0")));
		TestEqual(TEXT("plugin_version"), Said->GetStringField(TEXT("plugin_version")), FString(TEXT("0.2.0")));
		TestEqual(TEXT("protocol_min"), Said->GetNumberField(TEXT("protocol_min")), 2.0);
		TestEqual(TEXT("protocol_max"), Said->GetNumberField(TEXT("protocol_max")), 2.0);
		TestEqual(TEXT("project_dir"), Said->GetStringField(TEXT("project_dir")), FString(KnownProject));
		TestEqual(TEXT("host"), Said->GetStringField(TEXT("host")), FString(TEXT("editor")));
	}
	Advert.bEditor = false;
	TestEqual(TEXT("a game's host"), PlaytestBrain::AdvertJson(Advert)->GetStringField(TEXT("host")), FString(TEXT("game")));
	PlaytestBrain::RemoveAdvert(Folder, 1234);
	TestFalse(TEXT("the advert is removed"), FPaths::FileExists(PlaytestBrain::AdvertPath(Folder, 1234)));
	IFileManager::Get().DeleteDirectory(*Folder, false, true);
	return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FCluewrenBindingInviteTest, "Cluewren.Binding.Invite",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FCluewrenBindingInviteTest::RunTest(const FString& Parameters)
{
	const FString Folder = TestFolder();
	const uint32 Me = FPlatformProcess::GetCurrentProcessId();
	const uint32 Gone = NoProcess();
	const FString Path = PlaytestBrain::InvitePath(Folder, Me);
	const FString Id = PlaytestBrain::EditorId();
	const FString Stranger = FString::ChrN(32, TEXT('0'));
	const TCHAR* const Good = TEXT("ws://127.0.0.1:1/v1/bridge");
	const TCHAR* const Elsewhere = TEXT("ws://example.com:1/v1/bridge");
	if (!TestTrue(TEXT("a process id nothing runs under"), Gone != 0))
	{
		return false;
	}

	PlaytestBrain::FAddress Address;
	FString Why;
	const auto Read = [&](const FString& Text)
	{
		Address = PlaytestBrain::FAddress();
		if (!FFileHelper::SaveStringToFile(Text, *Path, FFileHelper::EEncodingOptions::ForceUTF8WithoutBOM)
			|| !MakeMine(FPaths::GetPath(Path)) || !MakeMine(Path))
		{
			AddError(TEXT("the test's invite couldn't be written as this user's"));
		}
		return PlaytestBrain::ReadInvite(Path, Id, Address, Why);
	};
	const auto Said = [&](const TSharedRef<FJsonObject>& Written) { return Read(PlaytestJson::Write(Written)); };
	const auto Is = [this](const TCHAR* What, EInvite Verdict, EInvite Expected) { TestEqual(What, (int32)Verdict, (int32)Expected); };

	Is(TEXT("no invite"), PlaytestBrain::ReadInvite(Path, Id, Address, Why), EInvite::Unreadable);

	// Each invite is wrong in everything checked after what it is named for: the first failure is the verdict.
	Is(TEXT("not JSON"), Read(TEXT("ws://127.0.0.1:1/v1/bridge")), EInvite::NotAnInvite);
	Is(TEXT("not a JSON object"), Read(TEXT("[]")), EInvite::NotAnInvite);

	Is(TEXT("another editor's invite"), Said(Invite(Stranger, 3, Range(0, 8), Gone, Elsewhere)), EInvite::OtherEditor);
	TestEqual(TEXT("another editor's invite is ignored without a word"), Why, FString());

	Is(TEXT("another protocol"), Said(Invite(Id, 3, Range(0, 8), Gone, Elsewhere)), EInvite::OtherRelease);
	TestEqual(TEXT("another protocol's words"), Why, FString(OtherRelease));

	Is(TEXT("a short secret"), Said(Invite(Id, 2, Range(0, 31), Gone, Elsewhere)), EInvite::NoSecret);

	Is(TEXT("a brain that is gone"), Said(Invite(Id, 2, Range(0), Gone, Elsewhere)), EInvite::Ended);
	TestEqual(TEXT("a brain that is gone: the words"), Why, FString(TEXT("The run this editor was invited to has ended")));

	Is(TEXT("a URL elsewhere"), Said(Invite(Id, 2, Range(0), Me, Elsewhere)), EInvite::BadUrl);
	Is(TEXT("a URL with userinfo"), Said(Invite(Id, 2, Range(0), Me, TEXT("ws://127.0.0.1:1@example.com/v1/bridge"))), EInvite::BadUrl);
	Is(TEXT("a URL over TLS"), Said(Invite(Id, 2, Range(0), Me, TEXT("wss://127.0.0.1:1/v1/bridge"))), EInvite::BadUrl);
	TestEqual(TEXT("no address from a refused invite"), Address.Port, 0);
	TestEqual(TEXT("no secret from a refused invite"), Address.Secret.Num(), 0);

	Is(TEXT("this editor's invite"), Said(Invite(Id, 2, Range(0), Me, Good)), EInvite::Taken);
	TestEqual(TEXT("its words"), Why, FString());
	TestEqual(TEXT("its host"), Address.Host, FString(TEXT("127.0.0.1")));
	TestEqual(TEXT("its port"), Address.Port, 1);
	TestEqual(TEXT("its path"), Address.Path, FString(TEXT("/v1/bridge")));
	TestTrue(TEXT("its secret"), Address.Secret == Range(0));
	TestTrue(TEXT("its brain is running"), Address.Pid == Me && PlaytestBrain::Alive(Address));
	Is(TEXT("IPv6 loopback"), Said(Invite(Id, 2, Range(0), Me, TEXT("ws://[::1]:65535/v1/bridge"))), EInvite::Taken);
	TestEqual(TEXT("its port"), Address.Port, 65535);

	// An invite written in the same second as the one before it is another invite: the engine's own file times would not tell.
	TSet<uint64> Stamps;
	for (int32 Time = 0; Time < 5; ++Time)
	{
		FFileHelper::SaveStringToFile(TEXT("{}"), *Path, FFileHelper::EEncodingOptions::ForceUTF8WithoutBOM);
		Stamps.Add(PlaytestBrain::InviteWritten(Path));
		FPlatformProcess::Sleep(0.05f);
	}
	TestEqual(TEXT("each of five invites written within a second has its own time"), Stamps.Num(), 5);
	TestFalse(TEXT("an invite has a time"), Stamps.Contains(0));
	TestTrue(TEXT("no invite has none"), PlaytestBrain::InviteWritten(Path + TEXT(".none")) == 0);

	// The folder's owner and kind come before what the file says: through a junction, not even that invite is read.
	IFileManager& Files = IFileManager::Get();
	const FString Invites = FPaths::GetPath(Path);
	const FString Aside = Folder / TEXT("elsewhere");
	Files.Move(*(Aside / FPaths::GetCleanFilename(Path)), *Path);
	Files.DeleteDirectory(*Invites);
	FString Link = Invites;
	FString Target = Aside;
	FPaths::MakePlatformFilename(Link);
	FPaths::MakePlatformFilename(Target);
	int32 Code = -1;
	FPlatformProcess::ExecProcess(*FPlatformMisc::GetEnvironmentVariable(TEXT("ComSpec")),
		*FString::Printf(TEXT("/c mklink /J \"%s\" \"%s\""), *Link, *Target), &Code, nullptr, nullptr);
	if (TestTrue(TEXT("a junction for the test"), Code == 0 && FPaths::FileExists(Path)))
	{
		Is(TEXT("an invite reached through a link"), PlaytestBrain::ReadInvite(Path, Id, Address, Why), EInvite::NotThisUsers);
		TestTrue(TEXT("a link's words"), Why.EndsWith(TEXT("isn't owned by this user: refused")));
	}
	Files.DeleteDirectory(*Invites);

	Files.DeleteDirectory(*Folder, false, true);
	return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FCluewrenBindingRefusalsTest, "Cluewren.Binding.Refusals",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FCluewrenBindingRefusalsTest::RunTest(const FString& Parameters)
{
	using PlaytestBrain::RefusalText;
	TestEqual(TEXT("protocol"), RefusalText(TEXT("protocol")), FString(OtherRelease));
	TestEqual(TEXT("not_authenticated"), RefusalText(TEXT("not_authenticated")), FString(TEXT("The brain did not accept this editor's proof of the invite")));
	TestEqual(TEXT("project"), RefusalText(TEXT("project")), FString(TEXT("The run is for another project than this editor's")));
	TestEqual(TEXT("resume"), RefusalText(TEXT("resume")), FString(TEXT("The session this editor tried to resume belongs to another project or engine")));
	TestEqual(TEXT("hello"), RefusalText(TEXT("hello")), FString(TEXT("The brain refused the connection (hello)")));
	TestEqual(TEXT("proof"), RefusalText(TEXT("proof")), FString(TEXT("The brain refused the connection (proof)")));
	TestEqual(TEXT("a code of a later release"), RefusalText(TEXT("busy_2")), FString(TEXT("The brain refused the connection (busy_2)")));
	for (const TCHAR* NoCode : {TEXT(""), TEXT("Install this: http://example.com"), TEXT("a_code_longer_than_any_code_the_brain_sends")})
	{
		TestEqual(*FString::Printf(TEXT("no code (%s)"), NoCode), RefusalText(NoCode), FString(PlaytestBrain::Unproven));
	}

	using PlaytestBrain::RefusalOfClose;
	TestEqual(TEXT("a refusal's close without its frame"), RefusalOfClose(1008, TEXT("project")), RefusalText(TEXT("project")));
	TestEqual(TEXT("a refusal's close with a code of a later release"), RefusalOfClose(1008, TEXT("busy_2")),
		FString(TEXT("The brain refused the connection (busy_2)")));
	TestEqual(TEXT("a refusal's close whose reason is no code"), RefusalOfClose(1008, TEXT("Install this: http://example.com")),
		FString(PlaytestBrain::Unproven));
	TestEqual(TEXT("a refusal's close with no reason"), RefusalOfClose(1008, TEXT("")), FString(PlaytestBrain::Unproven));
	for (const int32 Status : {0, 1000, 1001, 1011, 1012})
	{
		TestEqual(*FString::Printf(TEXT("a close with %d is no refusal"), Status), RefusalOfClose(Status, TEXT("project")), FString());
	}
	return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FCluewrenBindingSessionTest, "Cluewren.Binding.Session",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FCluewrenBindingSessionTest::RunTest(const FString& Parameters)
{
	const auto Kept = [](const TCHAR* Id)
	{
		UPlaytestSubsystem::FSession Session;
		Session.Id = Id;
		Session.Asked = Id;
		Session.Received = 7;
		Session.Held.Add({MakeShared<FJsonObject>(), {}});
		return Session;
	};

	UPlaytestSubsystem::FSession Resumed = Kept(TEXT("local-a"));
	TestTrue(TEXT("the session asked for is resumed"), Resumed.Welcomed(TEXT("local-a")));
	TestEqual(TEXT("a resume keeps what was received"), Resumed.Received, (int64)7);
	TestEqual(TEXT("a resume keeps the held answers to send"), Resumed.Held.Num(), 1);

	UPlaytestSubsystem::FSession Another = Kept(TEXT("local-a"));
	TestFalse(TEXT("another session is a new one"), Another.Welcomed(TEXT("local-b")));
	TestEqual(TEXT("a new session has received nothing"), Another.Received, (int64)0);
	TestEqual(TEXT("a new session drops the held answers"), Another.Held.Num(), 0);
	TestEqual(TEXT("a new session's id"), Another.Id, FString(TEXT("local-b")));

	UPlaytestSubsystem::FSession First = Kept(TEXT(""));
	TestFalse(TEXT("no session asked for is a new one"), First.Welcomed(TEXT("")));
	TestEqual(TEXT("with nothing received"), First.Received, (int64)0);
	TestEqual(TEXT("and nothing held"), First.Held.Num(), 0);
	return true;
}

#endif
