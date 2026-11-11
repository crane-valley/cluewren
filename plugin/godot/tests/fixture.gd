extends Node3D

const LIMIT := 5

var score := 3
var bag := {}
var list := []


func add(a: int, b: int) -> int:
	return a + b


static func twice(x: int) -> int:
	return x * 2


func pick(path := "res://.godot/cluewren/runs/x/evil.tscn") -> String:
	return path


func pick_node(path := NodePath("res://.godot/cluewren/runs/x/evil")) -> NodePath:
	return path


func pick_safe(path := "res://tests/fixture.gd") -> String:
	return path


func echo(value) -> Variant:
	return value
