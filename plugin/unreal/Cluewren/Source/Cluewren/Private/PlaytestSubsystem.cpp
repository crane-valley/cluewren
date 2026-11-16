// Modified by Crane Valley: advertises this process, connects over PlaytestSocket to the brain that invites it with a
// mutual HMAC handshake and resumes its session after a drop; the account, sign-in, Aura, agent service, run request,
// usage, memory, inspection, steering and report plumbing is removed.
#include "PlaytestSubsystem.h"

#include "Engine/Engine.h"
#include "Engine/World.h"
#include "GeneralProjectSettings.h"
#include "HAL/PlatformMisc.h"
#include "Interfaces/IPluginManager.h"
#include "Kismet/GameplayStatics.h"
#include "Misc/App.h"
#include "Misc/Base64.h"
#include "Misc/CommandLine.h"
#include "Misc/CoreDelegates.h"
#include "Misc/EngineVersion.h"
#include "Misc/EngineVersionComparison.h"
#include "Misc/Parse.h"
#include "PlaytestBrain.h"
#include "PlaytestInternal.h"
#include "PlaytestJson.h"
#include "PlaytestLevels.h"
#include "PlaytestRefusal.h"
#include "PlaytestSocket.h"
#include "UObject/Package.h"

namespace
{
	constexpr double RetrySeconds = 1.0;
	constexpr double HandshakeSeconds = 10.0;
	constexpr double DefaultConnectTimeout = 120.0;
	constexpr int32 MaxHeldResponses = 200;
	constexpr int32 MaxLogEventsPerFrame = 50;

	/**
	 * Exit with a code CI can see. A clean exit request drops the code on Windows (it only posts a quit
	 * message, and the launcher returns the engine's own error level), so exit cleanly as usual and set
	 * the code at the very end of shutdown, once the world and the engine are gone and the log is flushed.
	 */
	void ExitWithCode(int32 Code)
	{
		static int32 ExitCode = 0;
		static bool bHooked = false;
		ExitCode = Code;
		if (!bHooked && Code != 0)
		{
			bHooked = true;
			FCoreDelegates::OnExit.AddLambda([]()
			{
				if (ExitCode != 0)
				{
					if (GLog)
					{
						GLog->Flush();
					}
#if UE_VERSION_OLDER_THAN(5, 3, 0)
					FPlatformMisc::RequestExitWithStatus(true, (uint8)ExitCode);
#else
					FPlatformMisc::RequestExitWithStatus(true, (uint8)ExitCode, TEXT("Cluewren"));
#endif
				}
			});
		}
#if UE_VERSION_OLDER_THAN(5, 3, 0)
		FPlatformMisc::RequestExitWithStatus(false, (uint8)Code);
#else
		FPlatformMisc::RequestExitWithStatus(false, (uint8)Code, TEXT("Cluewren"));
#endif
	}

	double Now()
	{
		return FPlatformTime::Seconds();
	}

	TSharedRef<FJsonValue> GameTimeJson(double GameTime)
	{
		if (GameTime < 0)
		{
			return MakeShared<FJsonValueNull>();
		}
		return PlaytestJson::Number(GameTime);
	}

	bool Bytes(const TSharedPtr<FJsonObject>& Message, const TCHAR* Field, TArray<uint8>& Out)
	{
		FString Text;
		return Message->TryGetStringField(Field, Text) && FBase64::Decode(Text, Out);
	}

	FString PluginVersion()
	{
		const TSharedPtr<IPlugin> Plugin = IPluginManager::Get().FindPlugin(TEXT("Cluewren"));
		return Plugin.IsValid() ? Plugin->GetDescriptor().VersionName : FString(TEXT("0.2.0"));
	}

	FString EngineVersion()
	{
		return FEngineVersion::Current().ToString(EVersionComponent::Patch);
	}
}

DEFINE_LOG_CATEGORY(LogCluewren);

bool UPlaytestSubsystem::FSession::Welcomed(const FString& Session)
{
	const bool bResumed = !Asked.IsEmpty() && Session == Asked;
	if (!bResumed)
	{
		Received = 0;
		Held.Reset();
	}
	Id = Session;
	return bResumed;
}

