#include "PlaytestRefusal.h"

#include "Dom/JsonObject.h"
#include "Misc/EngineVersionComparison.h"
#include "Misc/PackageName.h"
#include "Misc/Paths.h"
#include "UObject/Class.h"
#include "UObject/ObjectKey.h"
#include "UObject/Package.h"
#include "UObject/SoftObjectPtr.h"
#include "UObject/TextProperty.h"
#include "UObject/UnrealType.h"
#if !UE_VERSION_OLDER_THAN(5, 5, 0)
#include "UObject/StrProperty.h"
#endif

#if UE_VERSION_OLDER_THAN(5, 4, 0)
#define PLAYTEST_REFUSAL_INDEX(It) (*(It))
#else
#define PLAYTEST_REFUSAL_INDEX(It) (It)
#endif

namespace PlaytestRefusal
{
namespace
{
	const FName PlaySettings(TEXT("LevelEditorPlaySettings"));
	const FName SystemLibrary(TEXT("KismetSystemLibrary"));
	constexpr int32 MaxDepth = 16;

	bool EditorPackage(const UPackage* Package)
	{
		return Package && Package->HasAnyPackageFlags(PKG_EditorOnly | PKG_Developer | PKG_UncookedOnly);
	}

	// The family KismetSystemLibrary writes properties by name with: rc's own property op applies these rules instead.
	bool WritesByName(FName Member)
	{
		const FString Name = Member.ToString();
		return (Name.StartsWith(TEXT("Set")) && Name.EndsWith(TEXT("PropertyByName"))) || Name == TEXT("SetCollisionProfileNameProperty");
	}

	bool ShortName(const FString& Part)
	{
		int32 Tilde = INDEX_NONE;
		return Part.FindChar(TEXT('~'), Tilde) && Tilde >= 1 && Tilde <= 6 && Tilde + 1 < Part.Len() && FChar::IsDigit(Part[Tilde + 1]);
	}

	FString RefusedText(const FString& Text)
	{
		if ((Text.Contains(TEXT("/")) || Text.Contains(TEXT("\\"))) && DataPath(Text))
		{
			return FString::Printf(TEXT("%s is refused: nothing under Saved/Cluewren is opened for rc"), *Text);
		}
		return FString();
	}

	FString Walk(const FProperty* Property, const void* ValuePtr, int32 Depth);

	FString WalkElements(const FProperty* Property, const void* ValuePtr, int32 Depth)
	{
		for (int32 i = 0; i < Property->ArrayDim; ++i)
		{
			const FString Why = Walk(Property, static_cast<const uint8*>(ValuePtr) + i * Property->GetElementSize(), Depth);
			if (!Why.IsEmpty())
			{
				return Why;
			}
		}
		return FString();
	}

	const TCHAR* TooDeep = TEXT("A value nested more than 16 deep is refused: rc can't check it");

