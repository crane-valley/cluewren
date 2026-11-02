# The kit: pointing, legacy input, moving without keys

- Unity: code that reads the legacy Input Manager (`UnityEngine.Input.GetKey`, `GetAxis`, `GetButton`, `GetMouseButton`,
  `mousePosition`...) sees the same keys too: the plugin routes those reads through itself when the game compiles.
  Axes follow the project's Input Manager settings: `GetAxis("Horizontal")` from `D` rises toward 1 over a few frames
  (its sensitivity) and falls back when let go, `MouseX` 20 is `GetAxis("Mouse X")` 2 (its sensitivity 0.1),
  `MouseWheelAxis` is one wheel notch, and the gamepad is joystick 1. `(await game.Context())["input"]["legacy_input_reads"]`
  counts the reads routed; with `"keys": false` nothing reaches the game: act through its own methods with
  `game.Call` (report them as direct changes), click its UI, and move the player with
  `game.Request("move", new Dictionary<string, object> { ["direction"] = ..., ["seconds"] = s })`, which moves its
  CharacterController at the walk speed.
- Pointing and clicking. `game.Request("pointer", new { screen_x = x, screen_y = y })` puts the mouse there without
  moving across (-1 to 1 from the middle, y up, the same numbers `view` gives a thing), or `new { world = point }`, or
  `new { @object = path }` (where the camera shows it); it answers `{"screen_x", "screen_y", "x", "y", "on_screen"}`. Then
  `game.Press("LeftMouseButton")` (or `RightMouseButton`) clicks there, in the world as on the UI. To drag (a selection
  box, a slider): point, `game.Hold("LeftMouseButton")`, point again in two or three steps, `game.Release(...)`. Games
  often ignore a drag that starts over their HUD, and pan the camera while the pointer is at a screen edge: point well
  inside the view. A click lands on whatever is in front at that spot (another object may cover the one you meant):
  check what got selected or ordered.
- `game.Request("move", ...)` with `direction` (a kit vector), `seconds`, and optional `via` (stick, keys or controller) walks a
  direction of the kit's frame through the game's move action (its gamepad stick: any direction; its keys: 8
  directions), or the CharacterController (`controller`); answers `{"moved", "via"}`.