UPlaytestSubsystem* UPlaytestSubsystem::Get()
{
	return GEngine ? GEngine->GetEngineSubsystem<UPlaytestSubsystem>() : nullptr;
}

void UPlaytestSubsystem::Initialize(FSubsystemCollectionBase& Collection)
{
	Super::Initialize(Collection);
	if (IsRunningCommandlet())
	{
		return;
	}
	bOnCommandLine = FParse::Param(FCommandLine::Get(), TEXT("CluewrenConnect"));
	if (!bOnCommandLine && (!GIsEditor || FApp::IsUnattended()))
	{
		return;  // (a game, or an unattended editor, advertises nothing and connects nowhere unless asked)
	}
	Rendezvous = PlaytestBrain::Folder();
	PlaytestBrain::FAdvert Advert;
	Advert.Pid = FPlatformProcess::GetCurrentProcessId();
	Advert.Id = PlaytestBrain::EditorId();
	Advert.EngineVersion = EngineVersion();
	Advert.PluginVersion = PluginVersion();
	Advert.ProjectDir = PlaytestBrain::ProjectDir();
	Advert.bEditor = GIsEditor;
	InvitePath = PlaytestBrain::InvitePath(Rendezvous, Advert.Pid);
	if (!PlaytestBrain::WriteAdvert(Rendezvous, Advert))
	{
		UE_LOG(LogCluewren, Warning, TEXT("[Cluewren] %s couldn't be written: no run will find this process"),
			*PlaytestBrain::AdvertPath(Rendezvous, Advert.Pid));
	}
	TickHandle = FTSTicker::GetCoreTicker().AddTicker(FTickerDelegate::CreateUObject(this, &UPlaytestSubsystem::Tick));
	if (bOnCommandLine)
	{
		Prepare();
		double Timeout = DefaultConnectTimeout;
		FParse::Value(FCommandLine::Get(), TEXT("CluewrenConnectTimeout="), Timeout);
		ConnectDeadline = Timeout > 0 ? Now() + Timeout : 0;
		FParse::Value(FCommandLine::Get(), TEXT("CluewrenMap="), StartMap);
	}
}

// Not at every editor start, nor on an invite alone: only once a brain has proved itself (or at once with -CluewrenConnect),
// so that what isn't the brain puts nothing in the log's path.
void UPlaytestSubsystem::Prepare()
{
	if (Detectors.IsValid())
	{
		return;
	}
	Detectors = MakeShared<FPlaytestDetectors>(*this);
	LogCapture = MakeShared<FPlaytestLogCapture>();
	Media = MakeShared<FPlaytestMedia>(*this);
	if (GLog)
	{
		GLog->AddOutputDevice(LogCapture.Get());
	}
}

void UPlaytestSubsystem::Deinitialize()
{
	if (TickHandle.IsValid())
	{
		FTSTicker::GetCoreTicker().RemoveTicker(TickHandle);
	}
	// Not under the ticker's handle: the engine empties the core ticker before it shuts its subsystems down.
	if (!Rendezvous.IsEmpty())
	{
		PlaytestBrain::RemoveAdvert(Rendezvous, FPlatformProcess::GetCurrentProcessId());
	}
#if WITH_EDITOR
	PlaytestExport::Shutdown();
#endif
	PlaytestOps::ReleasePointer();
	if (Media.IsValid())
	{
		Media->Shutdown();
	}
	if (GLog && LogCapture.IsValid())
	{
		GLog->RemoveOutputDevice(LogCapture.Get());
	}
	if (Socket.IsValid())
	{
		Socket->CloseCleanly();
		Socket.Reset();
	}
	Super::Deinitialize();
}

