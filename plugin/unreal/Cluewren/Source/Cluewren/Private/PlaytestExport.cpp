// Modified by Crane Valley: the export starting and staleness parts of upstream's PlaytestCodex.cpp; the codex_manifest and
// codex_read ops, hashing, progress and stopping are removed (ADR 0003), and a run start triggers it.
// Keeps Saved/CluewrenCodex fresh: the Blueprint export the CluewrenCodex commandlet writes, run as a child
// process, never in this one: it loads every asset.

#include "PlaytestInternal.h"

#if WITH_EDITOR

#include "Async/Async.h"
#include "HAL/FileManager.h"
#include "HAL/PlatformProcess.h"
#include "HAL/PlatformTime.h"
#include "Misc/Paths.h"

namespace PlaytestExport
{
namespace
{
	constexpr double PollSeconds = 1.0;

	/** Absolute, forward slashes, no trailing slash. Worked out on the game thread. */
	struct FRoots
	{
		FString Project;  // the project's directory
		FString Export;   // <ProjectSavedDir>/CluewrenCodex
		FString Content;  // the project's Content/
		FString Plugins;  // the project's Plugins/
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
		Roots.Export = Clean(FPaths::ProjectSavedDir() / TEXT("CluewrenCodex"));
		Roots.Content = Clean(FPaths::ProjectContentDir());
		Roots.Plugins = Clean(FPaths::ProjectPluginsDir());
		return Roots;
	}

	bool IsSkippedDir(const FString& Name)
	{
		return Name.Equals(TEXT("Intermediate"), ESearchCase::IgnoreCase) || Name.Equals(TEXT("Binaries"), ESearchCase::IgnoreCase)
			|| Name.Equals(TEXT("ThirdParty"), ESearchCase::IgnoreCase) || Name.Equals(TEXT("node_modules"), ESearchCase::IgnoreCase)
			|| Name.Equals(TEXT(".git"), ESearchCase::IgnoreCase);
	}

	/** Whether a .uasset or .umap under Dir (skipped directories left out) was written after Since. */
	bool HasNewerAsset(const FString& Dir, const FDateTime& Since)
	{
		bool bNewer = false;
		TArray<FString> SubDirs;
		IFileManager::Get().IterateDirectoryStat(*Dir, [&](const TCHAR* Found, const FFileStatData& Stat)
		{
			const FString Name = FPaths::GetCleanFilename(Found);
			if (Stat.bIsDirectory)
			{
				if (!IsSkippedDir(Name))
				{
					SubDirs.Add(Dir / Name);
				}
			}
			else if (Stat.ModificationTime > Since)
			{
				const FString Ext = FPaths::GetExtension(Name);
				bNewer = Ext.Equals(TEXT("uasset"), ESearchCase::IgnoreCase) || Ext.Equals(TEXT("umap"), ESearchCase::IgnoreCase);
			}
			return !bNewer;
		});
		for (int32 i = 0; i < SubDirs.Num() && !bNewer; ++i)
		{
			bNewer = HasNewerAsset(SubDirs[i], Since);
		}
		return bNewer;
	}

	/** The Content/ of each plugin under Dir (a directory holding a .uplugin), this one left out. */
	void FindPluginContent(const FString& Dir, TArray<FString>& Out)
	{
		TArray<FString> SubDirs;
		bool bPlugin = false;
		bool bOurs = false;
		IFileManager::Get().IterateDirectoryStat(*Dir, [&](const TCHAR* Found, const FFileStatData& Stat)
		{
			const FString Name = FPaths::GetCleanFilename(Found);
			if (Stat.bIsDirectory)
			{
				if (!IsSkippedDir(Name))
				{
					SubDirs.Add(Dir / Name);
				}
			}
			else if (FPaths::GetExtension(Name).Equals(TEXT("uplugin"), ESearchCase::IgnoreCase))
			{
				bPlugin = true;
				bOurs |= Name.Equals(TEXT("Cluewren.uplugin"), ESearchCase::IgnoreCase);
			}
			return true;
		});
		if (bPlugin)
		{
			if (!bOurs)
			{
				Out.Add(Dir / TEXT("Content"));
			}
			return;  // (no plugin inside a plugin)
		}
		for (const FString& Sub : SubDirs)
		{
			FindPluginContent(Sub, Out);
		}
	}

	/** File times only, off the game thread: there is no export, or an asset was saved after its summary.txt. */
	bool IsNeeded(const FRoots& Roots)
	{
		// (the stat the walk reads too: the same clock and precision)
		const FFileStatData Summary = IFileManager::Get().GetStatData(*(Roots.Export / TEXT("summary.txt")));
		if (!Summary.bIsValid)
		{
			return true;
		}
		TArray<FString> Dirs = {Roots.Content};
		FindPluginContent(Roots.Plugins, Dirs);
		for (const FString& Dir : Dirs)
		{
			if (HasNewerAsset(Dir, Summary.ModificationTime))
			{
				return true;
			}
		}
		return false;
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

	TOptional<TFuture<bool>> Checking;
	FProcHandle Export;
	double NextPoll = 0;

	void Start()
	{
		const FString Cmd = FindEditorCmd();
		if (Cmd.IsEmpty())
		{
			UE_LOG(LogPlaytest, Warning, TEXT("[Cluewren] UnrealEditor-Cmd wasn't found next to %s: no Blueprint export"), FPlatformProcess::ExecutablePath());
			return;
		}
		const FRoots Roots = GetRoots();
		const FString UProject = FPaths::ConvertRelativePathToFull(FPaths::GetProjectFilePath());
		const FString Log = Roots.Project / TEXT("Saved/Logs/CluewrenCodexExport.log");
		const FString Params = FString::Printf(TEXT("\"%s\" -run=CluewrenCodex -out=\"%s\" -unattended -nullrhi -nosplash -nop4 -abslog=\"%s\""), *UProject, *Roots.Export, *Log);
		uint32 ProcessId = 0;
		// Hidden, below normal priority: the game keeps its frames
		Export = FPlatformProcess::CreateProc(*Cmd, *Params, false, true, true, &ProcessId, -1, nullptr, nullptr);
		if (Export.IsValid())
		{
			UE_LOG(LogPlaytest, Display, TEXT("[Cluewren] Exporting Blueprints to %s (process %u)"), *Roots.Export, ProcessId);
		}
		else
		{
			UE_LOG(LogPlaytest, Warning, TEXT("[Cluewren] Couldn't start %s"), *Cmd);
		}
	}
}

void Refresh()
{
	if (Export.IsValid() || Checking.IsSet())
	{
		return;
	}
	Checking = Async(EAsyncExecution::ThreadPool, [Roots = GetRoots()]() { return IsNeeded(Roots); });
}

void Tick()
{
	if (Checking.IsSet() && Checking->IsReady())
	{
		const bool bNeeded = Checking->Get();
		Checking.Reset();
		if (bNeeded)
		{
			Start();
		}
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
		UE_LOG(LogPlaytest, Display, TEXT("[Cluewren] Blueprint export ended with code %d"), Code);
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
}
}

#endif
