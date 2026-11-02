// `find`, `transforms`, `pose`, `pointer` and `movement`, which the Kit asks for as ops: the engine's own reads, so a read
// in a control loop costs no reflection. The frame is the Kit's, Unreal's own.

#include "PlaytestOps.h"

#include "Camera/PlayerCameraManager.h"
#include "Components/ActorComponent.h"
#include "Components/CapsuleComponent.h"
#include "Engine/GameViewportClient.h"
#include "EngineUtils.h"
#include "Framework/Application/SlateApplication.h"
#include "GameFramework/Character.h"
#include "GameFramework/CharacterMovementComponent.h"
#include "GameFramework/Pawn.h"
#include "GameFramework/PawnMovementComponent.h"
#include "GameFramework/PlayerController.h"
#include "InputCoreTypes.h"
#include "PlaytestJson.h"
#include "Slate/SceneViewport.h"
#include "UObject/UObjectGlobals.h"

namespace PlaytestOps
{
namespace
{
	double Rounded(double Value, double Scale)
	{
		return FMath::RoundToDouble(Value * Scale) / Scale;
	}

	TSharedRef<FJsonObject> Point(const FVector& V)
	{
		TSharedRef<FJsonObject> O = MakeShared<FJsonObject>();
		O->SetField(TEXT("X"), PlaytestJson::Number(Rounded(V.X, 10.0)));
		O->SetField(TEXT("Y"), PlaytestJson::Number(Rounded(V.Y, 10.0)));
		O->SetField(TEXT("Z"), PlaytestJson::Number(Rounded(V.Z, 10.0)));
		return O;
	}

	TSharedRef<FJsonObject> Turned(const FRotator& R)
	{
		TSharedRef<FJsonObject> O = MakeShared<FJsonObject>();
		O->SetField(TEXT("Pitch"), PlaytestJson::Number(Rounded(FRotator::NormalizeAxis(R.Pitch), 100.0)));
		O->SetField(TEXT("Yaw"), PlaytestJson::Number(Rounded(FRotator::NormalizeAxis(R.Yaw), 100.0)));
		O->SetField(TEXT("Roll"), PlaytestJson::Number(Rounded(FRotator::NormalizeAxis(R.Roll), 100.0)));
		return O;
	}

	UClass* LookUp(const FString& Name)
	{
		if (Name.StartsWith(TEXT("/")))
		{
			if (UClass* Class = Cast<UClass>(PlaytestJson::ResolveObject(Name)))
			{
				return Class;
			}
			return Cast<UClass>(PlaytestJson::ResolveObject(Name + TEXT("_C")));
		}
		for (const FString& Candidate : {Name, Name + TEXT("_C"), Name.Mid(1)})
		{
			if (UClass* Class = FindFirstObject<UClass>(*Candidate, EFindFirstObjectOptions::NativeFirst, ELogVerbosity::NoLogging))
			{
				if (Class->IsChildOf(AActor::StaticClass()))
				{
					return Class;
				}
			}
		}
		return nullptr;
	}

	// A control loop asks for the same classes many times a second, and a global name search for each is the cost here.
	UClass* ClassNamed(const FString& Name)
	{
		static TMap<FString, TWeakObjectPtr<UClass>> Known;
		if (const TWeakObjectPtr<UClass>* Found = Known.Find(Name))
		{
			if (UClass* Class = Found->Get())
			{
				return Class;
			}
		}
		UClass* Class = LookUp(Name);
		if (Class)
		{
			Known.Add(Name, Class);
		}
		return Class;
	}

	bool IsThing(const AActor* Actor)
	{
		return Actor->GetRootComponent() != nullptr;
	}

	bool bFauxCursor = false;
}

	void ReleasePointer()
	{
		if (bFauxCursor && FSlateApplication::IsInitialized())
		{
			FSlateApplication::Get().UsePlatformCursorForCursorUser(true);
		}
		bFauxCursor = false;
	}