	FString Walk(const FProperty* Property, const void* ValuePtr, int32 Depth)
	{
		if (Depth > MaxDepth)
		{
			return TooDeep;
		}
		if (!Property || !ValuePtr)
		{
			return FString();
		}
		if (const FSoftObjectProperty* Soft = CastField<FSoftObjectProperty>(Property))
		{
			const FSoftObjectPtr& Ptr = *static_cast<const FSoftObjectPtr*>(ValuePtr);
			const FString Why = RefusedText(Ptr.ToSoftObjectPath().ToString());
			return Why.IsEmpty() ? Refused(Ptr.Get(), NAME_None, EOp::Argument) : Why;
		}
		if (const FObjectPropertyBase* Object = CastField<FObjectPropertyBase>(Property))
		{
			return Refused(Object->GetObjectPropertyValue(ValuePtr), NAME_None, EOp::Argument);
		}
		if (CastField<FInterfaceProperty>(Property))
		{
			return Refused(static_cast<const FScriptInterface*>(ValuePtr)->GetObject(), NAME_None, EOp::Argument);
		}
		if (const FDelegateProperty* Delegate = CastField<FDelegateProperty>(Property))
		{
			if (Delegate->GetPropertyValuePtr(ValuePtr)->IsBound())
			{
				return FString::Printf(TEXT("%s is refused: rc doesn't bind a delegate, which calls a function by name"), *Property->GetName());
			}
			return FString();
		}
		if (const FMulticastDelegateProperty* Multicast = CastField<FMulticastDelegateProperty>(Property))
		{
			const FMulticastScriptDelegate* Bound = Multicast->GetMulticastDelegate(ValuePtr);
			if (Bound && Bound->IsBound())
			{
				return FString::Printf(TEXT("%s is refused: rc doesn't bind a delegate, which calls a function by name"), *Property->GetName());
			}
			return FString();
		}
		if (const FStrProperty* Str = CastField<FStrProperty>(Property))
		{
			return RefusedText(Str->GetPropertyValue(ValuePtr));
		}
		if (const FNameProperty* Name = CastField<FNameProperty>(Property))
		{
			return RefusedText(Name->GetPropertyValue(ValuePtr).ToString());
		}
		if (const FTextProperty* Text = CastField<FTextProperty>(Property))
		{
			return RefusedText(Text->GetPropertyValue(ValuePtr).ToString());
		}
		if (const FStructProperty* Struct = CastField<FStructProperty>(Property))
		{
			for (TFieldIterator<FProperty> It(Struct->Struct); It; ++It)
			{
				const FString Why = WalkElements(*It, It->ContainerPtrToValuePtr<void>(ValuePtr), Depth + 1);
				if (!Why.IsEmpty())
				{
					return Why;
				}
			}
			return FString();
		}
		if (const FArrayProperty* Array = CastField<FArrayProperty>(Property))
		{
			FScriptArrayHelper Helper(Array, ValuePtr);
			for (int32 i = 0; i < Helper.Num(); ++i)
			{
				const FString Why = Walk(Array->Inner, Helper.GetRawPtr(i), Depth + 1);
				if (!Why.IsEmpty())
				{
					return Why;
				}
			}
			return FString();
		}
		if (const FSetProperty* Set = CastField<FSetProperty>(Property))
		{
			FScriptSetHelper Helper(Set, ValuePtr);
			for (FScriptSetHelper::FIterator It(Helper); It; ++It)
			{
				const FString Why = Walk(Set->ElementProp, Helper.GetElementPtr(PLAYTEST_REFUSAL_INDEX(It)), Depth + 1);
				if (!Why.IsEmpty())
				{
					return Why;
				}
			}
			return FString();
		}
		if (const FMapProperty* Map = CastField<FMapProperty>(Property))
		{
			FScriptMapHelper Helper(Map, ValuePtr);
			for (FScriptMapHelper::FIterator It(Helper); It; ++It)
			{
				FString Why = Walk(Map->KeyProp, Helper.GetKeyPtr(PLAYTEST_REFUSAL_INDEX(It)), Depth + 1);
				if (Why.IsEmpty())
				{
					Why = Walk(Map->ValueProp, Helper.GetValuePtr(PLAYTEST_REFUSAL_INDEX(It)), Depth + 1);
				}
				if (!Why.IsEmpty())
				{
					return Why;
				}
			}
		}
		return FString();
	}
}

const TSet<FName>& RefusedClasses()
{
	static const TSet<FName> Names = {
		TEXT("PythonScriptLibrary"),
		TEXT("BlueprintFileUtilsBPLibrary"),
		TEXT("BlueprintPathsLibrary"),
		TEXT("AsyncActionHandleSaveGame"),
		TEXT("AssetTagsSubsystem"),
		TEXT("AutomationUtilsBlueprintLibrary"),
		TEXT("GLTFExporter"),
		TEXT("ImageWriteBlueprintLibrary"),
		TEXT("TraceUtilLibrary"),
		TEXT("TakeRecorderSubsystem"),
		TEXT("TakeMetaData"),
		TEXT("TmvMediaPlayerFrameProducer"),
		TEXT("AsyncTaskDownloadImage"),
		TEXT("TwitterIntegrationBase"),
		TEXT("MobilePatchingLibrary"),
		TEXT("ConsoleVariablesAsset"),
		TEXT("BuildPatchManifest"),
	};
	return Names;
}

const TMap<FName, TSet<FName>>& RefusedProperties()
{
	static const TMap<FName, TSet<FName>> Properties = {
		{TEXT("FileMediaSource"), {TEXT("FilePath")}},
		{TEXT("ImgMediaSource"), {TEXT("SequencePath")}},
		{TEXT("StreamMediaSource"), {TEXT("StreamUrl")}},
		{TEXT("Engine"), {TEXT("GameScreenshotSaveDirectory")}},
		{TEXT("AssetExportTask"), {TEXT("Filename")}},
		{TEXT("DNAAsset"), {TEXT("DnaFileName")}},
		{TEXT("VolumeCache"), {TEXT("FilePath")}},
		{TEXT("PCGExportSelectedAttributesSettings"), {TEXT("FileName"), TEXT("Path")}},
		{TEXT("PCGSaveDataViewSettings"), {TEXT("FilePath")}},
		{TEXT("LandscapeProxy"), {TEXT("ReimportHeightmapFilePath")}},
		{TEXT("BrushBuilder"), {TEXT("BitmapFilename")}},
		{TEXT("GameplayTagsList"), {TEXT("ConfigFileName")}},
		{TEXT("RestrictedGameplayTagsList"), {TEXT("ConfigFileName")}},
		{TEXT("MassEntitySettings"), {TEXT("DumpDependencyGraphFileName")}},
		{TEXT("AndroidRuntimeSettings"), {TEXT("MaliGraphicsDebuggerPath")}},
	};
	return Properties;
}

const TArray<FString>& RefusedPackages()
{
	static const TArray<FString> Prefixes = {TEXT("/Script/Interchange"), TEXT("/Script/HttpBlueprint"), TEXT("/Script/WebBrowser"),
		TEXT("/Script/OnlineSubsystem"), TEXT("/Script/AudioCapture"), TEXT("/Script/MovieSceneCapture"), TEXT("/Script/TakeRecorder"),
		TEXT("/Script/TakeTrackRecorders")};
	return Prefixes;
}

const TMap<FName, TSet<FName>>& RefusedFunctions()
{
	static const TMap<FName, TSet<FName>> Functions = {
		{TEXT("KismetSystemLibrary"), {TEXT("ExecuteConsoleCommand"), TEXT("LaunchURL"), TEXT("LaunchExternalUrl"), TEXT("QuitEditor"),
			TEXT("GetCommandLine"), TEXT("GetDeviceId"), TEXT("GetUniqueDeviceId"), TEXT("K2_SetTimer"), TEXT("K2_SetTimerForNextTick")}},
		{TEXT("GameplayStatics"), {TEXT("OpenLevel"), TEXT("OpenLevelBySoftObjectPtr"), TEXT("SaveGameToSlot"), TEXT("LoadGameFromSlot"),
			TEXT("DeleteGameInSlot"), TEXT("DoesSaveGameExist")}},
		{TEXT("PlayerController"), {TEXT("ClientTravel"), TEXT("ClientTravelInternal"), TEXT("LocalTravel"), TEXT("SwitchLevel"),
			TEXT("SendToConsole"), TEXT("TestServerLevelVisibilityChange")}},
		{TEXT("PlayerInput"), {TEXT("SetBind")}},
		{TEXT("CheatManager"), {TEXT("CheatScript")}},
		{TEXT("KismetRenderingLibrary"), {TEXT("ExportRenderTarget"), TEXT("ExportTexture2D"), TEXT("ImportFileAsTexture2D")}},
		{TEXT("KismetStringTableLibrary"), {TEXT("ExportTableToCSVFile"), TEXT("ImportTableFromCSVFile")}},
		{TEXT("DataTableFunctionLibrary"), {TEXT("ExportDataTableToCSVFile"), TEXT("ExportDataTableToJSONFile"),
			TEXT("FillDataTableFromCSVFile"), TEXT("FillDataTableFromJSONFile")}},
		{TEXT("SoundSubmix"), {TEXT("StopRecordingOutput")}},
		{TEXT("AudioMixerBlueprintLibrary"), {TEXT("StopRecordingOutput")}},
		{TEXT("MediaPlayer"), {TEXT("OpenFile"), TEXT("OpenUrl")}},
		{TEXT("MediaPlaylist"), {TEXT("AddFile"), TEXT("AddUrl")}},
		{TEXT("MediaPlateComponent"), {TEXT("SelectExternalMedia")}},
		{TEXT("FileMediaSource"), {TEXT("SetFilePath")}},
		{TEXT("ImgMediaSource"), {TEXT("SetSequencePath"), TEXT("SetTokenizedSequencePath")}},
		{TEXT("GroomBlueprintLibrary"), {TEXT("CreateNewGroomBindingAssetWithPath"), TEXT("CreateNewGeometryCacheGroomBindingAssetWithPath")}},
		{TEXT("ControlRigReplay"), {TEXT("CreateNewAsset")}},
		{TEXT("Variant"), {TEXT("SetThumbnailFromFile")}},
		{TEXT("VariantSet"), {TEXT("SetThumbnailFromFile")}},
	};
	return Functions;
}

namespace
{
	FString DecideClass(const UClass* Class)
	{
		for (const UClass* C = Class; C; C = C->GetSuperClass())
		{
			if (C->GetFName() == PlaySettings)
			{
				return TEXT("LevelEditorPlaySettings is refused: the editor's Play settings (net mode, clients...) are the user's "
					"and aren't changed by a run, the game is played as it is set up. If a setting stands in the way, say so in "
					"the report");
			}
		}
		// A Blueprint class has no module of its own: its nearest native ancestor's decides.
		const UClass* Native = Class;
		while (Native && !Native->HasAnyClassFlags(CLASS_Native))
		{
			Native = Native->GetSuperClass();
		}
		const FString Module = Native ? Native->GetPackage()->GetName() : FString();
		if (RefusedPackages().ContainsByPredicate([&Module](const FString& Prefix) { return Module.StartsWith(Prefix); }))
		{
			return FString::Printf(TEXT("%s is refused: rc doesn't reach files, the network, processes or code through it"),
				*Class->GetName());
		}
		if (Native && EditorPackage(Native->GetPackage()))
		{
			return FString::Printf(TEXT("%s is refused: the editor's own modules (%s) are out of rc's reach"), *Class->GetName(),
				*Native->GetPackage()->GetName());
		}
		for (const UClass* C = Class; C; C = C->GetSuperClass())
		{
			if (RefusedClasses().Contains(C->GetFName()))
			{
				return FString::Printf(TEXT("%s is refused: rc doesn't reach files, the network, processes or code through it"),
					*Class->GetName());
			}
		}
		return FString();
	}

