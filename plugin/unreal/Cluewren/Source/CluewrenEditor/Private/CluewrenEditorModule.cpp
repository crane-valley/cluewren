// Modified by Crane Valley: reduced to starting and stopping play for a run, which the agent starts over MCP: the panel, the
// session's run requests, the style and the toolbar are removed, and the editor keeps its pace while the brain is connected.
#include "Containers/Ticker.h"
#include "Editor.h"
#include "Editor/EditorEngine.h"
#include "Engine/World.h"
#include "Modules/ModuleManager.h"
#include "PlayInEditorDataTypes.h"
#include "PlaytestLevels.h"
#include "PlaytestSubsystem.h"

namespace
{
	constexpr double SessionTimeout = 30.0;

	bool PieRunning()
	{
		return GEditor && GEditor->PlayWorld != nullptr;
	}

	/** Play In Editor is up with a world to play in: the player's (a client of a dedicated server joins it after the
	 *  server's world has begun play). */
	bool PieReady()
	{
		const UPlaytestSubsystem* Bridge = UPlaytestSubsystem::Get();
		const UWorld* World = Bridge ? Bridge->GetGameWorld() : nullptr;
		return GEditor && GEditor->PlayWorld && World && World->HasBegunPlay();
	}

	/** Call Done once Condition holds (checked every tick), or with an error after the timeout. */
	void WaitFor(TFunction<bool()> Condition, TFunction<void(const FString&)> Done)
	{
		const double Start = FPlatformTime::Seconds();
		FTSTicker::GetCoreTicker().AddTicker(FTickerDelegate::CreateLambda([Condition, Done, Start](float)
		{
			if (Condition())
			{
				Done(FString());
				return false;
			}
			if (FPlatformTime::Seconds() - Start > SessionTimeout)
			{
				const UPlaytestSubsystem* Bridge = UPlaytestSubsystem::Get();
				const FString Problem = Bridge ? Bridge->GetPlayProblem() : FString();
				Done(Problem.IsEmpty() ? FString(TEXT("Timed out waiting for the play session"))
					: FString::Printf(TEXT("Timed out waiting for the play session: %s"), *Problem));
				return false;
			}
			return true;
		}));
	}

	/** Play In Editor, in the level Map when given (else the one open in the editor). Another level plays through
	 *  the play session's map override, as Movie Render Queue's PIE renders do: the editor keeps its own level open,
	 *  unsaved changes and all. */
	void StartPie(const FString& Map, TFunction<void(bool, const FString&)> Done)
	{
		if (PieReady())
		{
			Done(true, FString());
			return;
		}
		FString Package, Error;
		if (!Map.IsEmpty() && !PlaytestLevels::Resolve(Map, Package, Error))
		{
			Done(PieRunning(), Error);
			return;
		}
		if (!PieRunning() && !GEditor->IsPlaySessionRequestQueued())
		{
			FRequestPlaySessionParams Params;
			if (!Package.IsEmpty() && Package != PlaytestLevels::EditorLevel())  // (the open one plays as it is, unsaved)
			{
				Params.GlobalMapOverride = PlaytestLevels::ObjectPath(Package);
			}
			GEditor->RequestPlaySession(Params);
		}
		WaitFor(&PieReady, [Done](const FString& Error) { Done(PieRunning(), Error); });
	}

	void StopPie(TFunction<void(bool, const FString&)> Done)
	{
		if (!PieRunning())
		{
			Done(false, FString());
			return;
		}
		GEditor->RequestEndPlayMap();
		WaitFor([]() { return !PieRunning(); }, [Done](const FString& Error) { Done(PieRunning(), Error); });
	}

	void HandleSession(const FString& Action, const FString& Map, TFunction<void(bool, const FString&)> Done)
	{
		if (!GEditor)
		{
			Done(false, TEXT("No editor"));
			return;
		}
		if (Action == TEXT("start"))
		{
			StartPie(Map, Done);
		}
		else if (Action == TEXT("stop"))
		{
			StopPie(Done);
		}
		else if (Action == TEXT("restart"))
		{
			FString Package, Error;
			if (!Map.IsEmpty() && !PlaytestLevels::Resolve(Map, Package, Error))
			{
				Done(PieRunning(), Error);  // (before stopping: a level that can't play leaves the game as it is)
				return;
			}
			StopPie([Map, Done](bool, const FString& Error)
			{
				if (!Error.IsEmpty())
				{
					Done(PieRunning(), Error);
					return;
				}
				StartPie(Map, Done);
			});
		}
		else
		{
			Done(PieRunning(), FString());
		}
	}
}

class FCluewrenEditorModule : public IModuleInterface
{
public:
	virtual void StartupModule() override
	{
		if (UPlaytestSubsystem* Bridge = UPlaytestSubsystem::Get())
		{
			Bridge->SessionHandler = &HandleSession;
			Bridge->SessionRequested = []() { return GEditor && GEditor->IsPlaySessionRequestQueued(); };
		}
		if (GEditor)
		{
			// While the brain is here the editor doesn't slow down: "Use Less CPU when in Background", on by default, drops an
			// unfocused editor to a few frames a second, and the agent would play at that pace. The user's preference stays.
			UEditorEngine::FShouldDisableCPUThrottling KeepPlaying = UEditorEngine::FShouldDisableCPUThrottling::CreateLambda([]()
			{
				const UPlaytestSubsystem* Bridge = UPlaytestSubsystem::Get();
				return Bridge && Bridge->IsConnected();
			});
			ThrottleHandle = KeepPlaying.GetHandle();
			GEditor->ShouldDisableCPUThrottlingDelegates.Add(MoveTemp(KeepPlaying));
		}
	}

	virtual void ShutdownModule() override
	{
		if (UObjectInitialized())
		{
			if (UPlaytestSubsystem* Bridge = UPlaytestSubsystem::Get())
			{
				Bridge->SessionHandler = nullptr;
				Bridge->SessionRequested = nullptr;
			}
		}
		if (GEditor)
		{
			GEditor->ShouldDisableCPUThrottlingDelegates.RemoveAll(
				[this](const UEditorEngine::FShouldDisableCPUThrottling& Delegate) { return Delegate.GetHandle() == ThrottleHandle; });
		}
	}

private:
	FDelegateHandle ThrottleHandle;
};

IMPLEMENT_MODULE(FCluewrenEditorModule, CluewrenEditor)
