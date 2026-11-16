// rc's refusals by category (docs/adr/0014), in the editor: Automation RunTests Cluewren.Rc
#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "HAL/FileManager.h"
#include "PlaytestJson.h"
#include "PlaytestLevels.h"
#include "PlaytestRefusal.h"
#include "Misc/EngineVersionComparison.h"
#include "Misc/Paths.h"
#include "UObject/Class.h"
#include "UObject/Package.h"
#if !UE_VERSION_OLDER_THAN(5, 5, 0)
#include "UObject/StrProperty.h"
#endif
#include "UObject/StructOnScope.h"
#include "UObject/TextProperty.h"
#include "UObject/UObjectIterator.h"
#include "UObject/UnrealType.h"

#include "Windows/AllowWindowsPlatformTypes.h"
#include "Windows/WindowsHWrapper.h"
#include "Windows/HideWindowsPlatformTypes.h"

namespace
{
	using PlaytestRefusal::EOp;

	bool PathLike(const FString& Name)
	{
		static const TSet<FString> Words = {TEXT("path"), TEXT("paths"), TEXT("file"), TEXT("files"), TEXT("filename"),
			TEXT("dir"), TEXT("directory"), TEXT("folder"), TEXT("url"), TEXT("uri"), TEXT("address"), TEXT("host"),
			TEXT("hostname"), TEXT("command"), TEXT("cmd"), TEXT("exec"), TEXT("script"), TEXT("ip")};
		FString Word;
		for (int32 i = 0; i <= Name.Len(); ++i)
		{
			const bool bEnd = i == Name.Len() || Name[i] == TEXT('_')
				|| (i > 0 && FChar::IsUpper(Name[i]) && !FChar::IsUpper(Name[i - 1]));
			if (bEnd && !Word.IsEmpty())
			{
				if (Words.Contains(Word.ToLower()))
				{
					return true;
				}
				Word.Reset();
			}
			if (i < Name.Len() && Name[i] != TEXT('_'))
			{
				Word.AppendChar(Name[i]);
			}
		}
		return false;
	}