	void Find(UPlaytestSubsystem& Bridge, int64 Id, const TSharedPtr<FJsonObject>& Args)
	{
		UWorld* World = Bridge.GetGameWorld();
		if (!World)
		{
			Bridge.RespondError(Id, TEXT("No game is running"));
			return;
		}
		const FString ClassName = StringField(Args, TEXT("class"));
		const FString Name = StringField(Args, TEXT("name"));
		const int32 Limit = (int32)NumberField(Args, TEXT("limit"), 10000);
		bool bThings = false;
		Args->TryGetBoolField(TEXT("things"), bThings);
		TArray<TSharedPtr<FJsonValue>> Objects;
		// (a class no actor is loaded of yet isn't found by name: then nothing in the world is of it)
		UClass* Class = ClassName.IsEmpty() ? AActor::StaticClass() : ClassNamed(ClassName);
		if (Class)
		{
			for (TActorIterator<AActor> It(World, Class); It && Objects.Num() < Limit; ++It)
			{
				AActor* Actor = *It;
				if (!IsValid(Actor) || Actor->IsActorBeingDestroyed() || (bThings && !IsThing(Actor))
					|| (!Name.IsEmpty() && !Actor->GetName().Contains(Name)))
				{
					continue;
				}
				Objects.Add(MakeShared<FJsonValueString>(Actor->GetPathName()));
			}
		}
		TSharedRef<FJsonObject> Result = MakeShared<FJsonObject>();
		Result->SetArrayField(TEXT("objects"), Objects);
		Bridge.Respond(Id, Obj(Result));
	}

	void Transforms(UPlaytestSubsystem& Bridge, int64 Id, const TSharedPtr<FJsonObject>& Args)
	{
		TSharedRef<FJsonObject> Found = MakeShared<FJsonObject>();
		const TArray<TSharedPtr<FJsonValue>>* Objects = nullptr;
		if (Args->TryGetArrayField(TEXT("objects"), Objects) && Objects)
		{
			for (const TSharedPtr<FJsonValue>& Value : *Objects)
			{
				const FString Path = Value->AsString();
				UObject* Object = PlaytestJson::ResolveObject(Path);
				const UActorComponent* Component = Cast<UActorComponent>(Object);
				const AActor* Actor = Component ? Component->GetOwner() : Cast<AActor>(Object);
				if (!IsValid(Actor) || Actor->IsActorBeingDestroyed())
				{
					Found->SetField(Path, MakeShared<FJsonValueNull>());
					continue;
				}
				FVector Center, Extent;
				Actor->GetActorBounds(false, Center, Extent);
				TSharedRef<FJsonObject> One = MakeShared<FJsonObject>();
				One->SetObjectField(TEXT("location"), Point(Actor->GetActorLocation()));
				One->SetObjectField(TEXT("rotation"), Turned(Actor->GetActorRotation()));
				One->SetObjectField(TEXT("velocity"), Point(Actor->GetVelocity()));
				One->SetObjectField(TEXT("center"), Point(Center));
				One->SetObjectField(TEXT("extent"), Point(Extent));
				One->SetBoolField(TEXT("active"), !Actor->IsHidden());
				Found->SetObjectField(Path, One);
			}
		}
		TSharedRef<FJsonObject> Result = MakeShared<FJsonObject>();
		Result->SetObjectField(TEXT("objects"), Found);
		Bridge.Respond(Id, Obj(Result));
	}

