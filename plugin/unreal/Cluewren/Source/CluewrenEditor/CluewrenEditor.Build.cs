// Modified by Crane Valley: renamed, and reduced to what starting and stopping play needs.
using UnrealBuildTool;

public class CluewrenEditor : ModuleRules
{
	public CluewrenEditor(ReadOnlyTargetRules Target) : base(Target)
	{
		PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;

		PrivateDependencyModuleNames.AddRange(new string[]
		{
			"Core",
			"CoreUObject",
			"Engine",
			"UnrealEd",
			"Cluewren",
		});
	}
}
