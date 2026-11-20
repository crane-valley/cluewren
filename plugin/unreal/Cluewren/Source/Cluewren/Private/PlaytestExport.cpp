// Modified by Crane Valley: the export starting and staleness parts of upstream's PlaytestCodex.cpp; a run start
// triggers the export in place of the codex_manifest and codex_read ops (ADR 0003).
// Keeps .cluewren/export in the project fresh: the Blueprint export the CluewrenExport commandlet writes, run as a
// child process, never in this one: it loads every asset.

#include "PlaytestInternal.h"

#if WITH_EDITOR

#include "Async/Async.h"
#include "HAL/FileManager.h"
#include "HAL/PlatformProcess.h"
#include "HAL/PlatformTime.h"
#include "Interfaces/IPluginManager.h"
#include "Misc/AutomationTest.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"
#include "Misc/SecureHash.h"

namespace PlaytestExport
{
namespace
{
	constexpr double PollSeconds = 1.0;

	/** Absolute, forward slashes, no trailing slash. Worked out on the game thread. */
	struct FRoots
	{
		FString Project;  // the project's directory
		FString Kept;     // <Project>/.cluewren
		FString Export;   // <Project>/.cluewren/export
		TArray<FString> Content;
	};

	FString Clean(FString Path)
	{
		Path = FPaths::ConvertRelativePathToFull(Path);
		FPaths::NormalizeDirectoryName(Path);
		return Path;
	}

	FRoots GetRoots()
	{
		FRoots Roots;
		Roots.Project = Clean(FPaths::ProjectDir());
		Roots.Kept = Roots.Project / TEXT(".cluewren");
		Roots.Export = Roots.Kept / TEXT("export");
		Roots.Content.Add(Clean(FPaths::ProjectContentDir()));
		for (const TSharedRef<IPlugin>& Plugin : IPluginManager::Get().GetEnabledPluginsWithContent())
		{
			if (Plugin->GetLoadedFrom() == EPluginLoadedFrom::Project && Plugin->GetName() != TEXT("Cluewren"))
			{
				Roots.Content.Add(Clean(Plugin->GetContentDir()));
			}
		}
		return Roots;
	}

	void CollectAssets(const FString& Dir, TArray<FString>& Out)
	{
		TArray<FString> SubDirs;
		IFileManager::Get().IterateDirectoryStat(*Dir, [&](const TCHAR* Found, const FFileStatData& Stat)
		{
			const FString Name = FPaths::GetCleanFilename(Found);
			if (Stat.bIsDirectory)
			{
				SubDirs.Add(Dir / Name);
			}
			else
			{
				const FString Ext = FPaths::GetExtension(Name);
				if (Ext.Equals(TEXT("uasset"), ESearchCase::IgnoreCase) || Ext.Equals(TEXT("umap"), ESearchCase::IgnoreCase))
				{
					Out.Add(FString::Printf(TEXT("%s\t%lld\t%lld"), *(Dir / Name), Stat.ModificationTime.GetTicks(), Stat.FileSize));
				}
			}
			return true;
		});
		for (const FString& SubDir : SubDirs)
		{
			CollectAssets(SubDir, Out);
		}
	}

	FString AssetFingerprint(const TArray<FString>& Dirs)
	{
		TArray<FString> Entries = Dirs;
		for (const FString& Dir : Dirs)
		{
			CollectAssets(Dir, Entries);
		}
		Entries.Sort();
		const FString Text = FString::Join(Entries, TEXT("\n"));
		const FTCHARToUTF8 Inventory(*Text);
		return FSHA1::HashBuffer(Inventory.Get(), Inventory.Length()).ToString();
	}

	FString FingerprintPath(const FRoots& Roots)
	{
		return Roots.Export / TEXT(".asset-fingerprint");
	}

	FString SummaryPath(const FRoots& Roots)
	{
		return Roots.Export / TEXT("summary.txt");
	}

	bool IsCurrent(const FRoots& Roots, const FString& Fingerprint)
	{
		FString Exported;
		return FPaths::FileExists(SummaryPath(Roots)) && FPaths::FileExists(Roots.Export / TEXT("assets.txt"))
			&& FFileHelper::LoadFileToString(Exported, *FingerprintPath(Roots)) && Exported == Fingerprint;
	}

