// Which brain an editor takes (docs/adr/0015), with the same vectors and known answer as the Unity and Godot tests:
// Automation RunTests Cluewren.Binding
#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "Dom/JsonObject.h"
#include "Dom/JsonValue.h"
#include "HAL/FileManager.h"
#include "HAL/PlatformProcess.h"
#include "Misc/Base64.h"
#include "Misc/FileHelper.h"
#include "Misc/Guid.h"
#include "Misc/Paths.h"
#include "PlaytestBrain.h"
#include "Policies/CondensedJsonPrintPolicy.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"

namespace
{
	struct FVector19
	{
		const TCHAR* Name;
		const TCHAR* Match;
		TArray<const TCHAR*> Folders;
		bool bAbsent;
		const TCHAR* Project;
		bool bTaken;
	};

	const TCHAR* const KnownMac = TEXT("b0e5b16410e69ae2e7ab99cb08045c3b0f6ac8cf250e9898d151a05c964a31a3");

	TSharedRef<FJsonObject> Said(const TCHAR* Match, const TArray<const TCHAR*>& Folders, bool bAbsent)
	{
		TSharedRef<FJsonObject> Said = MakeShared<FJsonObject>();
		Said->SetStringField(TEXT("match"), Match);
		if (!bAbsent)
		{
			TArray<TSharedPtr<FJsonValue>> List;
			for (const TCHAR* Folder : Folders)
			{
				List.Add(MakeShared<FJsonValueString>(Folder));
			}
			Said->SetArrayField(TEXT("folders"), List);
		}
		return Said;
	}

