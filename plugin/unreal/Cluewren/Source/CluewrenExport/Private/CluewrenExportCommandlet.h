// Modified by Crane Valley: renamed for Cluewren; the -progress file went with the panel that showed it.
#pragma once

#include "CoreMinimal.h"
#include "Commandlets/Commandlet.h"
#include "CluewrenExportCommandlet.generated.h"

/**
 * Exports a project's game logic and data as plain text for a language model to research:
 * Blueprints as pseudo-code, input assets, data tables/assets, the asset list and the maps' actors.
 *
 * UnrealEditor-Cmd.exe <project>.uproject -run=CluewrenExport -out=<dir> [-root=/Game[,/Other]] [-maxmaps=N] [-nomaps]
 */
UCLASS()
class UCluewrenExportCommandlet : public UCommandlet
{
	GENERATED_BODY()

public:
	UCluewrenExportCommandlet();

	virtual int32 Main(const FString& Params) override;
};