void UPlaytestSubsystem::TryConnect(double T)
{
	NextAttempt = T + RetrySeconds;
	const uint64 Written = PlaytestBrain::InviteWritten(InvitePath);
	if (Written == 0)
	{
		InviteSeen = Written;
		Invited.Reset();
		Status(TEXT("Waiting for an invite from the cluewren command"));
		return;
	}
	if (Written != InviteSeen)
	{
		Invited.Reset();
		const TSharedRef<PlaytestBrain::FAddress> Brain = MakeShared<PlaytestBrain::FAddress>();
		FString Why;
		const PlaytestBrain::EInvite Verdict = PlaytestBrain::ReadInvite(InvitePath, PlaytestBrain::EditorId(), *Brain, Why);
		if (!Why.IsEmpty())
		{
			Status(Why);
		}
		if (Verdict == PlaytestBrain::EInvite::Unreadable)
		{
			return;  // (it may be readable in a second without having changed)
		}
		InviteSeen = Written;
		if (Verdict != PlaytestBrain::EInvite::Taken)
		{
			return;
		}
		Invited = Brain;
	}
	else if (Invited.IsValid() && !PlaytestBrain::Alive(*Invited))
	{
		Invited.Reset();
		Status(PlaytestBrain::RunEnded);
	}
	if (!Invited.IsValid())
	{
		return;
	}
	bHelloSent = false;
	HandshakeDeadline = T + HandshakeSeconds;
	Socket = MakeShared<FPlaytestSocket>(Invited->Host, Invited->Port, Invited->Path);
}

void UPlaytestSubsystem::SendHello()
{
	PluginNonce = PlaytestBrain::NewNonce();
	TSharedRef<FJsonObject> Hello = MakeShared<FJsonObject>();
	Hello->SetStringField(TEXT("type"), TEXT("hello"));
	Hello->SetStringField(TEXT("plugin_version"), PluginVersion());
	Hello->SetStringField(TEXT("engine"), TEXT("unreal"));
	Hello->SetStringField(TEXT("engine_version"), EngineVersion());
	Hello->SetStringField(TEXT("project"), FApp::GetProjectName());
	// What this plugin does beyond the protocol's first version: the brain falls back for a plugin without them
	TArray<TSharedPtr<FJsonValue>> Features;
	Features.Add(MakeShared<FJsonValueString>(TEXT("timed_hold")));  // key hold with "seconds", let go in-game
	Features.Add(MakeShared<FJsonValueString>(TEXT("pointer")));  // `pointer`
	Features.Add(MakeShared<FJsonValueString>(TEXT("levels")));  // `levels`, and `session` playing the `map` asked for
	if (FPlaytestMedia::CanCapture())
	{
		Features.Add(MakeShared<FJsonValueString>(TEXT("media")));  // `record` and `capture` (a game that renders)
	}
	Hello->SetArrayField(TEXT("features"), Features);
	// The project's own GUID (DefaultGame.ini, made with the project and committed with it): a stable id a brain can
	// keep what it learns by, so two games with the same name (every "MyProject") stay apart
	const FGuid& ProjectId = GetDefault<UGeneralProjectSettings>()->ProjectID;
	if (ProjectId.IsValid())
	{
		Hello->SetStringField(TEXT("project_id"), ProjectId.ToString(EGuidFormats::DigitsWithHyphensLower));
	}
	Hello->SetStringField(TEXT("host"), GIsEditor ? TEXT("editor") : TEXT("game"));
	PlaytestBrain::Sign(*Hello, Invited->Secret, PluginNonce, PlaytestBrain::ProjectDir());
	if (!Session.Id.IsEmpty())
	{
		Hello->SetStringField(TEXT("resume"), Session.Id);
		Hello->SetNumberField(TEXT("received"), (double)Session.Received);
	}
	Session.Asked = Session.Id;
	Socket->Send(Hello);
}

void UPlaytestSubsystem::Send(const TSharedRef<FJsonObject>& Message)
{
	if (bConnected)
	{
		Socket->Send(Message);
	}
}

void UPlaytestSubsystem::SendOrHold(const TSharedRef<FJsonObject>& Message)
{
	if (bConnected)
	{
		Socket->Send(Message);
	}
	else if (!Session.Id.IsEmpty() && Session.Held.Num() < MaxHeldResponses)
	{
		Session.Held.Add({Message, {}});
	}
}

void UPlaytestSubsystem::SendFrame(TArray<uint8>&& Text)
{
	if (bConnected)
	{
		Socket->SendBytes(MoveTemp(Text));
	}
}

