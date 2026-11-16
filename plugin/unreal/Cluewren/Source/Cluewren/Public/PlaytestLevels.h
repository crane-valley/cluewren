// Modified by Crane Valley: renamed for Cluewren.
// The project's levels, and the one a run asks for by path or name: the brain
// plays a level other than the one open in the editor through the `session` op's `map` (the editor's Play In Editor
// map override: what is open in the editor stays as it is).

#pragma once

#include "CoreMinimal.h"

namespace PlaytestLevels
{
	/** The project's levels (World assets in /Game and the project's own plugins), by package name, sorted. */
	CLUEWREN_API TArray<FString> List();

	/** The level `Name` means: a package name (/Game/Maps/Arena, its .Arena object part or .umap ending allowed) or
	 *  a level's own name (Arena), found among List()'s levels; OutPackage is List()'s own name for it. False, with why in
	 *  OutError, when there is none or several have that name. A map comes from harness code too: nothing but the
	 *  project's levels is looked at, and a name that isn't Shaped is not looked up at all. */
	CLUEWREN_API bool Resolve(const FString& Name, FString& OutPackage, FString& OutError);

	/** Whether Name is made only of what a package or level name has. */
	bool Shaped(const FString& Name);

	/** A level's object path for loading it (/Game/Maps/Arena -> /Game/Maps/Arena.Arena). */
	CLUEWREN_API FString ObjectPath(const FString& Package);

	/** A world's package name without Play In Editor's prefix (/Game/Maps/UEDPIE_0_Arena -> /Game/Maps/Arena). */
	CLUEWREN_API FString PackageOf(const class UWorld* World);

	/** The level open in the editor (its package name; empty in a game). */
	CLUEWREN_API FString EditorLevel();
}