	/** The engine's command-line editor next to the running executable (or in the engine's binaries). */
	FString FindEditorCmd()
	{
		const FString Exe = FPlatformProcess::ExecutablePath();
		const FString Dir = FPaths::GetPath(Exe);
		const FString Ext = FPaths::GetExtension(Exe, true);
		const FString Base = FPaths::GetBaseFilename(Exe);
		TArray<FString> Candidates;
		if (Base.StartsWith(TEXT("UnrealEditor")))
		{
			// UnrealEditor.exe -> UnrealEditor-Cmd.exe; UnrealEditor-Win64-DebugGame.exe -> ...-DebugGame-Cmd.exe
			Candidates.Add(Dir / (Base.EndsWith(TEXT("-Cmd")) ? Base : Base + TEXT("-Cmd")) + Ext);
		}
		const FString Plain = FString(TEXT("UnrealEditor-Cmd")) + Ext;
		Candidates.Add(Dir / Plain);
		Candidates.Add(FPaths::ConvertRelativePathToFull(FPaths::EngineDir() / TEXT("Binaries") / FPlatformProcess::GetBinariesSubdirectory()) / Plain);
		for (const FString& Candidate : Candidates)
		{
			if (FPaths::FileExists(Candidate))
			{
				return Candidate;
			}
		}
		return FString();
	}

	TOptional<TFuture<FString>> Checking;
	TOptional<FString> ExportFingerprint;
	FProcHandle Export;
	double NextPoll = 0;
	bool bRescanRequested = false;
	bool bReady = false;

	void Check()
	{
		bRescanRequested = false;
		Checking = Async(EAsyncExecution::ThreadPool, [Dirs = GetRoots().Content]() { return AssetFingerprint(Dirs); });
	}

