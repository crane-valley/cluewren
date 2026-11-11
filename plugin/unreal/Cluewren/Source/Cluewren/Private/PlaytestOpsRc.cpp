// Modified by Crane Valley: renamed for Cluewren; the settings class is compared as an FName; every receiver, argument
// and written value passes PlaytestRefusal (docs/adr/0014).
// `rc`: Remote Control compatible calls on the running game. Call a function, read or write
// a property, or describe an object, with the same bodies and results as Unreal's Remote Control HTTP API.

#include "PlaytestOps.h"

#include "PlaytestJson.h"
#include "PlaytestRefusal.h"
#include "UObject/StructOnScope.h"
#include "UObject/UnrealType.h"

namespace PlaytestOps
{
namespace
{
	using PlaytestRefusal::EOp;

	constexpr int32 Refusal = 403;

	using PlaytestRefusal::RefusedJson;

	UObject* Target(UPlaytestSubsystem& Bridge, int64 Id, const FString& Path, FName Member, EOp Op)
	{
		if (PlaytestRefusal::DataPath(Path))
		{
			Bridge.RespondError(Id, FString::Printf(TEXT("%s is refused: nothing under Saved/Cluewren is opened for rc"), *Path), Refusal);
			return nullptr;
		}
		UObject* Object = PlaytestJson::ResolveObject(Path);
		if (!Object)
		{
			Bridge.RespondError(Id, Missing(Path));
			return nullptr;
		}
		const FString Why = PlaytestRefusal::Refused(Object, Member, Op);
		if (!Why.IsEmpty())
		{
			Bridge.RespondError(Id, Why, Refusal);
			return nullptr;
		}
		return Object;
	}

	void Call(UPlaytestSubsystem& Bridge, int64 Id, const TSharedPtr<FJsonObject>& Body)
	{
		const FString Path = StringField(Body, TEXT("objectPath"));
		const FString Name = StringField(Body, TEXT("functionName"));
		UObject* Object = Target(Bridge, Id, Path, FName(*Name), EOp::Call);
		if (!Object)
		{
			return;
		}
		UFunction* Function = Object->FindFunction(FName(*Name));
		if (!Function)
		{
			Bridge.RespondError(Id, FString::Printf(TEXT("Function: %s does not exist on object: %s"), *Name, *Path));
			return;
		}
		const TSharedPtr<FJsonObject> Params = ObjectField(Body, TEXT("parameters"));
		const FString Paths = Params.IsValid() ? RefusedJson(MakeShared<FJsonValueObject>(Params)) : FString();
		if (!Paths.IsEmpty())
		{
			Bridge.RespondError(Id, Paths, Refusal);
			return;
		}
		FStructOnScope Scope(Function);
		uint8* Parms = Scope.GetStructMemory();
#if WITH_METADATA
		const FString WorldContext = Function->GetMetaData(TEXT("WorldContext"));
#else
		const FString WorldContext;
#endif
		for (TFieldIterator<FProperty> It(Function); It && It->HasAnyPropertyFlags(CPF_Parm); ++It)
		{
			FProperty* Param = *It;
			if (Param->HasAnyPropertyFlags(CPF_ReturnParm))
			{
				continue;
			}
			void* ValuePtr = Param->ContainerPtrToValuePtr<void>(Parms);
			if (TSharedPtr<FJsonValue> Value = FindField(Params, Param->GetName()))
			{
				FString Error;
				if (!PlaytestJson::JsonToProperty(Param, ValuePtr, Value, Error))
				{
					Bridge.RespondError(Id, FString::Printf(TEXT("%s on %s: %s"), *Name, *Path, *Error));
					return;
				}
				continue;
			}
			if (!WorldContext.IsEmpty() && Param->GetName() == WorldContext)
			{
				if (FObjectPropertyBase* ObjParam = CastField<FObjectPropertyBase>(Param))
				{
					ObjParam->SetObjectPropertyValue(ValuePtr, Bridge.GetGameWorld());
				}
				continue;
			}
#if WITH_METADATA
			const FString Default = Function->GetMetaData(*FString::Printf(TEXT("CPP_Default_%s"), *Param->GetName()));
			if (!Default.IsEmpty())
			{
				Param->ImportText_Direct(*Default, ValuePtr, nullptr, PPF_None, GLog);
			}
#endif
		}
		// After conversion: a struct's text and a default argument are imported, and can name objects themselves.
		for (TFieldIterator<FProperty> It(Function); It && It->HasAnyPropertyFlags(CPF_Parm); ++It)
		{
			if (It->HasAnyPropertyFlags(CPF_ReturnParm))
			{
				continue;
			}
			const FString Why = PlaytestRefusal::RefusedValue(*It, It->ContainerPtrToValuePtr<void>(Parms));
			if (!Why.IsEmpty())
			{
				Bridge.RespondError(Id, FString::Printf(TEXT("%s on %s: %s"), *Name, *Path, *Why), Refusal);
				return;
			}
		}
		Object->ProcessEvent(Function, Parms);
		TSharedRef<FJsonObject> Result = MakeShared<FJsonObject>();
		for (TFieldIterator<FProperty> It(Function); It && It->HasAnyPropertyFlags(CPF_Parm); ++It)
		{
			FProperty* Param = *It;
			const bool bOut = Param->HasAnyPropertyFlags(CPF_ReturnParm)
				|| (Param->HasAnyPropertyFlags(CPF_OutParm) && !Param->HasAnyPropertyFlags(CPF_ConstParm));
			if (!bOut)
			{
				continue;
			}
			TSharedPtr<FJsonValue> Value = PlaytestJson::PropertyInContainerToJson(Param, Parms);
			if (Value.IsValid())
			{
				Result->SetField(Param->GetName(), Value);
			}
		}
		Bridge.Respond(Id, Obj(Result));
	}

