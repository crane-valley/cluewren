// What `rc` refuses (docs/adr/0014): asked of every receiver, every value an argument or a property write converts to,
// and every path rc or a restart would open.

#pragma once

#include "CoreMinimal.h"
#include "Dom/JsonValue.h"

class FProperty;
class UObject;

namespace PlaytestRefusal
{
	enum class EOp : uint8
	{
		Read,
		Write,
		Call,
		Argument,
	};

	FString Refused(const UObject* Object, FName Member, EOp Op);

	FString RefusedValue(const FProperty* Property, const void* ValuePtr);

	bool DataPath(const FString& Path);

	FString RefusedJson(const TSharedPtr<FJsonValue>& Json, int32 Depth = 0);

	const TSet<FName>& RefusedClasses();
	const TArray<FString>& RefusedPackages();
	const TMap<FName, TSet<FName>>& RefusedProperties();
	const TMap<FName, TSet<FName>>& RefusedFunctions();
}
