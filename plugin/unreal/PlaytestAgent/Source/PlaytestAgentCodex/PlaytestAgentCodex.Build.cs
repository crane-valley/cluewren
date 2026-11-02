// Modified by Crane Valley: renamed for Playtest Agent.
using UnrealBuildTool;

// The PlaytestAgentCodex commandlet lives in its own editor module loaded at the Default phase: commandlets are looked up
// before PostEngineInit modules (PlaytestAgentEditor) load.
public class PlaytestAgentCodex : ModuleRules
{
	public PlaytestAgentCodex(ReadOnlyTargetRules Target) : base(Target)
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