	void Pose(UPlaytestSubsystem& Bridge, int64 Id)
	{
		APlayerController* PC = PlayerController(Bridge.GetGameWorld());
		const APawn* Pawn = PC ? PC->GetPawn() : nullptr;
		if (!Pawn)
		{
			Bridge.RespondError(Id, TEXT("No player pawn"));
			return;
		}
		const FVector At = Pawn->GetActorLocation();
		const APlayerCameraManager* Camera = PC->PlayerCameraManager;
		const FRotator View = PC->GetControlRotation();
		const ACharacter* Character = Cast<ACharacter>(Pawn);
		const UCharacterMovementComponent* Walking = Character ? Character->GetCharacterMovement() : nullptr;
		TSharedRef<FJsonObject> Result = MakeShared<FJsonObject>();
		Result->SetObjectField(TEXT("at"), Point(At));
		Result->SetObjectField(TEXT("eye"), Point(Camera ? Camera->GetCameraLocation() : At));
		Result->SetField(TEXT("yaw"), PlaytestJson::Number(Rounded(FRotator::NormalizeAxis(View.Yaw), 100.0)));
		Result->SetField(TEXT("pitch"), PlaytestJson::Number(Rounded(FRotator::NormalizeAxis(View.Pitch), 100.0)));
		Result->SetObjectField(TEXT("velocity"), Point(Pawn->GetVelocity()));
		if (Walking)
		{
			Result->SetBoolField(TEXT("on_ground"), Walking->IsMovingOnGround());
		}
		else
		{
			Result->SetField(TEXT("on_ground"), MakeShared<FJsonValueNull>());
		}
		if (const UCapsuleComponent* Capsule = Character ? Character->GetCapsuleComponent() : nullptr)
		{
			Result->SetObjectField(TEXT("feet"), Point(At - FVector(0.0, 0.0, Capsule->GetScaledCapsuleHalfHeight())));
		}
		Result->SetStringField(TEXT("player"), PathOf(Pawn));
		Result->SetStringField(TEXT("camera"), PathOf(Camera));
		Bridge.Respond(Id, Obj(Result));
	}

	// Slate's faux cursor stands in for the platform's: the game reads this position however it asks (the viewport or
	// Slate, as GetMousePositionOnViewport does), and the user's own cursor stays where it is. A click then reaches the
	// game (`key`), not the Slate widgets under the spot: those are `ui_act`'s.
	void Pointer(UPlaytestSubsystem& Bridge, int64 Id, const TSharedPtr<FJsonObject>& Args)
	{
		UWorld* World = Bridge.GetGameWorld();
		APlayerController* PC = PlayerController(World);
		UGameViewportClient* Client = World ? World->GetGameViewport() : nullptr;
		FSceneViewport* Viewport = Client ? Client->GetGameViewport() : nullptr;
		if (!PC || !Viewport)
		{
			Bridge.RespondError(Id, TEXT("No game viewport"));
			return;
		}
		const FIntPoint Size = Viewport->GetSizeXY();
		if (Size.X <= 0 || Size.Y <= 0)
		{
			Bridge.RespondError(Id, TEXT("The game's viewport has no size"), 409);
			return;
		}
		const double W = Size.X;
		const double H = Size.Y;
		FVector2D Pixel;
		const TSharedPtr<FJsonObject> WorldPoint = ObjectField(Args, TEXT("world"));
		const FString ObjectPath = StringField(Args, TEXT("object"));
		double ScreenX = 0.0;
		double ScreenY = 0.0;
		if (WorldPoint.IsValid() || !ObjectPath.IsEmpty())
		{
			FVector At = VectorField(Args, TEXT("world"));
			if (!WorldPoint.IsValid())
			{
				UObject* Object = PlaytestJson::ResolveObject(ObjectPath);
				const UActorComponent* Component = Cast<UActorComponent>(Object);
				const AActor* Actor = Component ? Component->GetOwner() : Cast<AActor>(Object);
				if (!IsValid(Actor))
				{
					Bridge.RespondError(Id, Missing(ObjectPath));
					return;
				}
				FVector Extent;
				Actor->GetActorBounds(false, At, Extent);
			}
			if (!PC->ProjectWorldLocationToScreen(At, Pixel, false))
			{
				Bridge.RespondError(Id, TEXT("That is behind the camera"));
				return;
			}
		}
		else if (Args->TryGetNumberField(TEXT("screen_x"), ScreenX) | Args->TryGetNumberField(TEXT("screen_y"), ScreenY))
		{
			Pixel = FVector2D((ScreenX + 1.0) / 2.0 * W, (1.0 - ScreenY) / 2.0 * H);
		}
		else
		{
			Bridge.RespondError(Id, TEXT("pointer needs screen_x and screen_y, world or object"));
			return;
		}
		// (without a renderer nothing ever arranged the viewport: its size is all there is)
		const FGeometry& Arranged = Viewport->GetCachedGeometry();
		const FGeometry Geometry = Arranged.GetLocalSize().X > 0.f ? Arranged : FGeometry::MakeRoot(FVector2f(Size), FSlateLayoutTransform());
		const FVector2D Local = Pixel * FVector2D(Geometry.GetLocalSize()) / FVector2D(W, H);
		const FVector2D Screen = FVector2D(Geometry.LocalToAbsolute(FVector2f(Local)));
		FIntPoint Before;
		Viewport->GetMousePos(Before);
		FSlateApplication& Slate = FSlateApplication::Get();
		Slate.UsePlatformCursorForCursorUser(false);
		bFauxCursor = true;
		Slate.SetCursorPos(Screen);
		const FPointerEvent Moved(0, Screen, FVector2D(Geometry.LocalToAbsolute(FVector2f(Before))), TSet<FKey>(), EKeys::Invalid, 0.f, FModifierKeysState());
		Viewport->OnMouseMove(Geometry, Moved);
		// A game can tell the mouse is in use only from its axes (Cropout's BP_PC does), and the viewport sends those
		// only while it has captured the mouse.
		const FVector2D Delta = Before.X >= 0 ? Pixel - FVector2D(Before) : FVector2D(1.0, 0.0);
		SendMouseMove(PC, Delta.X, -Delta.Y);
		TSharedRef<FJsonObject> Result = MakeShared<FJsonObject>();
		Result->SetField(TEXT("screen_x"), PlaytestJson::Number(Rounded(Pixel.X / W * 2.0 - 1.0, 1000.0)));
		Result->SetField(TEXT("screen_y"), PlaytestJson::Number(Rounded(1.0 - Pixel.Y / H * 2.0, 1000.0)));
		Result->SetField(TEXT("x"), PlaytestJson::Number(FMath::RoundToDouble(Pixel.X)));
		Result->SetField(TEXT("y"), PlaytestJson::Number(FMath::RoundToDouble(Pixel.Y)));
		Result->SetBoolField(TEXT("on_screen"), Pixel.X >= 0.0 && Pixel.X <= W && Pixel.Y >= 0.0 && Pixel.Y <= H);
		Bridge.Respond(Id, Obj(Result));
	}

