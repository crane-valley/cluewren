#include "PlaytestSocket.h"

#include "HAL/Event.h"
#include "HAL/PlatformProcess.h"
#include "PlaytestJson.h"

#include "Windows/AllowWindowsPlatformTypes.h"
#include <winhttp.h>
#include "Windows/HideWindowsPlatformTypes.h"

namespace
{
	constexpr int32 ConnectMilliseconds = 2000;
	constexpr int32 ReceiveChunk = 64 * 1024;
	// The brain's own limit on a frame (BridgeLink.MaxFrameBytes).
	constexpr int32 MaxMessageBytes = 16 * 1024 * 1024;
	constexpr double CloseWaitSeconds = 0.3;
}

FPlaytestSocket::FPlaytestSocket(const FString& Host, int32 Port, const FString& Path)
{
	Wake = FPlatformProcess::GetSynchEventFromPool(false);
	ReaderDone = FPlatformProcess::GetSynchEventFromPool(true);
	ReaderBody.Body = [this, Host, Port, Path]() { ReadLoop(Host, Port, Path); };
	WriterBody.Body = [this]() { WriteLoop(); };
	Reader.Reset(FRunnableThread::Create(&ReaderBody, TEXT("PlaytestAgentReader"), 128 * 1024));
	Writer.Reset(FRunnableThread::Create(&WriterBody, TEXT("PlaytestAgentWriter"), 128 * 1024));
}

FPlaytestSocket::~FPlaytestSocket()
{
	bStopping = true;
	Wake->Trigger();
	Writer->WaitForCompletion();
	// The reader may be waiting on a peer that never answers the close.
	ReaderDone->Wait(FTimespan::FromSeconds(CloseWaitSeconds));
	CloseHandles();
	Reader->WaitForCompletion();
	Reader.Reset();
	Writer.Reset();
	FPlatformProcess::ReturnSynchEventToPool(Wake);
	FPlatformProcess::ReturnSynchEventToPool(ReaderDone);
}

FString FPlaytestSocket::GetCloseReason() const
{
	FScopeLock Locked(&Lock);
	return CloseReason;
}

void FPlaytestSocket::Send(const TSharedRef<FJsonObject>& Message)
{
	{
		FScopeLock Locked(&Lock);
		Outbox.Add({Message, {}});
	}
	Wake->Trigger();
}

void FPlaytestSocket::SendBytes(TArray<uint8>&& Text)
{
	{
		FScopeLock Locked(&Lock);
		Outbox.Add({nullptr, MoveTemp(Text)});
	}
	Wake->Trigger();
}

void FPlaytestSocket::CloseCleanly()
{
	bCleanClose = true;
	Wake->Trigger();
}

void FPlaytestSocket::Take(TArray<TSharedPtr<FJsonObject>>& Out)
{
	FScopeLock Locked(&Lock);
	Swap(Out, Inbox);
}

void FPlaytestSocket::Fail(const FString& Why)
{
	{
		FScopeLock Locked(&Lock);
		if (CloseReason.IsEmpty())
		{
			CloseReason = Why;
		}
	}
	State = EState::Closed;
	Wake->Trigger();
}

void FPlaytestSocket::CloseHandles()
{
	FScopeLock Locked(&Lock);
	for (void** Handle : {&Socket, &Request, &Connection, &Session})
	{
		if (*Handle)
		{
			WinHttpCloseHandle(*Handle);
			*Handle = nullptr;
		}
	}
}

bool FPlaytestSocket::Connect(const FString& Host, int32 Port, const FString& Path)
{
	// No proxy: a system-wide one must not carry a connection that only ever goes to this machine.
	HINTERNET S = WinHttpOpen(TEXT("PlaytestAgent"), WINHTTP_ACCESS_TYPE_NO_PROXY, WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0);
	HINTERNET C = S ? WinHttpConnect(S, *Host, (INTERNET_PORT)Port, 0) : nullptr;
	HINTERNET R = C ? WinHttpOpenRequest(C, TEXT("GET"), *Path, nullptr, WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES, 0) : nullptr;
	{
		FScopeLock Locked(&Lock);
		Session = S;
		Connection = C;
		Request = R;
	}
	if (!R || bStopping)
	{
		Fail(FString::Printf(TEXT("couldn't start a request (%u)"), GetLastError()));
		return false;
	}
	// Receive 0: a run can be quiet for as long as the brain likes.
	WinHttpSetTimeouts(R, ConnectMilliseconds, ConnectMilliseconds, 0, 0);
	if (!WinHttpSetOption(R, WINHTTP_OPTION_UPGRADE_TO_WEB_SOCKET, nullptr, 0)
		|| !WinHttpSendRequest(R, WINHTTP_NO_ADDITIONAL_HEADERS, 0, WINHTTP_NO_REQUEST_DATA, 0, 0, 0)
		|| !WinHttpReceiveResponse(R, nullptr))
	{
		Fail(FString::Printf(TEXT("couldn't reach it (%u)"), GetLastError()));
		return false;
	}
	DWORD Status = 0;
	DWORD Size = sizeof(Status);
	WinHttpQueryHeaders(R, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER, WINHTTP_HEADER_NAME_BY_INDEX, &Status, &Size, WINHTTP_NO_HEADER_INDEX);
	if (Status != HTTP_STATUS_SWITCH_PROTOCOLS)
	{
		Fail(FString::Printf(TEXT("it answered HTTP %u"), Status));
		return false;
	}
	HINTERNET W = WinHttpWebSocketCompleteUpgrade(R, 0);
	if (!W)
	{
		Fail(FString::Printf(TEXT("the upgrade failed (%u)"), GetLastError()));
		return false;
	}
	FScopeLock Locked(&Lock);
	Socket = W;
	return true;
}

