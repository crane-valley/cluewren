## The engine: Unity

Objects and paths. Things are GameObjects, named by path: `Main:/Level/Door` (`<scene>:/<root>/<child>`; a path
starting with `/` is looked for in every loaded scene). Objects that share a name under one parent are numbered:
the first is plain (`Coin`), the others `Coin@1`, `Coin@2`... and each keeps its number while it lives (a number is
never given to another object), so a path stays that object or is gone. A component is `<object path>|<Type>`
(`Main:/Player|PlayerMover`); a type is `type:<Full.Name>`, for static members (`game.Get("type:UnityEngine.Time",
"timeScale")`, `game.Ret("type:GameManager", "Restart")`; a short name works when only one type has it). Names escape
`\ / | @` with `\`. `game.NameOf(path)` is the last part (`Coin@2`), as `game.View` / `game.Sees` name things.

Finding things. `game.Actors("Door")`: the GameObjects with a component of that type (a MonoBehaviour's class
name, subclasses and interfaces too; `Collider`, `Rigidbody`, `Camera` work as well). `game.AllActors()`: the scene's
things (objects that aren't plain groups, at the top or under a plain group). `game.Named("door")`: by name.
`game.Request("find", new Dictionary<string, object> { ["class"] = ..., ["name"] = ..., ["inactive"] = true })` also lists
inactive objects. The codex's `scenes/<scene>.txt` lists every object of a scene by these same paths with its components
and their values.

Reading and calling (`game.Get`, `game.Props`, `game.Call`, `game.Describe`). On a component: its fields,
public and private (an auto-property by its name), and properties; methods by name with arguments by parameter name
(`game.Call("Main:/Door|Door", "Open")`, `game.Call(path, "Damage", new { amount = 10 })`). On a GameObject path: its own state
(`name`, `tag`, `layer`, `activeSelf`, `position`, `eulerAngles`, `components`, `children`) and, for a name it doesn't
have, the member of its component that has it (the game's scripts first), so `game.Get("Main:/Player", "health")`
reads the player script's field. A method returning `IEnumerator` is started as a coroutine. Values are Unity's own:
metres, x right, y up, z forward; vectors `{"x","y","z"}`; references as paths (`""` for none). Turn a position or
velocity into the kit's frame with `game.ToKit(value)` (and back with `game.ToNative(vec)`) before mixing it with
`game.Loc`, `game.Pose` or `game.View`, which are already in the kit's frame. Rotations read with `game.Get` are
`eulerAngles` (y turns right; x > 0 looks down) or quaternions `{"x","y","z","w","euler"}`. When a verification's
setup must move something out of the way (never the player), `game.Set(path, "position", game.ToNative(v),
why: ...)`; an object with a CharacterController keeps its own position until it is disabled and enabled again.

The player. The object tagged `Player`, else the one with a `PlayerInput`, else the one with a
`CharacterController` (the main camera's first). If the game's player is something else, name it once in Setup:
`game.Request("context", new { player = path })`. `(await game.Pose()).At` is the middle of its body (the controller's
centre: the feet are `HalfHeight` lower), the eye is the main camera. `Movement(game)` reads the
CharacterController (half height, radius, step, max slope) and the player script's fields (walk speed from a field
like `walkSpeed`/`moveSpeed`/`speed`, jump from `jumpSpeed`/`jumpForce` or `jumpHeight`, gravity from `gravity` or
`Physics.gravity` (`Physics2D.gravity` without the Physics module, else 9.81)); a number it can't find is null: read
the script in the codex and use the real one.

Input. Keys go in as a keyboard, mouse and gamepad would (the plugin's own Input System devices): FKey names
(`W`, `SpaceBar`, `E`, `LeftShift`, `Escape`, `One`, `LeftMouseButton`, `MouseX`/`MouseY` in pixels of mouse
movement, `Gamepad_LeftX`, `Gamepad_FaceButton_Bottom`...). Input System actions by `Map/Action` or name:
`game.Press("Player/Jump")`, `game.Hold("Player/Move", new[] { 0.0, 1.0 })` (a Vector2: x right, y forward) then
`game.Release("Player/Move")`. `(await game.Context())["controls"]` lists each action with its keys and bindings; the
codex's `input/` has the action maps. Mouse look goes through the game's own look code: `game.CalibrateLook()` once,
then `game.Turn` / `LookAt`.
- Games that read the legacy Input Manager, games played by pointing and clicking, and moving with `game.Request("move", ...)`:
  KIT-POINTER.md.

UI. `game.Screen()` keys texts `<Canvas>.<object>` (uGUI Text and TextMeshPro on screen-space canvases) and
`<UIDocument object>.<element>` (UI Toolkit). `game.Ui()` lists uGUI and UI Toolkit buttons, toggles, sliders,
dropdowns and text fields with ids `<Canvas>.<object>`; `game.UiAct(new { id = ... })` clicks them as a mouse would (the
button's `onClick` runs). Text drawn with `OnGUI` (IMGUI) can't be read or clicked. A world-space canvas is a thing in
the world: its texts are in `view`'s `look.texts`.

Sessions. In the editor, the session is play mode: `game.Restart()` reloads the scene play mode started with
(`map:` a scene name or path from the codex's build_scenes.txt), `game.Request("session", new { action = "restart",
fresh = true })` leaves and re-enters play mode (static fields reset too). A player build restarts its first scene,
or the one it was launched with. Objects kept with `DontDestroyOnLoad` (game managers, scores) survive a scene reload,
as they do for the game itself: read their state after a restart instead of assuming it reset. A game that boots
into a menu: set the harness's `StartMap` to the gameplay scene, or click through the menu in Setup.

What the plugin leaves out. `game.View()` tells visible things (name, path, class, center, size, distance,
screen position, crosshair, `look.parts` mesh names, texts, animation clip) and the body (velocity, on_ground,
falling, standing_on, touching), with world-aligned boxes, and `game.View(new { sonar = true })` (`Sonar`, which `WalkTo`
steers by) feels each way with the player's own capsule (a step above the floor) and rays down for drops: `by` wall,
drop, steep or reach. It has no `held`, `openings`, `ways_up`, `point_blank` or `lane` (listed in its `omitted`), so `WalkTo`
finds no way up to a raised goal by itself: walk it to the ramp's or stairs' foot and top first (from the codex,
`game.Locs`). `WalkTo` finds its way round obstacles on the map it feels out with the sonar. `watch` sees objects move, turn
and change size, not meshes appearing or effects. Detectors: errors and exceptions in the log, NaN transforms, falling out
of the level, long falls, hitches, on-screen counts above their maximum.
