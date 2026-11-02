// Modified by Crane Valley: renamed, and a Shipping build of it is an error.
#include "Modules/ModuleManager.h"

#if UE_BUILD_SHIPPING
#error The bridge must never reach a Shipping build (TargetConfigurationDenyList in Cluewren.uplugin)
#endif

IMPLEMENT_MODULE(FDefaultModuleImpl, Cluewren)