	TArray<uint8> Range(int32 From)
	{
		TArray<uint8> Bytes;
		for (int32 i = 0; i < 32; ++i)
		{
			Bytes.Add((uint8)(From + i));
		}
		return Bytes;
	}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FCluewrenBindingVectorsTest, "Cluewren.Binding.Vectors",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FCluewrenBindingVectorsTest::RunTest(const FString& Parameters)
{
	const FVector19 Vectors[] = {
		{TEXT("1"), TEXT("related"), {TEXT("C:\\Games\\Foo")}, false, TEXT("C:\\Games\\Foo"), true},
		{TEXT("2"), TEXT("related"), {TEXT("C:\\Games\\Foo")}, false, TEXT("C:\\Games\\Foo\\"), true},
		{TEXT("3"), TEXT("related"), {TEXT("C:\\Games\\Foo")}, false, TEXT("c:/games/foo"), true},
		{TEXT("4"), TEXT("related"), {TEXT("C:\\Games")}, false, TEXT("C:\\Games\\Foo"), true},
		{TEXT("5"), TEXT("related"), {TEXT("C:\\Games\\Foo\\Assets\\Scripts")}, false, TEXT("C:\\Games\\Foo"), true},
		{TEXT("6"), TEXT("related"), {TEXT("C:\\Games\\Foo")}, false, TEXT("C:\\Games\\FooBar"), false},
		{TEXT("7"), TEXT("related"), {TEXT("C:\\Games\\FooBar")}, false, TEXT("C:\\Games\\Foo"), false},
		{TEXT("8"), TEXT("related"), {TEXT("C:\\")}, false, TEXT("C:\\Games\\Foo"), true},
		{TEXT("9"), TEXT("related"), {TEXT("C:\\Games\\Foo")}, false, TEXT("D:\\Games\\Foo"), false},
		{TEXT("10"), TEXT("exact"), {TEXT("C:\\Games")}, false, TEXT("C:\\Games\\Foo"), false},
		{TEXT("11"), TEXT("exact"), {TEXT("C:\\Games\\Foo")}, false, TEXT("C:\\Games\\Foo\\"), true},
		{TEXT("12"), TEXT("exact"), {TEXT("C:\\Games\\Foo\\Assets")}, false, TEXT("C:\\Games\\Foo"), false},
		{TEXT("13"), TEXT("related"), {TEXT("C:\\Link\\Foo"), TEXT("C:\\Games\\Foo")}, false, TEXT("C:\\Games\\Foo"), true},
		{TEXT("14"), TEXT("related"), {TEXT("C:\\Games\\Foo")}, false, TEXT("S:\\Foo"), false},
		{TEXT("15"), TEXT("related"), {TEXT("C:\\Games\\LongName")}, false, TEXT("C:\\Games\\LONGNA~1"), false},
		{TEXT("16"), TEXT("exact"), {TEXT("S:\\Foo"), TEXT("C:\\Games\\Foo")}, false, TEXT("S:\\Foo"), true},
		{TEXT("17 absent"), TEXT("related"), {}, true, TEXT("C:\\Games\\Foo"), true},
		{TEXT("17 empty"), TEXT("related"), {}, false, TEXT("C:\\Games\\Foo"), true},
		{TEXT("18 empty"), TEXT("related"), {TEXT("C:\\Games\\Foo")}, false, TEXT(""), false},
		{TEXT("18 relative"), TEXT("related"), {TEXT("C:\\Games\\Foo")}, false, TEXT("Games\\Foo"), false},
		{TEXT("19"), TEXT("nearby"), {TEXT("C:\\Games\\Foo")}, false, TEXT("C:\\Games\\Foo"), false},
	};
	for (const FVector19& V : Vectors)
	{
		const FString Why = PlaytestBrain::ForAnotherProject(*Said(V.Match, V.Folders, V.bAbsent), V.Project);
		TestEqual(*FString::Printf(TEXT("vector %s (%s)"), V.Name, *Why), Why.IsEmpty(), V.bTaken);
	}
	return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FCluewrenBindingWordsTest, "Cluewren.Binding.Words",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FCluewrenBindingWordsTest::RunTest(const FString& Parameters)
{
	TestEqual(TEXT("the verdict's words"),
		PlaytestBrain::ForAnotherProject(*Said(TEXT("related"), {TEXT("C:\\Link\\Foo"), TEXT("C:\\Games\\Foo")}, false), TEXT("C:\\Games\\Bar")),
		FString(TEXT("For another project: the run is for \"C:\\Link\\Foo\" or \"C:\\Games\\Foo\" (match \"related\"), this project is \"C:\\Games\\Bar\"")));
	return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FCluewrenBindingHelloMacTest, "Cluewren.Binding.HelloMac",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FCluewrenBindingHelloMacTest::RunTest(const FString& Parameters)
{
	const TArray<uint8> Mac = PlaytestBrain::HelloMac(Range(0), Range(32), TEXT("C:\\Users\\Dev\\Projects\\Gr\u00f6\u00dfe"));
	TestEqual(TEXT("the hello's MAC"), BytesToHex(Mac.GetData(), Mac.Num()).ToLower(), FString(KnownMac));
	return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FCluewrenBindingReadTest, "Cluewren.Binding.Read",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FCluewrenBindingReadTest::RunTest(const FString& Parameters)
{
	const FString Dir = FPaths::ConvertRelativePathToFull(FPaths::ProjectIntermediateDir() / TEXT("CluewrenBindingTest") / FGuid::NewGuid().ToString());
	const FString Other = Dir + TEXT("-other");
	const FString Path = Dir / TEXT("brain.json");
	TSharedRef<FJsonObject> Brain = Said(TEXT("related"), {*Other}, false);
	Brain->SetStringField(TEXT("url"), TEXT("ws://127.0.0.1:1/v1/bridge"));
	Brain->SetStringField(TEXT("secret"), FBase64::Encode(Range(0)));
	Brain->SetNumberField(TEXT("pid"), FPlatformProcess::GetCurrentProcessId());
	FString Text;
	FJsonSerializer::Serialize(Brain, TJsonWriterFactory<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>::Create(&Text));
	if (!TestTrue(TEXT("brain.json written"), FFileHelper::SaveStringToFile(Text, *Path, FFileHelper::EEncodingOptions::ForceUTF8WithoutBOM)))
	{
		return false;
	}
	PlaytestBrain::FAddress Address;
	const FString Why = PlaytestBrain::Read(Path, Dir, Address);
	TestTrue(TEXT("a verdict naming both folders"), Why.Contains(Other) && Why.Contains(Dir));
	TestEqual(TEXT("no address for another project"), Address.Port, 0);
	PlaytestBrain::FAddress Taken;
	TestEqual(TEXT("this project's brain is taken"), PlaytestBrain::Read(Path, Other, Taken), FString());
	TestEqual(TEXT("its port"), Taken.Port, 1);
	IFileManager::Get().DeleteDirectory(*Dir, false, true);
	return true;
}

#endif