	// Into a copy first: a refused value never reaches the object.
	bool Write(UPlaytestSubsystem& Bridge, int64 Id, UObject* Object, FProperty* Prop, const TSharedPtr<FJsonValue>& Value)
	{
		void* Dest = Prop->ContainerPtrToValuePtr<void>(Object);
		void* Copy = FMemory::Malloc(Prop->GetSize(), Prop->GetMinAlignment());
		Prop->InitializeValue(Copy);
		Prop->CopyCompleteValue(Copy, Dest);
		FString Error;
		bool bWritten = false;
		if (!PlaytestJson::JsonToProperty(Prop, Copy, Value, Error))
		{
			Bridge.RespondError(Id, Error);
		}
		else if (const FString Why = PlaytestRefusal::RefusedValue(Prop, Copy); !Why.IsEmpty())
		{
			Bridge.RespondError(Id, Why, Refusal);
		}
		else
		{
			Prop->CopyCompleteValue(Dest, Copy);
			bWritten = true;
		}
		Prop->DestroyValue(Copy);
		FMemory::Free(Copy);
		return bWritten;
	}

	void Property(UPlaytestSubsystem& Bridge, int64 Id, const TSharedPtr<FJsonObject>& Body)
	{
		const FString Path = StringField(Body, TEXT("objectPath"));
		const FString Name = StringField(Body, TEXT("propertyName"));
		const TSharedPtr<FJsonObject> NewValue = ObjectField(Body, TEXT("propertyValue"));
		const bool bWrite = NewValue.IsValid() || StringField(Body, TEXT("access")).StartsWith(TEXT("WRITE"));
		UObject* Object = Target(Bridge, Id, Path, FName(*Name), bWrite ? EOp::Write : EOp::Read);
		if (!Object)
		{
			return;
		}
		if (Name.IsEmpty())
		{
			if (bWrite)
			{
				Bridge.RespondError(Id, TEXT("A write needs propertyName"));
				return;
			}
			Bridge.Respond(Id, Obj(PlaytestJson::StructToJson(Object->GetClass(), Object)));
			return;
		}
		FProperty* Prop = PlaytestJson::FindProperty(Object->GetClass(), Name);
		if (!Prop || (!bWrite && PlaytestJson::IsHidden(Prop)))
		{
			Bridge.RespondError(Id, FString::Printf(TEXT("Property %s does not exist on object %s"), *Name, *Path));
			return;
		}
		if (bWrite)
		{
			TSharedPtr<FJsonValue> Value = FindField(NewValue, Name);
			if (!Value.IsValid() && NewValue.IsValid() && NewValue->Values.Num() == 1)
			{
				Value = NewValue->Values.CreateConstIterator().Value();
			}
			if (!Value.IsValid())
			{
				Bridge.RespondError(Id, FString::Printf(TEXT("No value given for %s"), *Name));
				return;
			}
			if (Prop->ArrayDim != 1)
			{
				Bridge.RespondError(Id, FString::Printf(TEXT("%s is a fixed-size array and can't be written"), *Name));
				return;
			}
			const FString Paths = RefusedJson(Value);
			if (!Paths.IsEmpty())
			{
				Bridge.RespondError(Id, Paths, Refusal);
				return;
			}
			if (Write(Bridge, Id, Object, Prop, Value))
			{
				Bridge.Respond(Id, Obj(MakeShared<FJsonObject>()));
			}
			return;
		}
		TSharedRef<FJsonObject> Result = MakeShared<FJsonObject>();
		TSharedPtr<FJsonValue> Value = PlaytestJson::PropertyInContainerToJson(Prop, Object);
		Result->SetField(Name, Value.IsValid() ? Value : MakeShared<FJsonValueNull>());
		Bridge.Respond(Id, Obj(Result));
	}

