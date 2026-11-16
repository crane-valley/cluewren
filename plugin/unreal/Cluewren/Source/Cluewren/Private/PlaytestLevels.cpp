// Modified by Crane Valley: renamed for Cluewren.
// The project's levels and the one a run asks for (PlaytestLevels.h).

#include "PlaytestLevels.h"

#include "AssetRegistry/AssetData.h"
#include "AssetRegistry/AssetRegistryModule.h"
#include "AssetRegistry/IAssetRegistry.h"
#include "Engine/Engine.h"
#include "Engine/World.h"
#include "Interfaces/IPluginManager.h"
#include "Misc/PackageName.h"
#include "UObject/Package.h"

namespace PlaytestLevels
{
namespace
{
	/** Where the project's own levels are: /Game and its project plugins' content (never the engine's). */
	TArray<FString> Roots()
	{
		TArray<FString> Out = { TEXT("/Game/") };
		for (const TSharedRef<IPlugin>& Plugin : IPluginManager::Get().GetEnabledPluginsWithContent())
		{
			if (Plugin->GetLoadedFrom() == EPluginLoadedFrom::Project)
			{
				Out.Add(Plugin->GetMountedAssetPath());
			}
		}
		return Out;
	}

	/** A package name from what a run gave: the .Object part and a .umap ending left out, slashes forward. */
	FString PackageName(FString Name)
	{
		Name.TrimStartAndEndInline();
		Name.ReplaceInline(TEXT("\\"), TEXT("/"));
		Name.RemoveFromEnd(TEXT(".umap"));
		int32 Dot;
		if (Name.FindLastChar(TEXT('.'), Dot) && Dot > Name.Find(TEXT("/"), ESearchCase::CaseSensitive, ESearchDir::FromEnd))
		{
			Name.LeftInline(Dot);
		}
		return Name;
	}

}

TArray<FString> List()
{
	IAssetRegistry& Registry = FModuleManager::LoadModuleChecked<FAssetRegistryModule>(TEXT("AssetRegistry")).Get();
	if (Registry.IsLoadingAssets())
	{
		Registry.WaitForCompletion();  // (the editor's first scan, right after it opened)
	}
	TArray<FAssetData> Assets;
	Registry.GetAssetsByClass(UWorld::StaticClass()->GetClassPathName(), Assets, true);
	const TArray<FString> Under = Roots();
	TArray<FString> Out;
	for (const FAssetData& Asset : Assets)
	{
		const FString Package = Asset.PackageName.ToString();
		if (Under.ContainsByPredicate([&Package](const FString& Root) { return Package.StartsWith(Root); }))
		{
			Out.AddUnique(Package);
		}
	}
	Out.Sort();
	return Out;
}

bool Shaped(const FString& Name)
{
	// What the engine opens a level by is a URL: '[' or a dot makes it a host, ':' a protocol or port, '?' '#' ';' options,
	// a name not starting with '/' a file to look for. Only package and level names' own characters are let through.
	const FString Text = Name.TrimStartAndEnd();
	int32 Slash = INDEX_NONE;
	int32 Dot = INDEX_NONE;
	for (int32 i = 0; i < Text.Len(); ++i)
	{
		const TCHAR C = Text[i];
		if (C == TEXT('/'))
		{
			if (i + 1 == Text.Len() || Text[i + 1] == TEXT('/') || Dot != INDEX_NONE)
			{
				return false;
			}
			Slash = i;
		}
		else if (C == TEXT('.'))
		{
			if (Dot != INDEX_NONE || i == Slash + 1 || i + 1 == Text.Len())
			{
				return false;
			}
			Dot = i;
		}
		else if (!FChar::IsAlnum(C) && C != TEXT('_') && C != TEXT('-'))
		{
			return false;
		}
	}
	// One dot: a package's .Object part, or .umap; a level's own name takes only .umap.
	return !Text.IsEmpty() && (Dot == INDEX_NONE || Text.StartsWith(TEXT("/")) || Text.Mid(Dot).Equals(TEXT(".umap"), ESearchCase::IgnoreCase));
}

bool Resolve(const FString& Name, FString& OutPackage, FString& OutError)
{
	if (!Shaped(Name))
	{
		OutError = FString::Printf(TEXT("No level named %s in the project"), *Name.TrimStartAndEnd());
		return false;
	}
	const FString Package = PackageName(Name);
	const TArray<FString> Levels = List();
	if (Package.StartsWith(TEXT("/")))
	{
		// Matched against the project's levels, not looked for on disk: what is opened is the registry's own name.
		const FString* Level = Levels.FindByPredicate([&Package](const FString& Each) { return Each.Equals(Package, ESearchCase::IgnoreCase); });
		if (!Level)
		{
			OutError = FString::Printf(TEXT("No level %s in the project"), *Package);
			return false;
		}
		OutPackage = *Level;
		return true;
	}
	TArray<FString> Found;
	for (const FString& Level : Levels)
	{
		if (FPackageName::GetShortName(Level).Equals(Package, ESearchCase::IgnoreCase))
		{
			Found.Add(Level);
		}
	}
	if (Found.Num() == 1)
	{
		OutPackage = Found[0];
		return true;
	}
	OutError = Found.Num() == 0
		? FString::Printf(TEXT("No level named %s in the project"), *Package)
		: FString::Printf(TEXT("Several levels are named %s: %s"), *Package, *FString::Join(Found, TEXT(", ")));
	return false;
}

FString ObjectPath(const FString& Package)
{
	// (the world's own name, which a level renamed on disk may not share with its package)
	IAssetRegistry& Registry = FModuleManager::LoadModuleChecked<FAssetRegistryModule>(TEXT("AssetRegistry")).Get();
	TArray<FAssetData> Assets;
	Registry.GetAssetsByPackageName(FName(*Package), Assets);
	for (const FAssetData& Asset : Assets)
	{
		if (Asset.AssetClassPath == UWorld::StaticClass()->GetClassPathName())
		{
			return Asset.GetObjectPathString();
		}
	}
	return Package + TEXT(".") + FPackageName::GetShortName(Package);
}

FString PackageOf(const UWorld* World)
{
	return World ? UWorld::RemovePIEPrefix(World->GetOutermost()->GetName()) : FString();
}

FString EditorLevel()
{
	if (GEngine)
	{
		for (const FWorldContext& Context : GEngine->GetWorldContexts())
		{
			if (Context.WorldType == EWorldType::Editor && Context.World())
			{
				return PackageOf(Context.World());
			}
		}
	}
	return FString();
}
}