void FPlaytestSocket::ReadLoop(const FString& Host, int32 Port, const FString& Path)
{
	if (Connect(Host, Port, Path))
	{
		State = EState::Open;
		Wake->Trigger();
		TArray<uint8> Message;
		Message.SetNumUninitialized(ReceiveChunk);
		int32 Used = 0;
		FString Why;
		while (Why.IsEmpty())
		{
			HINTERNET W;
			{
				FScopeLock Locked(&Lock);
				W = Socket;
			}
			if (!W)
			{
				Why = TEXT("closed");
				break;
			}
			if (Message.Num() - Used < ReceiveChunk)
			{
				Message.SetNumUninitialized(Message.Num() * 2);
			}
			DWORD Read = 0;
			WINHTTP_WEB_SOCKET_BUFFER_TYPE Type;
			const DWORD Error = WinHttpWebSocketReceive(W, Message.GetData() + Used, ReceiveChunk, &Read, &Type);
			if (Error != ERROR_SUCCESS)
			{
				Why = FString::Printf(TEXT("the connection dropped (%u)"), Error);
			}
			else if (Type == WINHTTP_WEB_SOCKET_CLOSE_BUFFER_TYPE)
			{
				USHORT Code = 0;
				BYTE Reason[123];
				DWORD ReasonBytes = 0;
				WinHttpWebSocketQueryCloseStatus(W, &Code, Reason, sizeof(Reason), &ReasonBytes);
				Why = FString::Printf(TEXT("%u %s"), Code, *FString(FUTF8ToTCHAR(reinterpret_cast<const ANSICHAR*>(Reason), ReasonBytes)));
			}
			else
			{
				Used += Read;
				if (Used > MaxMessageBytes)
				{
					Why = TEXT("a frame over the limit");
				}
				else if (Type == WINHTTP_WEB_SOCKET_UTF8_MESSAGE_BUFFER_TYPE)
				{
					TSharedPtr<FJsonObject> Parsed = PlaytestJson::Parse(FString(FUTF8ToTCHAR(reinterpret_cast<const ANSICHAR*>(Message.GetData()), Used)));
					Used = 0;
					FScopeLock Locked(&Lock);
					Inbox.Add(MoveTemp(Parsed));
				}
				else if (Type == WINHTTP_WEB_SOCKET_BINARY_MESSAGE_BUFFER_TYPE)
				{
					Used = 0;
				}
			}
		}
		Fail(Why);
	}
	ReaderDone->Trigger();
}

void FPlaytestSocket::WriteLoop()
{
	TArray<FOutgoing> Sending;
	while (true)
	{
		Wake->Wait();
		const EState Now = State;
		if (Now == EState::Connecting && !bStopping)
		{
			continue;
		}
		HINTERNET W;
		{
			FScopeLock Locked(&Lock);
			Sending = MoveTemp(Outbox);
			Outbox.Reset();
			W = Socket;
		}
		if (W && Now == EState::Open)
		{
			for (FOutgoing& Item : Sending)
			{
				DWORD Error;
				if (Item.Json.IsValid())
				{
					const FTCHARToUTF8 Text(*PlaytestJson::Write(Item.Json.ToSharedRef()));
					Error = WinHttpWebSocketSend(W, WINHTTP_WEB_SOCKET_UTF8_MESSAGE_BUFFER_TYPE, const_cast<ANSICHAR*>(Text.Get()), Text.Length());
				}
				else
				{
					Error = WinHttpWebSocketSend(W, WINHTTP_WEB_SOCKET_UTF8_MESSAGE_BUFFER_TYPE, Item.Bytes.GetData(), Item.Bytes.Num());
				}
				if (Error != ERROR_SUCCESS)
				{
					break;  // (the reader sees the connection go and says why)
				}
			}
			if (bCleanClose)
			{
				WinHttpWebSocketShutdown(W, WINHTTP_WEB_SOCKET_SUCCESS_CLOSE_STATUS, nullptr, 0);
				bCleanClose = false;
			}
		}
		Sending.Reset();
		if (bStopping || State == EState::Closed)
		{
			return;
		}
	}
}