	FString Tooltip(const FField* Field)
	{
#if WITH_METADATA
		return Field->GetMetaData(TEXT("ToolTip"));
#else
		return FString();
#endif
	}

	void Describe(UPlaytestSubsystem& Bridge, int64 Id, const TSharedPtr<FJsonObject>& Body)
	{
		const FString Path = StringField(Body, TEXT("objectPath"));
		UObject* Object = Target(Bridge, Id, Path, NAME_None, EOp::Read);
		if (!Object)
		{
			return;
		}
		UClass* Class = Object->GetClass();
		TSharedRef<FJsonObject> Result = MakeShared<FJsonObject>();
		Result->SetStringField(TEXT("Name"), Object->GetName());
		Result->SetStringField(TEXT("Class"), Class->GetPathName());
		TArray<TSharedPtr<FJsonValue>> Properties;
		for (TFieldIterator<FProperty> It(Class); It; ++It)
		{
			if (PlaytestJson::IsHidden(*It))
			{
				continue;
			}
			TSharedRef<FJsonObject> P = MakeShared<FJsonObject>();
			P->SetStringField(TEXT("Name"), It->GetAuthoredName());
			P->SetStringField(TEXT("Description"), Tooltip(*It));
			P->SetStringField(TEXT("Type"), It->GetCPPType());
			Properties.Add(Obj(P));
		}
		Result->SetArrayField(TEXT("Properties"), Properties);
		TArray<TSharedPtr<FJsonValue>> Functions;
		for (TFieldIterator<UFunction> It(Class, EFieldIteratorFlags::IncludeSuper); It; ++It)
		{
			UFunction* Function = *It;
			if (!Function->HasAnyFunctionFlags(FUNC_BlueprintCallable | FUNC_BlueprintPure)
				|| !PlaytestRefusal::Refused(Object, Function->GetFName(), EOp::Call).IsEmpty())
			{
				continue;
			}
			TSharedRef<FJsonObject> F = MakeShared<FJsonObject>();
			F->SetStringField(TEXT("Name"), Function->GetName());
#if WITH_METADATA
			F->SetStringField(TEXT("Description"), Function->GetMetaData(TEXT("ToolTip")));
#else
			F->SetStringField(TEXT("Description"), FString());
#endif
			TArray<TSharedPtr<FJsonValue>> Arguments;
			for (TFieldIterator<FProperty> P(Function); P && P->HasAnyPropertyFlags(CPF_Parm); ++P)
			{
				const bool bInput = !P->HasAnyPropertyFlags(CPF_ReturnParm)
					&& (!P->HasAnyPropertyFlags(CPF_OutParm) || P->HasAnyPropertyFlags(CPF_ReferenceParm | CPF_ConstParm));
				if (!bInput)
				{
					continue;
				}
				TSharedRef<FJsonObject> A = MakeShared<FJsonObject>();
				A->SetStringField(TEXT("Name"), P->GetName());
				A->SetStringField(TEXT("Type"), P->GetCPPType());
				Arguments.Add(Obj(A));
			}
			F->SetArrayField(TEXT("Arguments"), Arguments);
			Functions.Add(Obj(F));
		}
		Result->SetArrayField(TEXT("Functions"), Functions);
		Bridge.Respond(Id, Obj(Result));
	}
}

	void Rc(UPlaytestSubsystem& Bridge, int64 Id, const TSharedPtr<FJsonObject>& Args)
	{
		const FString Verb = StringField(Args, TEXT("verb"));
		const TSharedPtr<FJsonObject> Body = ObjectField(Args, TEXT("body"));
		if (!Body.IsValid())
		{
			Bridge.RespondError(Id, TEXT("rc needs a body"));
		}
		else if (Verb == TEXT("call"))
		{
			Call(Bridge, Id, Body);
		}
		else if (Verb == TEXT("property"))
		{
			Property(Bridge, Id, Body);
		}
		else if (Verb == TEXT("describe"))
		{
			Describe(Bridge, Id, Body);
		}
		else
		{
			Bridge.RespondError(Id, FString::Printf(TEXT("unknown rc verb %s"), *Verb));
		}
	}
}