	void Start(const FRoots& Roots, const FString& Fingerprint)
	{
		if (!IFileManager::Get().Delete(*SummaryPath(Roots), false, true))
		{
			UE_LOG(LogCluewren, Warning, TEXT("[Cluewren] Couldn't remove the outdated Blueprint summary in %s"), *Roots.Export);
			return;
		}
		const FString Cmd = FindEditorCmd();
		if (Cmd.IsEmpty())
		{
			UE_LOG(LogCluewren, Warning, TEXT("[Cluewren] UnrealEditor-Cmd wasn't found next to %s: no Blueprint export"), FPlatformProcess::ExecutablePath());
			return;
		}
		// Whoever makes .cluewren first keeps it out of the game's repository, the brain or this.
		const FString Ignore = Roots.Kept / TEXT(".gitignore");
		if (!FPaths::FileExists(Ignore))
		{
			FFileHelper::SaveStringToFile(TEXT("*\n"), *Ignore, FFileHelper::EEncodingOptions::ForceUTF8WithoutBOM);
		}
		const FString UProject = FPaths::ConvertRelativePathToFull(FPaths::GetProjectFilePath());
		const FString Log = Clean(FPaths::ProjectLogDir() / TEXT("CluewrenExport.log"));
		const FString Params = FString::Printf(TEXT("\"%s\" -run=CluewrenExport -out=\"%s\" -unattended -nullrhi -nosplash -nop4 -abslog=\"%s\""), *UProject, *Roots.Export, *Log);
		uint32 ProcessId = 0;
		// Hidden, below normal priority: the game keeps its frames
		Export = FPlatformProcess::CreateProc(*Cmd, *Params, false, true, true, &ProcessId, -1, nullptr, nullptr);
		if (Export.IsValid())
		{
			ExportFingerprint = Fingerprint;
			NextPoll = 0;
			UE_LOG(LogCluewren, Display, TEXT("[Cluewren] Exporting Blueprints to %s (process %u)"), *Roots.Export, ProcessId);
		}
		else
		{
			UE_LOG(LogCluewren, Warning, TEXT("[Cluewren] Couldn't start %s"), *Cmd);
		}
	}
}

void Refresh()
{
	bReady = false;
	if (Checking.IsSet())
	{
		bRescanRequested = true;
	}
	else if (!Export.IsValid())
	{
		Check();
	}
}

const TCHAR* State()
{
	if (Checking.IsSet() || Export.IsValid())
	{
		return TEXT("pending");
	}
	return bReady ? TEXT("ready") : TEXT("unavailable");
}

void Tick()
{
	if (Checking.IsSet() && Checking->IsReady())
	{
		const FString Fingerprint = Checking->Get();
		Checking.Reset();
		if (bRescanRequested)
		{
			Check();
			return;
		}
		const FRoots Roots = GetRoots();
		if (ExportFingerprint.IsSet() && ExportFingerprint.GetValue() == Fingerprint)
		{
			bReady = FFileHelper::SaveStringToFile(Fingerprint, *FingerprintPath(Roots), FFileHelper::EEncodingOptions::ForceUTF8WithoutBOM)
				&& IsCurrent(Roots, Fingerprint);
		}
		else if (IsCurrent(Roots, Fingerprint))
		{
			bReady = true;
		}
		else
		{
			ExportFingerprint.Reset();
			Start(Roots, Fingerprint);
			return;
		}
		ExportFingerprint.Reset();
	}
	if (!Export.IsValid())
	{
		return;
	}
	const double Now = FPlatformTime::Seconds();
	if (Now < NextPoll)
	{
		return;
	}
	NextPoll = Now + PollSeconds;
	if (!FPlatformProcess::IsProcRunning(Export))
	{
		int32 Code = -1;
		FPlatformProcess::GetProcReturnCode(Export, &Code);
		FPlatformProcess::CloseProc(Export);
		Export = FProcHandle();
		UE_LOG(LogCluewren, Display, TEXT("[Cluewren] Blueprint export ended with code %d"), Code);
		const FRoots Roots = GetRoots();
		if (Code == 0 && FPaths::FileExists(SummaryPath(Roots)) && FPaths::FileExists(Roots.Export / TEXT("assets.txt")))
		{
			// Assets saved while the commandlet ran may have missed that export.
			Check();
		}
		else
		{
			ExportFingerprint.Reset();
			IFileManager::Get().Delete(*SummaryPath(Roots), false, true);
		}
	}
}

void Shutdown()
{
	if (Checking.IsSet())
	{
		Checking->Wait();
		Checking.Reset();
	}
	if (Export.IsValid())
	{
		if (FPlatformProcess::IsProcRunning(Export))
		{
			FPlatformProcess::TerminateProc(Export, true);
		}
		FPlatformProcess::CloseProc(Export);
		Export = FProcHandle();
	}
	ExportFingerprint.Reset();
	bReady = false;
}

#if WITH_DEV_AUTOMATION_TESTS
IMPLEMENT_SIMPLE_AUTOMATION_TEST(FCluewrenExportFingerprintTest, "Cluewren.Export.SavedAssets",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FCluewrenExportFingerprintTest::RunTest(const FString& Parameters)
{
	const FString Sandbox = FPaths::CreateTempFilename(*FPaths::ProjectIntermediateDir(), TEXT("CluewrenExportTest"), TEXT(""));
	FRoots Roots;
	Roots.Export = Sandbox / TEXT("Export");
	Roots.Content = { Sandbox / TEXT("Content"), Sandbox / TEXT("PluginContent") };
	IFileManager& Files = IFileManager::Get();
	Files.MakeDirectory(*Roots.Export, true);
	for (const FString& Dir : Roots.Content)
	{
		Files.MakeDirectory(*Dir, true);
	}
	const FString Asset = Roots.Content[0] / TEXT("Blueprint.uasset");
	const FString PluginMaps = Roots.Content[1] / TEXT("ThirdParty");
	Files.MakeDirectory(*PluginMaps, true);
	const FString Map = PluginMaps / TEXT("Level.umap");
	TestTrue(TEXT("Save test asset"), FFileHelper::SaveStringToFile(TEXT("asset"), *Asset));
	TestTrue(TEXT("Save test plugin map"), FFileHelper::SaveStringToFile(TEXT("map"), *Map));
	TestTrue(TEXT("Write summary"), FFileHelper::SaveStringToFile(TEXT("summary"), *SummaryPath(Roots)));
	TestTrue(TEXT("Write asset list"), FFileHelper::SaveStringToFile(TEXT("assets"), *(Roots.Export / TEXT("assets.txt"))));
	const FString Exported = AssetFingerprint(Roots.Content);
	TestFalse(TEXT("A legacy export has no asset fingerprint"), IsCurrent(Roots, Exported));
	TestTrue(TEXT("Write export fingerprint"), FFileHelper::SaveStringToFile(Exported, *FingerprintPath(Roots)));
	TestTrue(TEXT("Unchanged saved assets reuse the export"), IsCurrent(Roots, AssetFingerprint(Roots.Content)));
	TestTrue(TEXT("Save a non-asset file"), FFileHelper::SaveStringToFile(TEXT("notes"), *(Roots.Content[0] / TEXT("notes.txt"))));
	TestTrue(TEXT("Non-asset files do not invalidate the export"), IsCurrent(Roots, AssetFingerprint(Roots.Content)));
	TestTrue(TEXT("Delete saved plugin map"), Files.Delete(*Map, true));
	TestFalse(TEXT("Deleting a map invalidates the export"), IsCurrent(Roots, AssetFingerprint(Roots.Content)));
	TestTrue(TEXT("Delete saved asset"), Files.Delete(*Asset, true));
	TestFalse(TEXT("Deleting all assets invalidates the export"), IsCurrent(Roots, AssetFingerprint(Roots.Content)));
	Files.DeleteDirectory(*Sandbox, false, true);
	return true;
}
#endif
}

#endif
