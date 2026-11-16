// Modified by Crane Valley: renamed for Cluewren.
using UnrealBuildTool;

// The CluewrenExport commandlet lives in its own editor module loaded at the Default phase: commandlets are looked up
// before PostEngineInit modules (CluewrenEditor) load.
public class CluewrenExport : ModuleRules
{
	public CluewrenExport(ReadOnlyTargetRules Target) : base(Target)
	{
		PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;

		PrivateDependencyModuleNames.AddRange(new string[]
		{
			"Core",
			"CoreUObject",
			"Engine",
			"UnrealEd",
			"Projects",
			"AssetRegistry",
			"BlueprintGraph",
			"UMG",
			"UMGEditor",
			"EnhancedInput",
			"InputCore",
		});
	}
}
