// Modified by Crane Valley: the brain is found through brain.json and must prove itself; the account, sign-in, Aura,
// agent service, run request, usage, memory, inspection, steering and settings plumbing is removed.
#pragma once

#include "CoreMinimal.h"
#include "Containers/Ticker.h"
#include "Dom/JsonObject.h"
#include "Subsystems/EngineSubsystem.h"
#include "PlaytestSubsystem.generated.h"

class UWorld;
class FPlaytestDetectors;
class FPlaytestLogCapture;
class FPlaytestMedia;
class FPlaytestSocket;

/** Starts, stops or restarts the play session (PIE in the editor), in the level Map when given (a package name or a
 *  level's name: PlaytestLevels::Resolve; empty: the level open in the editor). Calls Done(running) once finished. */
using FPlaytestSessionHandler = TFunction<void(const FString& /*Action*/, const FString& /*Map*/, TFunction<void(bool /*bRunning*/, const FString& /*Error*/)> /*Done*/)>;

/**
 * The bridge between the game and the brain: finds it through brain.json, connects out over a WebSocket, proves itself,
 * answers the brain's requests on the game thread, and streams events (engine errors, in-engine detector findings,
 * heartbeats). In the editor it connects by itself once brain.json appears; a game only with -PlaytestConnect. Never built
 * into Shipping.
 */
UCLASS()
class CLUEWREN_API UPlaytestSubsystem : public UEngineSubsystem
{
	GENERATED_BODY()

public:
	static constexpr int32 ProtocolVersion = 1;

	static UPlaytestSubsystem* Get();

	virtual void Initialize(FSubsystemCollectionBase& Collection) override;
	virtual void Deinitialize() override;

	/** The brain has proved itself on this connection. */
	bool IsConnected() const { return bConnected; }

	/** The world being played: the one with the player in it, the PIE world in the editor (the first client's when the
	 *  editor plays as clients of a dedicated server), the game world otherwise. Never a dedicated server's world: it
	 *  has no local player, no viewport, and its player controllers only stand in for remote players. */
	UWorld* GetGameWorld() const;

	/** The world with authority over the game being played: GetGameWorld's own unless that is a client, then the
	 *  server's when it runs in this process (the editor's dedicated server, or a listen server); nullptr when the server
	 *  is in another process. */
	UWorld* GetServerWorld() const;

	/** Why no game world is being played although the editor is in Play In Editor (the client hasn't joined its
	 *  dedicated server yet, or never will), "" otherwise. */
	FString GetPlayProblem() const;

	/** Set by the editor module: starts/stops PIE. Without it (a game build), restart reopens the launch map. */
	FPlaytestSessionHandler SessionHandler;

	/** Set by the editor module: whether a play session has been asked for and hasn't started yet. */
	TFunction<bool()> SessionRequested;

	/** The map the game was launched with: the long package name of the first game world seen begin play (no PIE prefix), ""
	 *  before one. A game build's restart reopens it, not the map the game has travelled to since (a match that ended
	 *  moves the server to the next map of its rotation). */
	const FString& GetLaunchMap() const { return LaunchMap; }

	/** Queue an event for the brain (sent once per tick, batched). */
	void QueueEvent(const TSharedRef<FJsonObject>& Event);

	/** Game time of the played world, or a negative number when there is none. */
	double GameTime() const;

	// Used by the request handlers
	struct FPending
	{
		int64 Id = 0;
		FString Op;
		double StartReal = 0;
		double Seconds = 0;
		TSharedPtr<FJsonObject> Args;
		TSharedPtr<FJsonObject> State;  // op-specific
		TFunction<bool(FPending&, double /*Now*/)> Tick;  // returns true when it has responded
	};
	void Respond(int64 Id, const TSharedPtr<FJsonValue>& Result);
	void RespondError(int64 Id, const FString& Error, int32 Status = 400);
	void AddPending(FPending&& Pending);
	/** Send a recording's frame, already written as JSON text; dropped while disconnected. */
	void SendFrame(TArray<uint8>&& Text);
	/** Send a response already written as JSON text; held while disconnected, as Respond's are. */
	void SendResponseText(TArray<uint8>&& Text);
	/** Bumped when the brain starts a session anew, not when it resumes one: what was asked in one is never answered in the next. */
	int32 GetSessionSerial() const { return SessionSerial; }
	/** `record` and `capture`. */
	FPlaytestMedia& GetMedia() { return *Media; }

	/** Settings for the in-engine detectors, from the `detectors` op. */
	FPlaytestDetectors& GetDetectors() { return *Detectors; }

private:
	struct FHeld
	{
		TSharedPtr<FJsonObject> Json;
		TArray<uint8> Bytes;
	};

	bool Tick(float DeltaTime);
	void TickConnection(double Now);
	void TryConnect(double Now);
	void ConnectionLost(double Now, const FString& Why);
	void HandleMessage(const TSharedPtr<FJsonObject>& Message);
	void HandleWelcome(const TSharedPtr<FJsonObject>& Message);
	void HandleRequest(const TSharedPtr<FJsonObject>& Message);
	void Refuse(const FString& Why);
	void SendHello();
	void Send(const TSharedRef<FJsonObject>& Message);
	void SendOrHold(const TSharedRef<FJsonObject>& Message);
	void FlushEvents(double GameNow);
	void Status(const FString& Text);
	void CheckSessionChange(UWorld* World);
	void NoteLaunchMap();

	TSharedPtr<FPlaytestSocket> Socket;
	FTSTicker::FDelegateHandle TickHandle;

	bool bOnCommandLine = false;  // -PlaytestConnect
	bool bConnected = false;
	bool bHelloSent = false;
	double NextAttempt = 0;
	double HandshakeDeadline = 0;
	double ConnectDeadline = 0;  // -PlaytestConnectTimeout: a game with no welcome by then exits with 1
	FString LastStatus;
	FDateTime RefusedFile;  // the brain.json that failed its proof: nothing connects until it is replaced

	TArray<uint8> Secret;
	TArray<uint8> PluginNonce;
	FString SessionId;     // kept across a dropped connection: the next hello asks to resume it
	FString AskedResume;   // the session the last hello asked to resume
	int64 LastRequest = 0;  // the newest request handled: on a resume, the brain sends again what came after it
	int32 SessionSerial = 0;
	TArray<FHeld> HeldResponses;

	double LastHeartbeat = 0;
	double LastTickReal = 0;

	TArray<TSharedPtr<FJsonObject>> Received;  // reused each tick
	TArray<TSharedPtr<FJsonValue>> OutgoingEvents;
	TArray<FPending> PendingOps;

	TWeakObjectPtr<UWorld> LastWorld;
	FString LaunchMap;  // see GetLaunchMap

	TSharedPtr<FPlaytestDetectors> Detectors;
	TSharedPtr<FPlaytestLogCapture> LogCapture;
	TSharedPtr<FPlaytestMedia> Media;
};
