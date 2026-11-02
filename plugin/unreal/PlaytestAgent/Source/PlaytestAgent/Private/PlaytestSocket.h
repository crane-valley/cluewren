// One WebSocket to the brain, over WinHTTP. The engine's WebSockets module can't be used: on Windows it is libwebsockets
// with Origin always set (LwsWebSocket.cpp), and the brain refuses any request with an Origin (ADR 0003).
//
// Two threads of its own, so a large frame costs the game thread nothing: the reader parses JSON as it arrives and the
// writer writes it out.

#pragma once

#include <atomic>

#include "CoreMinimal.h"
#include "Dom/JsonObject.h"
#include "HAL/Runnable.h"
#include "HAL/RunnableThread.h"

class FEvent;

class FPlaytestSocket
{
public:
	enum class EState : int32
	{
		Connecting,
		Open,
		Closed,
	};

	FPlaytestSocket(const FString& Host, int32 Port, const FString& Path);
	~FPlaytestSocket();

	EState GetState() const { return State; }
	/** Why it closed, once Closed. */
	FString GetCloseReason() const;

	/** Written to JSON on the writer thread. Any thread. */
	void Send(const TSharedRef<FJsonObject>& Message);
	/** Text already encoded. Any thread. */
	void SendBytes(TArray<uint8>&& Text);
	/** Sends what is queued, then closes with 1000: for the brain, the end of the session. */
	void CloseCleanly();

	/** What arrived since the last call, in order, swapped into Out, which must be empty; an invalid pointer is a message
	 *  that wasn't JSON. Game thread. */
	void Take(TArray<TSharedPtr<FJsonObject>>& Out);

private:
	struct FOutgoing
	{
		TSharedPtr<FJsonObject> Json;
		TArray<uint8> Bytes;
	};

	struct FThreadBody : FRunnable
	{
		TFunction<void()> Body;
		virtual uint32 Run() override
		{
			Body();
			return 0;
		}
	};

	void ReadLoop(const FString& Host, int32 Port, const FString& Path);
	bool Connect(const FString& Host, int32 Port, const FString& Path);
	void WriteLoop();
	void Fail(const FString& Why);
	void CloseHandles();

	FThreadBody ReaderBody;
	FThreadBody WriterBody;
	TUniquePtr<FRunnableThread> Reader;
	TUniquePtr<FRunnableThread> Writer;
	FEvent* Wake = nullptr;
	FEvent* ReaderDone = nullptr;

	std::atomic<EState> State{EState::Connecting};
	std::atomic<bool> bStopping{false};
	std::atomic<bool> bCleanClose{false};

	mutable FCriticalSection Lock;
	FString CloseReason;
	TArray<FOutgoing> Outbox;
	TArray<TSharedPtr<FJsonObject>> Inbox;
	// HINTERNETs: closing one is what wakes a thread blocked on it.
	void* Session = nullptr;
	void* Connection = nullptr;
	void* Request = nullptr;
	void* Socket = nullptr;
};
