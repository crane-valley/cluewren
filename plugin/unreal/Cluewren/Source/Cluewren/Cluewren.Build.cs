// Modified by Crane Valley: renamed; WebSockets and DeveloperSettings out, WinHTTP and BCrypt in.
using UnrealBuildTool;

public class Cluewren : ModuleRules
{
	public Cluewren(ReadOnlyTargetRules Target) : base(Target)
	{
		PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;

		PublicDependencyModuleNames.AddRange(new string[]
		{
			"Core",
			"CoreUObject",
			"Engine",
			"Json",
		});

		PrivateDependencyModuleNames.AddRange(new string[]
		{
			"AssetRegistry",
			"EnhancedInput",
			"InputCore",
			"Slate",
			"SlateCore",
			"UMG",
			"Projects",
			"EngineSettings",
			"PhysicsCore",
			"RHI",
			"RenderCore",
			"ImageWrapper",
		});

		PublicSystemLibraries.AddRange(new string[]
		{
			"winhttp.lib",
			"bcrypt.lib",
		});
	}
}
