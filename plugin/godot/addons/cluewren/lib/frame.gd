## The kit frame: every op but `rc` speaks Unreal's frame, so a brain's geometry works across engines.
##
## 3D: Z up, left-handed, centimetres. Godot (x right, y up, -z forward, metres) -> kit (X, Y, Z) = 100 * (-z, x, y).
## Yaw: degrees about Z from +X toward +Y (turning right is positive): kit yaw = -deg(rotation.y) of the global basis
## (0 when looking along Godot's -Z). Pitch: positive up, = deg(rotation.x). Roll: positive with the right side down,
## = -deg(rotation.z). Rotations are read from the global basis as Euler YXZ (Godot's default order).
## 2D: (x right, y down, pixels) -> kit (X, Y, Z) = (x, 0, -y), 1 pixel = 1 unit; a 2D rotation (clockwise on screen
## positive) is a kit pitch: Pitch = -deg(rotation), Yaw = Roll = 0.
extends RefCounted

const SCALE := 100.0


static func r2(x: float) -> float:
	return snappedf(x, 0.01) if is_finite(x) else x


static func kit(v: Vector3) -> Dictionary:
	return {"X": r2(-v.z * SCALE), "Y": r2(v.x * SCALE), "Z": r2(v.y * SCALE)}


static func kit_dir(v: Vector3) -> Dictionary:
	return {"X": r2(-v.z), "Y": r2(v.x), "Z": r2(v.y)}


static func kit2d(v: Vector2) -> Dictionary:
	return {"X": r2(v.x), "Y": 0.0, "Z": r2(-v.y)}


static func kit_extent(size: Vector3) -> Dictionary:
	return {"X": r2(absf(size.z) * SCALE * 0.5), "Y": r2(absf(size.x) * SCALE * 0.5), "Z": r2(absf(size.y) * SCALE * 0.5)}


## A kit vector ({"X","Y","Z"}) as a Godot vector, without scaling (a direction).
static func from_kit_dir(d) -> Vector3:
	if typeof(d) != TYPE_DICTIONARY:
		return Vector3.ZERO
	return Vector3(float(d.get("Y", 0.0)), float(d.get("Z", 0.0)), -float(d.get("X", 0.0)))


## A kit location as a Godot point (metres).
static func from_kit(d) -> Vector3:
	return from_kit_dir(d) / SCALE


static func yaw_of(basis: Basis) -> float:
	return -rad_to_deg(basis.orthonormalized().get_euler().y)


static func pitch_of(basis: Basis) -> float:
	return rad_to_deg(basis.orthonormalized().get_euler().x)


static func kit_rot(basis: Basis) -> Dictionary:
	var e := basis.orthonormalized().get_euler()
	return {"Pitch": r2(rad_to_deg(e.x)), "Yaw": r2(-rad_to_deg(e.y)), "Roll": r2(-rad_to_deg(e.z))}


static func kit_rot2d(rotation: float) -> Dictionary:
	return {"Pitch": r2(-rad_to_deg(rotation)), "Yaw": 0.0, "Roll": 0.0}


## Kit yaw of a Godot direction (degrees, -180..180).
static func yaw_of_dir(d: Vector3) -> float:
	return rad_to_deg(atan2(d.x, -d.z))


## A Godot horizontal direction from a kit yaw.
static func dir_of_yaw(yaw_deg: float) -> Vector3:
	var a := deg_to_rad(yaw_deg)
	return Vector3(sin(a), 0.0, -cos(a))


static func wrap180(a: float) -> float:
	return fposmod(a + 180.0, 360.0) - 180.0
