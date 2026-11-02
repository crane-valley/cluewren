// Modified by Crane Valley: renamed, and reduced to what starting and stopping play needs.
using UnrealBuildTool;

public class PlaytestAgentEditor : ModuleRules
{
	public PlaytestAgentEditor(ReadOnlyTargetRules Target) : base(Target)
	{
		PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;

		PrivateDependencyModuleNames.AddRange(new string[]
		{
			"Core",
			"CoreUObject",
			"Engine",
			"UnrealEd",
			"PlaytestAgent",
		});
	}
}
