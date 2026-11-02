## The engine: Godot

Objects and paths. Things are nodes, named by their paths in the scene tree: `/root/<Scene>/Player`,
`/root/<Scene>/Enemies/Bat2`. A component is a child node (`/root/<Scene>/Player/Head/Camera3D`). Autoloads, the game's
global singletons, are `/root/<Name>` and outlive scene changes. `game.NameOf(path)` is the last part (`Bat2`).

Finding things. `game.Actors("CharacterBody3D")` (a Godot class, subclasses too), `game.Actors("Enemy")` (a script's
`class_name` or C# class, scripts extending it too; a script with neither is matched by its file name, `bat` for
`bat.gd`), `game.Actors("group:enemies")` (the nodes in a group). `game.AllActors()`: the scene's things (its direct
children, physics bodies and areas, instanced scenes), not every mesh and collision shape under them.
`game.Named("bat")` searches those.

Reading and calling (`game.Get`, `game.Props`, `game.Call`, `game.Describe`). They reach script variables, script
constants (read only), built-in properties and methods. A part of a property takes Godot's `a:b` path:
`game.Get(path, "position:y")`. `game.Call` takes arguments by name; leave out those with defaults. `game.Describe(path)`
lists the script's own variables, constants and functions first. Engine singletons and autoloads go by name:
`game.Call("Input", "is_action_pressed", new { action = "jump" })`, `game.Get("/root/<Autoload>", "score")`. Values are
Godot's own: metres, x right, y up, -z forward, vectors `{"x","y","z"}`, a node as its path, a resource as its `res://`
path, an enum as its name, a `rotation` in radians. Turn a position or velocity into the kit's frame with
`game.ToKit(value)` (and back with `game.ToNative(vec)`) before mixing it with `game.Loc`, `game.Pose` or `game.View`,
which are already in the kit's frame.

The player. The physics body the current camera hangs from, else the first node in group `player`. `await game.Pawn()` is
its path. `(await game.Pose()).At` is the middle of its body (its collision shape's centre), the feet `HalfHeight` below.
`game.Controller()` is empty: Godot has none, so use `game.Pose` or `game.ViewRotation` for the view.

Input. Keys are the same names as everywhere (`W`, `SpaceBar`, `E`, `LeftMouseButton`, `MouseX`...). The addon turns them
into Godot input events, so `_input`, `_unhandled_input`, `Input.IsActionPressed` and `Input.GetVector` all see them. The
game's InputMap actions (project.godot `[input]`, listed in the summary) press by name: `game.Press("interact")`,
`game.Hold("sprint")`. Prefer an action to its key when the code reads the action. Mouse look goes through `game.Mouse`
and `game.Turn`. Without a window the mouse is never captured: a game that turns its view only while it is captured
won't turn.

Scenes. `game.Restart()` reopens the scene the game started in; `game.Restart(map: "res://scenes/level.tscn")`, or the
scene file's name, opens another. Autoloads keep their variables across a restart in a running game, as across a game's
own scene change: read or reset what the verification needs. In the editor a restart is a fresh game process, autoloads
too. Game time starts at 0 in each scene.

Physics. A CharacterBody doesn't step up ledges, so `Movement(game)` has no `Step` and `ReachUp` is the jump's peak
alone; `floor_max_angle` is `MaxSlope`. Walk speed and jump come from the player script's variables or constants
(`SPEED`, `JUMP_VELOCITY`...); a number it can't find is null: read the script then.

What the addon leaves out. `game.View()` tells visible things (name, path, class, center, size, distance, screen
position, crosshair, look: meshes, lights, animation, 3D texts), the camera (horizontal fov) and the body (on_ground,
falling, standing_on, touching, floor slope), with `game.View(new { sonar = true })` for the sonar ways. It has no
`held`, `openings`, `ways_up`, slope ends, lanes, `under`, `overhead`, highlights or effects (its `omitted` lists them):
use the project's files and `game.Locs` or `game.Bounds`. Without `ways_up`, `WalkTo` finds no way up to a raised goal by
itself: walk it to the foot and the top of the ramp or stairs.

UI. `game.Screen()` keys are `<UI>.<Node name>`, `<UI>` being the CanvasLayer or the scene the control belongs to
(`HUD.ScoreLabel`). `game.Ui()` lists Buttons, CheckBoxes, OptionButtons, Sliders, SpinBoxes and LineEdits; `game.UiAct`
clicks with the mouse at the control's middle.

2D games. Positions are x right, y down pixels as kit `(X = x, Y = 0, Z = -y)`, one unit a pixel: kit X is right, kit Z is
up. `game.View` lists drawn sprites. The 3D controllers that turn the view (`WalkTo`, `LookAt`) don't apply: press the
movement actions and read `game.Loc`.