void UPlaytestSubsystem::SendResponseText(TArray<uint8>&& Text)
{
	if (bConnected)
	{
		Socket->SendBytes(MoveTemp(Text));
	}
	else if (!Session.Id.IsEmpty() && Session.Held.Num() < MaxHeldResponses)
	{
		Session.Held.Add({nullptr, MoveTemp(Text)});
	}
}

void UPlaytestSubsystem::Status(const FString& Text)
{
	if (Text != LastStatus)
	{
		LastStatus = Text;
		UE_LOG(LogCluewren, Display, TEXT("[Cluewren] %s"), *Text);
	}
}

UWorld* UPlaytestSubsystem::GetGameWorld() const
{
	if (!GEngine)
	{
		return nullptr;
	}
	UWorld* Game = nullptr;
	UWorld* Pie = nullptr;
	int32 PieInstance = MAX_int32;
	bool bAnyPie = false;
	for (const FWorldContext& Context : GEngine->GetWorldContexts())
	{
		UWorld* World = Context.World();
		if (!World)
		{
			continue;
		}
		if (Context.WorldType == EWorldType::PIE)
		{
			// (the first of several clients: the lowest PIE instance; a dedicated server's is never played)
			bAnyPie = true;
			if (!Context.RunAsDedicated && World->GetNetMode() != NM_DedicatedServer && Context.PIEInstance < PieInstance)
			{
				Pie = World;
				PieInstance = Context.PIEInstance;
			}
		}
		else if (Context.WorldType == EWorldType::Game && !Game)
		{
			Game = World;
		}
	}
	return bAnyPie ? Pie : Game;
}

UWorld* UPlaytestSubsystem::GetServerWorld() const
{
	UWorld* Played = GetGameWorld();
	if (!Played || Played->GetNetMode() != NM_Client || !GEngine)
	{
		return Played;
	}
	for (const FWorldContext& Context : GEngine->GetWorldContexts())
	{
		UWorld* World = Context.World();
		if (World && World != Played && Context.WorldType == EWorldType::PIE && World->GetNetMode() != NM_Client)
		{
			return World;
		}
	}
	return nullptr;
}

FString UPlaytestSubsystem::GetPlayProblem() const
{
	if (!GEngine || GetGameWorld())
	{
		return FString();
	}
	for (const FWorldContext& Context : GEngine->GetWorldContexts())
	{
		if (Context.WorldType == EWorldType::PIE && Context.World())
		{
			return TEXT("Play In Editor runs a dedicated server, but its client hasn't joined it (yet): no player to "
				"play as");
		}
	}
	return FString();
}

double UPlaytestSubsystem::GameTime() const
{
	UWorld* World = GetGameWorld();
	return World ? World->GetTimeSeconds() : -1.0;
}

void UPlaytestSubsystem::QueueEvent(const TSharedRef<FJsonObject>& Event)
{
	OutgoingEvents.Add(MakeShared<FJsonValueObject>(Event));
}

void UPlaytestSubsystem::Respond(int64 Id, const TSharedPtr<FJsonValue>& Result)
{
	TSharedRef<FJsonObject> Message = MakeShared<FJsonObject>();
	Message->SetStringField(TEXT("type"), TEXT("response"));
	Message->SetNumberField(TEXT("id"), (double)Id);
	Message->SetBoolField(TEXT("ok"), true);
	Message->SetField(TEXT("result"), Result.IsValid() ? Result : MakeShared<FJsonValueObject>(MakeShared<FJsonObject>()));
	Message->SetField(TEXT("t"), GameTimeJson(GameTime()));
	SendOrHold(Message);
}

void UPlaytestSubsystem::RespondError(int64 Id, const FString& Error, int32 StatusCode)
{
	TSharedRef<FJsonObject> Message = MakeShared<FJsonObject>();
	Message->SetStringField(TEXT("type"), TEXT("response"));
	Message->SetNumberField(TEXT("id"), (double)Id);
	Message->SetBoolField(TEXT("ok"), false);
	Message->SetNumberField(TEXT("status"), StatusCode);
	Message->SetStringField(TEXT("error"), Error);
	Message->SetField(TEXT("t"), GameTimeJson(GameTime()));
	SendOrHold(Message);
}