	void Movement(UPlaytestSubsystem& Bridge, int64 Id)
	{
		APlayerController* PC = PlayerController(Bridge.GetGameWorld());
		const APawn* Pawn = PC ? PC->GetPawn() : nullptr;
		if (!Pawn)
		{
			Bridge.RespondError(Id, TEXT("No player pawn"));
			return;
		}
		TSharedRef<FJsonObject> Result = MakeShared<FJsonObject>();
		const ACharacter* Character = Cast<ACharacter>(Pawn);
		const UCharacterMovementComponent* Walking = Character ? Character->GetCharacterMovement() : nullptr;
		if (Walking)
		{
			Result->SetField(TEXT("walk_speed"), PlaytestJson::Number(Walking->MaxWalkSpeed));
			Result->SetField(TEXT("jump_z"), PlaytestJson::Number(Walking->JumpZVelocity));
			Result->SetField(TEXT("air_control"), PlaytestJson::Number(Walking->AirControl));
			Result->SetField(TEXT("step"), PlaytestJson::Number(Walking->MaxStepHeight));
			Result->SetField(TEXT("max_slope"), PlaytestJson::Number(Walking->GetWalkableFloorAngle()));
			Result->SetField(TEXT("gravity"), PlaytestJson::Number(FMath::Abs(Walking->GetGravityZ())));
		}
		else if (const UPawnMovementComponent* Moving = Pawn->GetMovementComponent())
		{
			Result->SetField(TEXT("walk_speed"), PlaytestJson::Number(Moving->GetMaxSpeed()));
		}
		if (const UCapsuleComponent* Capsule = Character ? Character->GetCapsuleComponent() : nullptr)
		{
			Result->SetField(TEXT("half_height"), PlaytestJson::Number(Capsule->GetScaledCapsuleHalfHeight()));
			Result->SetField(TEXT("radius"), PlaytestJson::Number(Capsule->GetScaledCapsuleRadius()));
		}
		Bridge.Respond(Id, Obj(Result));
	}
}
