## The engine: Unreal

Objects and paths. Things are actors, named by their object paths: `/Game/Maps/Village.Village:PersistentLevel.BP_Door_C_3`.
A component is `<actor path>.<Component>` (`<actor>.MeshComp`). `game.NameOf(path)` is the last part (`BP_Door_C_3`). The
kit's frame is Unreal's own: X forward, Y right, Z up, centimetres, so `game.ToKit` and `game.ToNative` change nothing.

Finding things. `game.Actors("BP_Door")`, `game.Actors("Door")` or `game.Actors("/Game/X/BP_Door.BP_Door_C")`: actors of a
class, subclasses too. A Blueprint goes by its name, a C++ class by its name in the project's modules.
`game.AllActors()`: the level's things. `game.Named("door")`: by name.

Reading and calling (`game.Get`, `game.Props`, `game.Call`, `game.Ret`, `game.Describe`). They reach any object and any
property, protected and Blueprint variables included. `game.Describe(path)` lists its class, properties and
Blueprint-callable functions with their arguments. Static library functions go to class default objects, as
`game.Ret("/Script/Engine.Default__GameplayStatics", "GetPlayerCharacter", new { WorldContextObject = await game.World(),
PlayerIndex = 0 })`. A rotation is `{"Pitch","Yaw","Roll"}` in degrees. A destroyed actor can still be read until garbage
collection: its `bActorIsBeingDestroyed` says it is gone.

The player. `await game.Pawn()` is the possessed pawn and `await game.Controller()` its controller.
`(await game.Pose()).At` is the pawn's location, the eye the camera manager's. `Movement(game)` reads CharacterMovement and
the capsule: `MaxWalkSpeed`, `JumpZVelocity`, `MaxStepHeight`, `AirControl`, the gravity, the half height. A pawn without
them gives nulls: read its Blueprint in the export.

Input. FKey names (`W`, `SpaceBar`, `E`, `LeftShift`, `Escape`, `One`, `LeftMouseButton`, `MouseX`...) or an Enhanced Input
action path (`game.Press("/Game/Input/IA_Jump.IA_Jump")`, a value optional). `(await game.Context())["controls"]` lists the
actions mapped for the player now, with their keys; the export's `input/` has the mapping contexts. Pointing and clicking
is in KIT-POINTER.md: `pointer` puts the mouse there and a key click reaches the game; widgets under it take `game.UiAct`.
`pointer` needs a rendered window.

Sessions. In the editor the session is Play In Editor. `game.Restart()` starts the run's level over, and
`game.Restart(map: "/Game/Maps/Level")` opens a map. A game that boots into a main menu: set the harness's `StartMap` to
its gameplay level (from the export's maps.txt), or click through the menu in Setup with `game.Ui()` and `game.UiAct`.

UI. `game.Screen()` keys texts `<UserWidget>.<TextBlock>` (UMG). `game.Ui()` lists its buttons, sliders and the like.

The export. `Saved/CluewrenCodex` holds the project as text: `summary.txt`, `blueprints/` as pseudo-code (variables,
components, defaults, graphs), `input/`, `data/` (data tables, data assets, enums, structs), `assets.txt` and `maps.txt`.
C++ is under `Source/`, settings under `Config/`. It is made in the background when a session starts, so it can arrive
after the run does.