void UPlaytestSubsystem::AddPending(FPending&& Pending)
{
	// Give it its first tick right away, so e.g. movement starts in the frame the request arrived
	if (Pending.Tick && Pending.Tick(Pending, Now()))
	{
		return;
	}
	PendingOps.Add(MoveTemp(Pending));
}

void UPlaytestSubsystem::HandleMessage(const TSharedPtr<FJsonObject>& Message)
{
	FString Type;
	if (Message.IsValid())
	{
		Message->TryGetStringField(TEXT("type"), Type);
	}
	if (!bConnected)
	{
		if (Type == TEXT("welcome"))
		{
			HandleWelcome(Message);
		}
		else
		{
			// Its message is never shown: whoever wrote it has proved nothing yet.
			FString Code;
			if (Type == TEXT("error"))
			{
				Message->TryGetStringField(TEXT("code"), Code);
			}
			Refuse(PlaytestBrain::RefusalText(Code));
		}
		return;
	}
	if (Type == TEXT("request"))
	{
		HandleRequest(Message);
	}
	else if (Type == TEXT("status"))
	{
		Status(Message->GetStringField(TEXT("text")));
	}
	else if (Type == TEXT("run_started"))
	{
		bRunActive = true;
		ShowRun();
	}
	else if (Type == TEXT("result"))
	{
		bRunActive = false;
		ShowRun();
	}
	else if (Type == TEXT("error"))
	{
		Refuse(Message->GetStringField(TEXT("message")));
	}
}

void UPlaytestSubsystem::ShowRun()
{
	Status(bRunActive ? TEXT("Connected to the brain: a run is active") : TEXT("Connected to the brain: no run is active"));
}

void UPlaytestSubsystem::HandleRequest(const TSharedPtr<FJsonObject>& Message)
{
	const int64 Id = (int64)Message->GetNumberField(TEXT("id"));
	Session.Received = FMath::Max(Session.Received, Id);
	const TSharedPtr<FJsonObject>* Args = nullptr;
	const TSharedPtr<FJsonObject> ArgsObj = Message->TryGetObjectField(TEXT("args"), Args) && Args ? *Args : MakeShared<FJsonObject>();
	PlaytestOps::Handle(*this, Id, Message->GetStringField(TEXT("op")), ArgsObj);
}

void UPlaytestSubsystem::HandleWelcome(const TSharedPtr<FJsonObject>& Message)
{
	TArray<uint8> BrainNonce, Proof;
	if (!Bytes(Message, TEXT("nonce"), BrainNonce) || BrainNonce.Num() != PlaytestBrain::NonceBytes || !Bytes(Message, TEXT("proof"), Proof)
		|| !PlaytestBrain::Verify(Invited->Secret, PlaytestBrain::BrainRole, PluginNonce, BrainNonce, Proof))
	{
		Refuse(PlaytestBrain::Unproven);
		return;
	}
	TSharedRef<FJsonObject> Answer = MakeShared<FJsonObject>();
	Answer->SetStringField(TEXT("type"), TEXT("proof"));
	Answer->SetStringField(TEXT("proof"), FBase64::Encode(PlaytestBrain::Prove(Invited->Secret, PlaytestBrain::PluginRole, PluginNonce, BrainNonce)));
	Socket->Send(Answer);
	PluginNonce.Empty();
	Prepare();
	FString Welcomed;
	Message->TryGetStringField(TEXT("session"), Welcomed);
	if (!Session.Welcomed(Welcomed))
	{
		++SessionSerial;
		bRunActive = false;
		PendingOps.Reset();
		Detectors->Reset();
#if WITH_EDITOR
		PlaytestExport::Refresh();
#endif
	}
	bConnected = true;
	LastHeartbeat = 0;
	for (FHeld& Held : Session.Held)
	{
		if (Held.Json.IsValid())
		{
			Socket->Send(Held.Json.ToSharedRef());
		}
		else
		{
			Socket->SendBytes(MoveTemp(Held.Bytes));
		}
	}
	Session.Held.Reset();
	ShowRun();
}