	FString DecideCall(const UClass* Class, FName Member)
	{
		for (const UClass* C = Class; C; C = C->GetSuperClass())
		{
			const TSet<FName>* Functions = RefusedFunctions().Find(C->GetFName());
			if ((Functions && Functions->Contains(Member)) || (C->GetFName() == SystemLibrary && WritesByName(Member)))
			{
				return FString::Printf(TEXT("%s.%s is refused: rc doesn't reach processes, URLs, files, the network, the host, "
					"console commands or calls by name through it"), *C->GetName(), *Member.ToString());
			}
		}
		return FString();
	}

	FString DecideWrite(const UClass* Class, FName Member)
	{
		for (const UClass* C = Class; C; C = C->GetSuperClass())
		{
			const TSet<FName>* Properties = RefusedProperties().Find(C->GetFName());
			if (Properties && Properties->Contains(Member))
			{
				return FString::Printf(TEXT("Writing %s.%s is refused: it names a file or URL the engine opens"), *C->GetName(),
					*Member.ToString());
			}
		}
		return FString();
	}
}

FString Refused(const UObject* Object, FName Member, EOp Op)
{
	if (!Object)
	{
		return FString();
	}
	const UClass* Class = Cast<UClass>(Object);
	if (!Class)
	{
		Class = Object->GetClass();
	}
	if (EditorPackage(Cast<UPackage>(Object)))
	{
		return FString::Printf(TEXT("%s is refused: the editor's own modules are out of rc's reach"), *Object->GetName());
	}
	// Decided once per class and per function, rc reads being on the harness's tick path. FObjectKey, not a pointer: a
	// Blueprint class recompiled at the same address is another key.
	static TMap<FObjectKey, FString> Classes;
	static TMap<TPair<FObjectKey, FName>, FString> Calls;
	static TMap<TPair<FObjectKey, FName>, FString> Writes;
	const FObjectKey Key(Class);
	const FString* ClassWhy = Classes.Find(Key);
	if (!ClassWhy)
	{
		ClassWhy = &Classes.Add(Key, DecideClass(Class));
	}
	if (!ClassWhy->IsEmpty())
	{
		return *ClassWhy;
	}
	if (Op == EOp::Call && !Member.IsNone())
	{
		const TPair<FObjectKey, FName> CallKey(Key, Member);
		const FString* CallWhy = Calls.Find(CallKey);
		if (!CallWhy)
		{
			CallWhy = &Calls.Add(CallKey, DecideCall(Class, Member));
		}
		if (!CallWhy->IsEmpty())
		{
			return *CallWhy;
		}
	}
	if (Op == EOp::Write && !Member.IsNone())
	{
		const TPair<FObjectKey, FName> WriteKey(Key, Member);
		const FString* WriteWhy = Writes.Find(WriteKey);
		if (!WriteWhy)
		{
			WriteWhy = &Writes.Add(WriteKey, DecideWrite(Class, Member));
		}
		if (!WriteWhy->IsEmpty())
		{
			return *WriteWhy;
		}
	}
	// The project's and the editor's settings live on their classes' defaults: Godot's ProjectSettings and Unity's
	// PlayerSettings are as far out of reach.
	if (Op == EOp::Write && Object->HasAnyFlags(RF_ClassDefaultObject) && Class->HasAnyClassFlags(CLASS_Config))
	{
		return FString::Printf(TEXT("Writing %s on %s is refused: rc doesn't change the project's settings"), *Member.ToString(),
			*Class->GetName());
	}
	return FString();
}

FString RefusedValue(const FProperty* Property, const void* ValuePtr)
{
	return Property ? WalkElements(Property, ValuePtr, 0) : FString();
}

namespace
{
	bool DecidePath(const FString& Path)
	{
		FString Clean = Path.TrimStartAndEnd().Replace(TEXT("\\"), TEXT("/"));
		int32 Quote = INDEX_NONE;
		if (Clean.FindChar(TEXT('\''), Quote) && Clean.EndsWith(TEXT("'")))
		{
			Clean = Clean.Mid(Quote + 1, Clean.Len() - Quote - 2);
		}
		if (Clean.IsEmpty())
		{
			return false;
		}
		FString File = Clean;
		if (Clean.StartsWith(TEXT("/")) && !Clean.StartsWith(TEXT("//")))
		{
			// A package path walks out of its mount point only with .. or . parts, which no package name has.
			if (Clean.Contains(TEXT("/../")) || Clean.Contains(TEXT("/./")) || Clean.EndsWith(TEXT("/..")))
			{
				return true;
			}
			FString Filename;
			if (FPackageName::TryConvertLongPackageNameToFilename(FPackageName::ObjectPathToPackageName(Clean), Filename))
			{
				File = Filename;
			}
		}
		const FString Root = FPaths::ConvertRelativePathToFull(FPaths::ProjectDir());
		const FString Full = FPaths::ConvertRelativePathToFull(FPaths::ProjectDir(), File);
		const FString Data = FPaths::ConvertRelativePathToFull(FPaths::ProjectSavedDir() / TEXT("Cluewren"));
		if (Full.Equals(Data, ESearchCase::IgnoreCase) || Full.StartsWith(Data + TEXT("/"), ESearchCase::IgnoreCase))
		{
			return true;
		}
		if (!Full.StartsWith(Root, ESearchCase::IgnoreCase))
		{
			return false;
		}
		// A Windows 8.3 short name (SAVED~1) names the same folder under another spelling.
		TArray<FString> Parts;
		Full.Mid(Root.Len()).ParseIntoArray(Parts, TEXT("/"));
		for (const FString& Part : Parts)
		{
			if (ShortName(Part))
			{
				return true;
			}
		}
		return false;
	}
}

FString RefusedJson(const TSharedPtr<FJsonValue>& Json, int32 Depth)
{
	if (Depth > MaxDepth)
	{
		return TooDeep;
	}
	if (!Json.IsValid())
	{
		return FString();
	}
	FString Text;
	if (Json->TryGetString(Text))
	{
		return RefusedText(Text);
	}
	const TArray<TSharedPtr<FJsonValue>>* Items = nullptr;
	if (Json->TryGetArray(Items))
	{
		for (const TSharedPtr<FJsonValue>& Item : *Items)
		{
			const FString Why = RefusedJson(Item, Depth + 1);
			if (!Why.IsEmpty())
			{
				return Why;
			}
		}
		return FString();
	}
	const TSharedPtr<FJsonObject>* Object = nullptr;
	if (Json->TryGetObject(Object) && Object && Object->IsValid())
	{
		for (const TPair<FString, TSharedPtr<FJsonValue>>& Pair : (*Object)->Values)
		{
			const FString Why = RefusedJson(Pair.Value, Depth + 1);
			if (!Why.IsEmpty())
			{
				return Why;
			}
		}
	}
	return FString();
}

bool DataPath(const FString& Path)
{
	// Object paths repeat tick after tick; the cap keeps a harness that names fresh strings from growing it without end.
	static TMap<FString, bool> Known;
	if (const bool* Found = Known.Find(Path))
	{
		return *Found;
	}
	if (Known.Num() > 4096)
	{
		Known.Reset();
	}
	return Known.Add(Path, DecidePath(Path));
}
}