	bool StringLike(const FProperty* Property)
	{
		return CastField<FStrProperty>(Property) || CastField<FNameProperty>(Property) || CastField<FTextProperty>(Property);
	}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FCluewrenRcRefusalTest, "Cluewren.Rc.Refusals",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FCluewrenRcRefusalTest::RunTest(const FString& Parameters)
{
	using PlaytestRefusal::Refused;
	using PlaytestRefusal::RefusedValue;
	using PlaytestRefusal::DataPath;
	const auto Class = [this](const TCHAR* Path) -> UClass*
	{
		UClass* Found = FindObject<UClass>(nullptr, Path);
		if (!Found)
		{
			AddError(FString::Printf(TEXT("no class %s"), Path));
		}
		return Found;
	};
	const auto Refuses = [this](const FString& What, const UObject* Object, const TCHAR* Member, EOp Op)
	{
		TestTrue(What + TEXT(" is refused"), Object && !Refused(Object, Member ? FName(Member) : NAME_None, Op).IsEmpty());
	};
	const auto Allows = [this](const FString& What, const UObject* Object, const TCHAR* Member, EOp Op)
	{
		const FString Why = Object ? Refused(Object, Member ? FName(Member) : NAME_None, Op) : TEXT("no object");
		TestTrue(What + TEXT(" stays reachable: ") + Why, Why.IsEmpty());
	};

	// The Engine module's classes are always there; the others come from plugins a project may leave out.
	static const TSet<FName> EngineClasses = {TEXT("KismetSystemLibrary"), TEXT("GameplayStatics"), TEXT("PlayerController"),
		TEXT("PlayerInput"), TEXT("CheatManager"), TEXT("KismetRenderingLibrary"), TEXT("KismetStringTableLibrary"),
		TEXT("DataTableFunctionLibrary"), TEXT("SoundSubmix")};
	for (const TPair<FName, TSet<FName>>& Pair : PlaytestRefusal::RefusedFunctions())
	{
		UClass* Owner = FindFirstObject<UClass>(*Pair.Key.ToString(), EFindFirstObjectOptions::NativeFirst);
		if (!Owner && !EngineClasses.Contains(Pair.Key))
		{
			AddInfo(FString::Printf(TEXT("%s isn't loaded here"), *Pair.Key.ToString()));
			continue;
		}
		TestNotNull(FString::Printf(TEXT("class %s exists"), *Pair.Key.ToString()), Owner);
		for (const FName Function : Pair.Value)
		{
			TestTrue(FString::Printf(TEXT("%s.%s exists"), *Pair.Key.ToString(), *Function.ToString()),
				Owner && Owner->FindFunctionByName(Function));
		}
	}

	UClass* System = Class(TEXT("/Script/Engine.KismetSystemLibrary"));
	UClass* Statics = Class(TEXT("/Script/Engine.GameplayStatics"));
	UClass* Controller = Class(TEXT("/Script/Engine.PlayerController"));
	UClass* Cheats = Class(TEXT("/Script/Engine.CheatManager"));
	UClass* PlaySettings = Class(TEXT("/Script/UnrealEd.LevelEditorPlaySettings"));
	UObject* SystemDefaults = System ? System->GetDefaultObject() : nullptr;
	UObject* StaticsDefaults = Statics ? Statics->GetDefaultObject() : nullptr;
	if (!System || !Statics || !Controller || !Cheats || !PlaySettings)
	{
		return false;
	}

	Refuses(TEXT("C1 KismetSystemLibrary.LaunchURL"), SystemDefaults, TEXT("LaunchURL"), EOp::Call);
	Refuses(TEXT("C1 KismetSystemLibrary.QuitEditor"), SystemDefaults, TEXT("QuitEditor"), EOp::Call);

	Refuses(TEXT("C2 GameplayStatics.SaveGameToSlot"), StaticsDefaults, TEXT("SaveGameToSlot"), EOp::Call);
	TestNull(TEXT("C2 GameplayStatics.SaveDataToSlot is no UFUNCTION"), Statics->FindFunctionByName(TEXT("SaveDataToSlot")));
	Refuses(TEXT("C2 BlueprintPathsLibrary"), Class(TEXT("/Script/Engine.BlueprintPathsLibrary")), TEXT("FileExists"), EOp::Call);
	if (UClass* FileSource = FindObject<UClass>(nullptr, TEXT("/Script/MediaAssets.FileMediaSource")))
	{
		Refuses(TEXT("C2 FileMediaSource.FilePath write"), NewObject<UObject>(GetTransientPackage(), FileSource), TEXT("FilePath"),
			EOp::Write);
	}
	Refuses(TEXT("C2 KismetRenderingLibrary.ExportRenderTarget"), Class(TEXT("/Script/Engine.KismetRenderingLibrary")),
		TEXT("ExportRenderTarget"), EOp::Call);
	Refuses(TEXT("C2 DataTableFunctionLibrary.FillDataTableFromCSVFile"), Class(TEXT("/Script/Engine.DataTableFunctionLibrary")),
		TEXT("FillDataTableFromCSVFile"), EOp::Call);
	if (UClass* Files = FindObject<UClass>(nullptr, TEXT("/Script/BlueprintFileUtils.BlueprintFileUtilsBPLibrary")))
	{
		Refuses(TEXT("C2 BlueprintFileUtilsBPLibrary"), Files, nullptr, EOp::Argument);
	}

	Refuses(TEXT("C3 GameplayStatics.OpenLevel"), StaticsDefaults, TEXT("OpenLevel"), EOp::Call);
	Refuses(TEXT("C3 PlayerController.ClientTravel"), Controller, TEXT("ClientTravel"), EOp::Call);

	Refuses(TEXT("C4 KismetSystemLibrary.SetIntPropertyByName"), SystemDefaults, TEXT("SetIntPropertyByName"), EOp::Call);
	Refuses(TEXT("C4 KismetSystemLibrary.SetObjectPropertyByName"), SystemDefaults, TEXT("SetObjectPropertyByName"), EOp::Call);
	Refuses(TEXT("C4 KismetSystemLibrary.K2_SetTimer"), SystemDefaults, TEXT("K2_SetTimer"), EOp::Call);
	if (UFunction* Timer = System->FindFunctionByName(TEXT("K2_SetTimerDelegate")))
	{
		FStructOnScope Scope(Timer);
		if (FDelegateProperty* Delegate = CastField<FDelegateProperty>(Timer->FindPropertyByName(TEXT("Delegate"))))
		{
			void* Value = Delegate->ContainerPtrToValuePtr<void>(Scope.GetStructMemory());
			TestTrue(TEXT("C4 an unbound delegate stays"), RefusedValue(Delegate, Value).IsEmpty());
			Delegate->GetPropertyValuePtr(Value)->BindUFunction(SystemDefaults, TEXT("PrintString"));
			TestFalse(TEXT("C4 a bound delegate is refused"), RefusedValue(Delegate, Value).IsEmpty());
		}
		else
		{
			AddError(TEXT("K2_SetTimerDelegate has no Delegate parameter"));
		}
	}
	if (UClass* Python = FindObject<UClass>(nullptr, TEXT("/Script/PythonScriptPlugin.PythonScriptLibrary")))
	{
		Refuses(TEXT("C4 PythonScriptLibrary"), Python->GetDefaultObject(), TEXT("ExecutePythonCommand"), EOp::Call);
	}

	TestTrue(TEXT("C5 UnrealEd is flagged editor-only"), PlaySettings->GetPackage()->HasAnyPackageFlags(PKG_EditorOnly));
	Refuses(TEXT("C5 EditorEngine"), Class(TEXT("/Script/UnrealEd.EditorEngine")), nullptr, EOp::Argument);
	for (const uint32 Flag : {uint32(PKG_EditorOnly), uint32(PKG_Developer), uint32(PKG_UncookedOnly)})
	{
		UClass* Found = nullptr;
		for (TObjectIterator<UClass> It; It && !Found; ++It)
		{
			if (It->HasAnyClassFlags(CLASS_Native) && It->GetPackage()->HasAnyPackageFlags(Flag))
			{
				Found = *It;
			}
		}
		if (TestNotNull(FString::Printf(TEXT("C5 a class in a package flagged 0x%x"), Flag), Found))
		{
			Refuses(FString::Printf(TEXT("C5 %s (%s)"), *Found->GetName(), *Found->GetPackage()->GetName()), Found->GetDefaultObject(),
				nullptr, EOp::Read);
		}
	}

	Refuses(TEXT("C6 KismetSystemLibrary.GetCommandLine"), SystemDefaults, TEXT("GetCommandLine"), EOp::Call);
	Refuses(TEXT("C6 KismetSystemLibrary.GetDeviceId"), SystemDefaults, TEXT("GetDeviceId"), EOp::Call);

	Refuses(TEXT("C7 KismetSystemLibrary.ExecuteConsoleCommand"), SystemDefaults, TEXT("ExecuteConsoleCommand"), EOp::Call);
	TestNull(TEXT("C7 PlayerController.ConsoleCommand is no UFUNCTION"), Controller->FindFunctionByName(TEXT("ConsoleCommand")));
	Refuses(TEXT("C7 PlayerController.SendToConsole"), Controller->GetDefaultObject(), TEXT("SendToConsole"), EOp::Call);
	Refuses(TEXT("C7 PlayerInput.SetBind"), Class(TEXT("/Script/Engine.PlayerInput")), TEXT("SetBind"), EOp::Call);
	Refuses(TEXT("C7 CheatManager.CheatScript"), Cheats->GetDefaultObject(), TEXT("CheatScript"), EOp::Call);

	UObject* Play = PlaySettings->GetDefaultObject();
	Refuses(TEXT("C8 LevelEditorPlaySettings read"), Play, TEXT("PlayNetMode"), EOp::Read);
	Refuses(TEXT("C8 LevelEditorPlaySettings write"), Play, TEXT("PlayNetMode"), EOp::Write);
	Refuses(TEXT("C8 LevelEditorPlaySettings as an argument"), Play, nullptr, EOp::Argument);
	if (UFunction* Display = System->FindFunctionByName(TEXT("GetDisplayName")))
	{
		FStructOnScope Scope(Display);
		if (FObjectPropertyBase* Param = CastField<FObjectPropertyBase>(Display->FindPropertyByName(TEXT("Object"))))
		{
			void* Value = Param->ContainerPtrToValuePtr<void>(Scope.GetStructMemory());
			Param->SetObjectPropertyValue(Value, Play);
			TestFalse(TEXT("C8 LevelEditorPlaySettings as a converted value"), RefusedValue(Param, Value).IsEmpty());
			Param->SetObjectPropertyValue(Value, StaticsDefaults);
			TestTrue(TEXT("C8 another object as a converted value stays"), RefusedValue(Param, Value).IsEmpty());
		}
	}
	if (UClass* General = Class(TEXT("/Script/EngineSettings.GeneralProjectSettings")))
	{
		Refuses(TEXT("C8 GeneralProjectSettings write"), General->GetDefaultObject(), TEXT("ProjectName"), EOp::Write);
		Allows(TEXT("C8 GeneralProjectSettings read"), General->GetDefaultObject(), TEXT("ProjectName"), EOp::Read);
	}

	const FString Data = FPaths::ProjectDir() / TEXT(".cluewren/runs/x/evil.umap");
	for (const FString& Path : {Data, FPaths::ConvertRelativePathToFull(Data), FString(TEXT(".cluewren")),
		FString(TEXT(".cluewren/export/summary.txt")), FString(TEXT(".CLUEWREN\\memory\\lib.cs")), FString(TEXT("Content/../.cluewren/x")),
		FString(TEXT("/Game/../.cluewren/x"))})
	{
		TestTrue(TEXT("C9 data path ") + Path, DataPath(Path));
	}
	for (const TCHAR* Path : {TEXT("/Game/Maps/Level"), TEXT("/Script/Engine.Default__GameplayStatics"), TEXT("Saved/Logs/x.log"),
		TEXT("Saved/Cluewren/x.uasset"), TEXT("/Temp/Cluewren/runs/x/evil"), TEXT(".cluewrenx/y"),
		TEXT("/Game/Maps/Level.Level:PersistentLevel.BP_Door_C_3")})
	{
		TestFalse(FString(TEXT("C9 not data ")) + Path, DataPath(Path));
	}
	TestNull(TEXT("C9 ResolveObject refuses .cluewren"), PlaytestJson::ResolveObject(Data));
	TSharedPtr<FJsonValue> Deep = MakeShared<FJsonValueString>(TEXT(".cluewren/x.uasset"));
	TSharedPtr<FJsonValue> Flat = MakeShared<FJsonValueNumber>(1);
	for (int32 i = 0; i < 20; ++i)
	{
		Deep = MakeShared<FJsonValueArray>(TArray<TSharedPtr<FJsonValue>>{Deep});
		Flat = MakeShared<FJsonValueArray>(TArray<TSharedPtr<FJsonValue>>{Flat});
	}
	TestFalse(TEXT("C9 a path nested past the depth checked"), PlaytestRefusal::RefusedJson(Deep).IsEmpty());
	TestFalse(TEXT("C9 a value nested past the depth checked"), PlaytestRefusal::RefusedJson(Flat).IsEmpty());
	TestTrue(TEXT("C9 a shallow value stays"), PlaytestRefusal::RefusedJson(MakeShared<FJsonValueArray>(
		TArray<TSharedPtr<FJsonValue>>{MakeShared<FJsonValueNumber>(1)})).IsEmpty());
	if (UFunction* Print = System->FindFunctionByName(TEXT("PrintString")))
	{
		FStructOnScope Scope(Print);
		if (FStrProperty* Text = CastField<FStrProperty>(Print->FindPropertyByName(TEXT("InString"))))
		{
			void* Value = Text->ContainerPtrToValuePtr<void>(Scope.GetStructMemory());
			Text->SetPropertyValue(Value, TEXT(".cluewren/runs/x/evil.umap"));
			TestFalse(TEXT("C9 a string into .cluewren"), RefusedValue(Text, Value).IsEmpty());
			Text->SetPropertyValue(Value, TEXT("hello/world"));
			TestTrue(TEXT("C9 another string stays"), RefusedValue(Text, Value).IsEmpty());
		}
	}

	for (const FString& Prefix : PlaytestRefusal::RefusedPackages())
	{
		for (TObjectIterator<UClass> It; It; ++It)
		{
			if (It->HasAnyClassFlags(CLASS_Native) && It->GetPackage()->GetName().StartsWith(Prefix))
			{
				Refuses(FString::Printf(TEXT("a class under %s (%s)"), *Prefix, *It->GetName()), *It, nullptr, EOp::Argument);
				break;
			}
		}
	}

	Allows(TEXT("GameplayStatics.GetPlayerCharacter"), StaticsDefaults, TEXT("GetPlayerCharacter"), EOp::Call);
	Allows(TEXT("KismetSystemLibrary.PrintString"), SystemDefaults, TEXT("PrintString"), EOp::Call);
	Allows(TEXT("PlayerController.SetPause"), Controller->GetDefaultObject(), TEXT("SetPause"), EOp::Call);
	Allows(TEXT("the Actor class as an argument"), Class(TEXT("/Script/Engine.Actor")), nullptr, EOp::Argument);
	Allows(TEXT("an instance's property write"), NewObject<UObject>(GetTransientPackage(), Class(TEXT("/Script/Engine.StaticMesh"))),
		TEXT("LightMapResolution"), EOp::Write);
	return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FCluewrenRcDataFolderTest, "Cluewren.Rc.DataFolder",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FCluewrenRcDataFolderTest::RunTest(const FString& Parameters)
{
	using PlaytestRefusal::DataPath;
	for (const TCHAR* Path : {TEXT(".cluewren/x"), TEXT("a/.CLUEWREN/x"), TEXT("C:\\other\\.cluewren\\x"),
		TEXT("\\\\server\\share\\.cluewren\\x"), TEXT(".cluewren."), TEXT(".cluewren "), TEXT(".cluewren::$INDEX_ALLOCATION"),
		TEXT(".cluewren:x"), TEXT("file:///C:/p/%2Ecluewren/x"), TEXT("Q:/nowhere/.cluewren./memory/lib.cs"), TEXT("C:.cluewren\\x"),
		TEXT("c:.CLUEWREN"), TEXT(".cluewren"), TEXT("%2Ecluewren%2Fmemory%2Flib.cs"), TEXT("CLUEWR~1"), TEXT("C:/p/cl12ab~1/x"),
		TEXT("CLUEW~12/x"), TEXT("CLUEWR~1./x"), TEXT("cluewr~3:x")})
	{
		TestTrue(FString(TEXT("C9 data folder: ")) + Path, DataPath(Path));
	}
	for (const TCHAR* Path : {TEXT(".cluewrens/x"), TEXT("x.cluewren/y"), TEXT("my.cluewren"), TEXT("a/cluewren/b"),
		TEXT("C:/PROGRA~1/Game/Content/x.uasset"), TEXT("Assets/NUL"), TEXT("file:///C:/p/%2Ecluewrens/x"), TEXT("C:.cluewrens\\x"),
		TEXT("PROGRA~1"), TEXT("CLUEWRE~1/x"), TEXT("CLUEWREN~1/x"), TEXT("CLUEWR~1.txt"), TEXT("CLUEWR~/x"), TEXT("CLUEWR~1a")})
	{
		TestFalse(FString(TEXT("C9 not the data folder: ")) + Path, DataPath(Path));
	}
	TestFalse(TEXT("C9 the folder alone as an rc value"), PlaytestRefusal::RefusedJson(MakeShared<FJsonValueString>(TEXT(".cluewren"))).IsEmpty());
	TestFalse(TEXT("C9 the folder percent-encoded as an rc value"),
		PlaytestRefusal::RefusedJson(MakeShared<FJsonValueString>(TEXT("%2Ecluewren%2Fmemory%2Flib.cs"))).IsEmpty());
	TestTrue(TEXT("C9 another word as an rc value"), PlaytestRefusal::RefusedJson(MakeShared<FJsonValueString>(TEXT("cluewren"))).IsEmpty());

	IFileManager& Files = IFileManager::Get();
	FString Root = FPaths::ConvertRelativePathToFull(FPaths::ProjectDir());
	FPaths::NormalizeDirectoryName(Root);
	const FString Data = Root / TEXT(".cluewren");
	const bool bMade = !Files.DirectoryExists(*Data);
	Files.MakeDirectory(*Data, true);
	FString Folder = Data;
	FPaths::MakePlatformFilename(Folder);
	TCHAR Short[4096];
	const DWORD Length = GetShortPathNameW(*Folder, Short, (DWORD)UE_ARRAY_COUNT(Short));
	const FString Name = Length > 0 && Length < UE_ARRAY_COUNT(Short) ? FPaths::GetCleanFilename(FString((int32)Length, Short)) : FString();
	if (!Name.IsEmpty() && !Name.Equals(TEXT(".cluewren"), ESearchCase::IgnoreCase))
	{
		AddInfo(FString::Printf(TEXT("the folder's short name here: %s"), *Name));
		TestTrue(TEXT("C9 the folder's short name"), DataPath(Name / TEXT("memory/lib.cs")));
		TestTrue(TEXT("C9 the folder's short name, under any root"), DataPath(FString(TEXT("D:/elsewhere/")) + Name.ToLower() + TEXT("/x")));
	}
	else
	{
		AddInfo(FString::Printf(TEXT("%s has no 8.3 short name: that case is not tried"), *Data));
	}
	if (bMade)
	{
		Files.DeleteDirectory(*Data);
	}
	return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FCluewrenRcSessionMapTest, "Cluewren.Rc.SessionMap",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FCluewrenRcSessionMapTest::RunTest(const FString& Parameters)
{
	for (const TCHAR* Map : {TEXT("C:/Maps/Arena"), TEXT("C:Arena"), TEXT("C:\\Maps\\Arena.umap"), TEXT("\\\\server\\share\\Arena"),
		TEXT("//server/share/Arena"), TEXT("\\\\.\\COM1"), TEXT("\\\\?\\C:\\Maps\\Arena"), TEXT("/C:/Maps/Arena"), TEXT("file:///C:/Maps/Arena"),
		TEXT("127.0.0.1:7777"), TEXT("Arena?listen"), TEXT("/Game/Maps/Arena?listen"), TEXT("Arena#Portal"), TEXT("example.com"),
		TEXT("10.0.0.1"), TEXT("[arena-server]"), TEXT("[::1]"), TEXT("\\Windows\\outside.umap"), TEXT("Arena;x"), TEXT("Arena Lobby"),
		TEXT("Arena%20Lobby"), TEXT("Arena.Arena"), TEXT("Arena|x"), TEXT("/Game/Maps/"), TEXT("/Game//Maps/Arena"), TEXT("/Game/.Maps"),
		TEXT("/Game/Maps/Arena.Arena.Arena"), TEXT("/Game/Ma.ps/Arena"), TEXT("Arena.demo"), TEXT("Arena.sav"), TEXT("Arena'"),
		TEXT("Arena\"x"), TEXT("Arena\tx"), TEXT("/"), TEXT("")})
	{
		TestFalse(FString(TEXT("not a level's name: ")) + Map, PlaytestLevels::Shaped(Map));
		FString Package, Error;
		TestFalse(FString(TEXT("no level: ")) + Map, PlaytestLevels::Resolve(Map, Package, Error));
		TestTrue(FString(TEXT("no level, in so many words: ")) + Map, Error.StartsWith(TEXT("No level named ")));
		TestTrue(FString(TEXT("nothing to open: ")) + Map, Package.IsEmpty());
	}
	for (const TCHAR* Map : {TEXT("/Game/Maps/Arena"), TEXT("/Game/Maps/Arena.Arena"), TEXT("/Game/Maps/Arena.umap"), TEXT("Arena"),
		TEXT("Arena.umap"), TEXT("ARENA.UMAP"), TEXT(" Lobby "), TEXT("Arena_2-b"), TEXT("Maps/Arena")})
	{
		TestTrue(FString(TEXT("a level's name: ")) + Map, PlaytestLevels::Shaped(Map));
	}
	// Shaped, but no level of the project's: matched against the project's levels, never looked for elsewhere.
	for (const TCHAR* Map : {TEXT("/Windows/outside"), TEXT("/Engine/Maps/Entry"), TEXT("localhost"), TEXT("Maps/Arena")})
	{
		FString Package, Error;
		TestFalse(FString(TEXT("no such level: ")) + Map, PlaytestLevels::Resolve(Map, Package, Error));
		TestTrue(FString(TEXT("nothing to open: ")) + Map, Package.IsEmpty());
	}
	const TArray<FString> Levels = PlaytestLevels::List();
	if (Levels.Num() > 0)
	{
		FString Package, Error;
		TestTrue(TEXT("a level of the project's"), PlaytestLevels::Resolve(Levels[0].ToLower(), Package, Error));
		TestEqual(TEXT("what is opened is the project's own name for it"), Package, Levels[0]);
	}
	else
	{
		AddInfo(TEXT("the project has no level: resolving one is not tried"));
	}
	return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FCluewrenRcCensusTest, "Cluewren.Rc.Census",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FCluewrenRcCensusTest::RunTest(const FString& Parameters)
{
	using PlaytestRefusal::Refused;
	static const TMap<FString, FString> Reviewed = {
		{TEXT("KismetSystemLibrary.CanLaunchURL"), TEXT("answers whether, launches nothing")},
		{TEXT("KismetSystemLibrary.ConvertToAbsolutePath"), TEXT("a string")},
		{TEXT("KismetSystemLibrary.ConvertToRelativePath"), TEXT("a string")},
		{TEXT("KismetSystemLibrary.NormalizeFilename"), TEXT("a string")},
		{TEXT("KismetSystemLibrary.ParseCommandLine"), TEXT("parses the string it is given")},
		{TEXT("KismetSystemLibrary.BreakSoftClassPath"), TEXT("a struct")},
		{TEXT("KismetSystemLibrary.BreakSoftObjectPath"), TEXT("a struct")},
		{TEXT("KismetSystemLibrary.BreakTopLevelAssetPath"), TEXT("a struct")},
		{TEXT("KismetSystemLibrary.MakeSoftClassPath"), TEXT("a struct")},
		{TEXT("KismetSystemLibrary.MakeSoftObjectPath"), TEXT("a struct")},
		{TEXT("MediaPlayer.CanPlayUrl"), TEXT("answers whether, opens nothing")},
		{TEXT("MediaPlateComponent.OnMediaOpened"), TEXT("an event the player raises")},
		{TEXT("Actor.SetFolderPath"), TEXT("the outliner's folder")},
		{TEXT("AssetImportData.ScriptedAddFilename"), TEXT("import metadata")},
		{TEXT("KismetStringLibrary.Conv_InputDeviceIdToString"), TEXT("an input device's id, not the host's")},
		{TEXT("InputDeviceSubsystem.GetMostRecentlyUsedInputDeviceId"), TEXT("an input device's id, not the host's")},
		{TEXT("PCGBlueprintHelpers.ExtractStructureAsAttributeSet"), TEXT("a property path")},
	};
	static const TMap<FString, FString> ReviewedClasses = {
		{TEXT("AssetRegistry"), TEXT("package paths in the asset registry")},
		{TEXT("InputDeviceLibrary"), TEXT("input device ids")},
		{TEXT("PluginBlueprintLibrary"), TEXT("a plugin's folders, read")},
		{TEXT("ModularRig"), TEXT("rig element paths")},
		{TEXT("ModularRigController"), TEXT("rig element paths")},
		{TEXT("RigHierarchyController"), TEXT("rig element paths")},
		{TEXT("MovieSceneSequenceExtensions"), TEXT("sequence folder and binding paths")},
		{TEXT("MovieScenePropertyTrackExtensions"), TEXT("property paths")},
		{TEXT("MovieSceneFolderExtensions"), TEXT("sequence folders")},
	};
	static const TArray<FString> NamedLike = {TEXT("CommandLine"), TEXT("Clipboard"), TEXT("EnvironmentVariable"), TEXT("DeviceId"),
		TEXT("ConsoleCommand"), TEXT("Python"), TEXT("LaunchURL")};
	TArray<FString> Unreviewed;
	int32 Seen = 0;
	for (TObjectIterator<UFunction> It; It; ++It)
	{
		UFunction* Function = *It;
		UClass* Owner = Function->GetOuterUClass();
		if (!Owner || Function->HasAnyFunctionFlags(FUNC_Delegate) || !Function->HasAnyFunctionFlags(FUNC_Native)
			|| !Refused(Owner, NAME_None, EOp::Argument).IsEmpty())
		{
			continue;
		}
		const FString Name = Function->GetName();
		bool bHit = NamedLike.ContainsByPredicate([&Name](const FString& Like) { return Name.Contains(Like); });
		for (TFieldIterator<FProperty> P(Function); P && P->HasAnyPropertyFlags(CPF_Parm) && !bHit; ++P)
		{
			bHit = StringLike(*P) && !P->HasAnyPropertyFlags(CPF_ReturnParm) && PathLike(P->GetName());
		}
		if (!bHit)
		{
			continue;
		}
		++Seen;
		const FString Key = Owner->GetName() + TEXT(".") + Name;
		if (Refused(Owner, Function->GetFName(), EOp::Call).IsEmpty() && !Reviewed.Contains(Key)
			&& !ReviewedClasses.Contains(Owner->GetName()))
		{
			Unreviewed.Add(Key + TEXT(" (") + Owner->GetPackage()->GetName() + TEXT(")"));
		}
	}
	// Writable string properties too: rc writes any property, and one can hold what a refused function would take.
	static const TMap<FString, FString> ReviewedProperties = {
		{TEXT("Actor.FolderPath"), TEXT("the outliner's folder")},
		{TEXT("ActorFolder.FolderLabel"), TEXT("the outliner's folder")},
		{TEXT("LevelStreaming.FolderPath"), TEXT("the outliner's folder")},
		{TEXT("MovieSceneFolder.FolderName"), TEXT("a sequence folder")},
		{TEXT("MovieScenePropertyTrack.PropertyPath"), TEXT("a property path")},
		{TEXT("NiagaraDataInterfacePropertyInterface.InterfacePath"), TEXT("a property path")},
		{TEXT("PCGGenericUserParameterGetSettings.PropertyPath"), TEXT("a property path")},
		{TEXT("PCGPointFromMeshSettings.MeshPathAttributeName"), TEXT("an attribute name")},
		{TEXT("PCGGetAssetListSettings.Directory"), TEXT("a package path in the project")},
		{TEXT("PCGDataTableRowToParamDataSettings.PathOverride"), TEXT("a package path in the project")},
		{TEXT("WorldPartitionHLOD.HLODResourcesPackagePath"), TEXT("a package path in the project")},
		{TEXT("PlayerState.SavedNetworkAddress"), TEXT("the game's own state")},
		{TEXT("PlatformInterfaceWebResponse.OriginalURL"), TEXT("a response's data")},
		{TEXT("VerseClass.MangledPackageVersePath"), TEXT("Verse metadata")},
		{TEXT("VerseClass.PackageRelativeVersePath"), TEXT("Verse metadata")},
	};
	// Import metadata (reimporting is the editor's) and the Niagara baker's asset path formats.
	static const TArray<FString> ReviewedNames = {TEXT("SourceFilePath"), TEXT("SourceFileTimestamp"), TEXT("ImportPath"),
		TEXT("SourceFilename")};
	static const TArray<FString> ReviewedPrefixes = {TEXT("Datasmith"), TEXT("NiagaraBakerOutput")};
	int32 Properties = 0;
	for (TObjectIterator<UClass> It; It; ++It)
	{
		UClass* Owner = *It;
		if (!Owner->HasAnyClassFlags(CLASS_Native) || !Refused(Owner, NAME_None, EOp::Argument).IsEmpty())
		{
			continue;
		}
		for (TFieldIterator<FProperty> P(Owner, EFieldIteratorFlags::ExcludeSuper); P; ++P)
		{
			const FStructProperty* Struct = CastField<FStructProperty>(*P);
			const bool bPathStruct = Struct && (Struct->Struct->GetFName() == TEXT("FilePath") || Struct->Struct->GetFName() == TEXT("DirectoryPath"));
			if ((!StringLike(*P) && !bPathStruct) || !PathLike(P->GetName()))
			{
				continue;
			}
			++Properties;
			const FString Key = Owner->GetName() + TEXT(".") + P->GetName();
			// A config value lives on the class's defaults, which rc doesn't write (C8).
			const bool bConfig = Owner->HasAnyClassFlags(CLASS_Config) && P->HasAnyPropertyFlags(CPF_Config | CPF_GlobalConfig);
			const FString OwnerName = Owner->GetName();
			if (Refused(Owner, P->GetFName(), EOp::Write).IsEmpty() && !bConfig && !ReviewedProperties.Contains(Key)
				&& !ReviewedClasses.Contains(OwnerName) && !ReviewedNames.Contains(P->GetName())
				&& !ReviewedPrefixes.ContainsByPredicate([&OwnerName](const FString& Prefix) { return OwnerName.StartsWith(Prefix); }))
			{
				Unreviewed.Add(TEXT("property ") + Key + TEXT(" (") + Owner->GetPackage()->GetName() + TEXT(")"));
			}
		}
	}
	Unreviewed.Sort();
	AddInfo(FString::Printf(TEXT("%d path-, URL- or command-taking native functions and %d such properties outside refused classes"),
		Seen, Properties));
	for (const FString& Key : Unreviewed)
	{
		AddError(TEXT("neither refused nor reviewed: ") + Key);
	}
	return true;
}

#endif