// No reconnect before the invite changes: the same invite gets the same answer from whatever answered here.
void UPlaytestSubsystem::Refuse(const FString& Why)
{
	Status(FString::Printf(TEXT("Refused: %s"), *Why));
	Invited.Reset();
	PluginNonce.Empty();
	Socket.Reset();
	bConnected = false;
	bRunActive = false;
	Session.Id.Empty();
	if (bOnCommandLine)
	{
		ExitWithCode(1);
	}
}

void UPlaytestSubsystem::ConnectionLost(double T, const FString& Why)
{
	const bool bWas = bConnected;
	Socket.Reset();
	bConnected = false;
	NextAttempt = T + RetrySeconds;
	PlaytestOps::ReleasePointer();
	if (bWas)
	{
		Status(FString::Printf(TEXT("Connection closed (%s)"), *Why));
	}
	else
	{
		Status(FString::Printf(TEXT("Can't reach the brain: %s"), *Why));
	}
}

void UPlaytestSubsystem::TickConnection(double T)
{
	if (ConnectDeadline > 0 && T >= ConnectDeadline && Session.Id.IsEmpty())
	{
		ConnectDeadline = 0;
		UE_LOG(LogCluewren, Error, TEXT("[Cluewren] No welcome from a brain within the connect timeout (%s)"), *LastStatus);
		Socket.Reset();
		ExitWithCode(1);
		return;
	}
	if (!Socket.IsValid())
	{
		if (T >= NextAttempt)
		{
			TryConnect(T);
		}
		return;
	}
	const FPlaytestSocket::EState State = Socket->GetState();
	if (State == FPlaytestSocket::EState::Open && !bHelloSent)
	{
		bHelloSent = true;
		SendHello();
	}
	Socket->Take(Received);
	for (const TSharedPtr<FJsonObject>& Message : Received)
	{
		HandleMessage(Message);
		if (!Socket.IsValid())
		{
			break;
		}
	}
	Received.Reset();
	if (!Socket.IsValid())
	{
		return;
	}
	if (State == FPlaytestSocket::EState::Closed)
	{
		FString Reason;
		const int32 Code = Socket->GetPeerClose(Reason);
		const FString Refusal = bConnected ? FString() : PlaytestBrain::RefusalOfClose(Code, Reason);
		if (!Refusal.IsEmpty())
		{
			Refuse(Refusal);
		}
		else
		{
			ConnectionLost(T, Socket->GetCloseReason(bConnected));
		}
	}
	else if (!bConnected && T >= HandshakeDeadline)
	{
		ConnectionLost(T, TEXT("no welcome in time"));
	}
}

void UPlaytestSubsystem::FlushEvents(double GameNow)
{
	if (OutgoingEvents.IsEmpty())
	{
		return;
	}
	TSharedRef<FJsonObject> Message = MakeShared<FJsonObject>();
	Message->SetStringField(TEXT("type"), TEXT("events"));
	Message->SetArrayField(TEXT("events"), OutgoingEvents);
	Message->SetField(TEXT("t"), GameTimeJson(GameNow));
	Send(Message);
	OutgoingEvents.Reset();
}

void UPlaytestSubsystem::CheckSessionChange(UWorld* World)
{
	if (World == LastWorld.Get() && (World != nullptr) == LastWorld.IsValid())
	{
		return;
	}
	LastWorld = World;
	Detectors->Reset();
	TSharedRef<FJsonObject> Event = MakeShared<FJsonObject>();
	Event->SetStringField(TEXT("kind"), TEXT("session"));
	Event->SetField(TEXT("t"), GameTimeJson(World ? World->GetTimeSeconds() : -1.0));
	Event->SetBoolField(TEXT("running"), World != nullptr);
	QueueEvent(Event);
}

void UPlaytestSubsystem::NoteLaunchMap()
{
	if (!LaunchMap.IsEmpty())
	{
		return;
	}
	// The first game world that began play (not the empty world a game instance makes before it loads its first map)
	UWorld* World = GetGameWorld();
	if (!World || !World->HasBegunPlay() || !World->GetPackage())
	{
		return;
	}
	LaunchMap = UWorld::RemovePIEPrefix(World->GetPackage()->GetName());
	// A game started with -CluewrenMap plays that level in place of the one it booted into, and a restart reopens it. (The
	// editor starts its play session in it: PlaytestOps::Session.)
	if (SessionHandler || StartMap.IsEmpty())
	{
		return;
	}
	if (PlaytestRefusal::DataPath(StartMap))
	{
		UE_LOG(LogCluewren, Warning, TEXT("[Cluewren] -CluewrenMap=%s is refused: nothing under .cluewren is opened as a level"), *StartMap);
		return;
	}
	FString Package, Error;
	if (!PlaytestLevels::Resolve(StartMap, Package, Error))
	{
		UE_LOG(LogCluewren, Warning, TEXT("[Cluewren] -CluewrenMap=%s: %s"), *StartMap, *Error);
		return;
	}
	const FString Booted = LaunchMap;
	LaunchMap = Package;
	if (!Booted.Equals(Package, ESearchCase::IgnoreCase))
	{
		UGameplayStatics::OpenLevel(World, FName(*Package));
	}
}

bool UPlaytestSubsystem::Tick(float DeltaTime)
{
	const double T = Now();
	const double RealDelta = LastTickReal > 0 ? T - LastTickReal : 0.0;
	LastTickReal = T;

	TickConnection(T);
	NoteLaunchMap();  // (connected or not: the map launched with, before any travel)
	if (!Detectors.IsValid())
	{
		return true;  // (no brain has proved itself yet: see Prepare)
	}
	PlaytestOps::TickKeys(*this, DeltaTime);  // held keys and axes, every frame (all let go once disconnected)
	Media->Tick();  // (also once disconnected: a recording pauses until the session resumes)
#if WITH_EDITOR
	PlaytestExport::Tick();
#endif

	if (!bConnected)
	{
		LogCapture->bEnabled = false;
		LogCapture->Take();
		OutgoingEvents.Reset();
		return true;
	}

	UWorld* World = GetGameWorld();
	CheckSessionChange(World);
	PlaytestScreen::TickUnpainted(World, DeltaTime);

	for (int32 i = PendingOps.Num() - 1; i >= 0; --i)
	{
		if (PendingOps[i].Tick(PendingOps[i], T))
		{
			PendingOps.RemoveAt(i);
		}
	}

	const double GameNow = World ? World->GetTimeSeconds() : -1.0;
	LogCapture->bEnabled = World != nullptr;
	TArray<FPlaytestLogCapture::FLine> Lines = LogCapture->Take();
	if (Lines.Num())
	{
		int32 Sent = 0;
		for (const FPlaytestLogCapture::FLine& Line : Lines)
		{
			if (++Sent > MaxLogEventsPerFrame)
			{
				break;  // a flood: the detector below still counts every line
			}
			TSharedRef<FJsonObject> Event = MakeShared<FJsonObject>();
			Event->SetStringField(TEXT("kind"), TEXT("log"));
			Event->SetField(TEXT("t"), GameTimeJson(GameNow));
			Event->SetStringField(TEXT("verbosity"), Line.Verbosity);
			Event->SetStringField(TEXT("category"), Line.Category);
			Event->SetStringField(TEXT("message"), Line.Message.Left(2000));
			QueueEvent(Event);
		}
		Detectors->OnLogLines(Lines, GameNow);
	}

	if (World)
	{
		Detectors->Tick(World, RealDelta);
	}

	if (T - LastHeartbeat >= 1.0)
	{
		LastHeartbeat = T;
		TSharedRef<FJsonObject> Event = MakeShared<FJsonObject>();
		Event->SetStringField(TEXT("kind"), TEXT("heartbeat"));
		Event->SetField(TEXT("t"), GameTimeJson(GameNow));
		Event->SetNumberField(TEXT("fps"), FMath::RoundToDouble(DeltaTime > 0 ? 1.0 / DeltaTime : 0.0));
		Event->SetBoolField(TEXT("paused"), World && World->IsPaused());
		QueueEvent(Event);
	}

	FlushEvents(GameNow);
	return true;
}
