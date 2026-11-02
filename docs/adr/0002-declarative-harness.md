# 0002 Declarative harness instead of model-written code

Status: superseded by 0004
Date: 2026-10-31

## Context

Autopilot's language model writes Python harnesses and the brain executes them. On Windows and macOS that code runs in
a plain child process: it has no API keys, but the disk and the network are open (sandbox.py). Game code is the model's
input, so a game can carry prompt injection.

Porting that shape to C# makes it worse. Roslyn-compiled code would run in a .NET process, and AssemblyLoadContext is
not a security boundary.

Independently of the brain, Autopilot's Unity plugin answers `rc` with unrestricted reflection (Reflect.cs: any loaded
type, non-public members, method invoke). A harness that can name System.Diagnostics.Process through `rc` runs anything
inside the editor, so a sandbox around the harness alone would not hold.

## Decision

A harness is data, not code. The language model emits JSON validated against a schema, and the C# runtime interprets it:

- observe: named reads of whitelisted kinds (player pose, view, actor position, a named property).
- options: names, descriptions and legality conditions, each bound to a controller from the kit with parameters
  (walk_to, look_at, press, hold_for, wait, jump_to, ...).
- status and plan: done and failed conditions as expressions over the observation.

Expressions are a small closed language evaluated by the runtime, with no calls and no loops.

The forked plugin replaces `rc` by member, not by type, because a type allowlist does not stop code: property getters
and setters run arbitrary project code, and upstream's FromJson constructs objects.

- By default only field reads are allowed, on objects in loaded scenes, and only for primitive, string and enum fields,
  an explicit list of Unity value types, and arrays or List<T> of those. Reading such a field runs no project code. A
  field of any other type answers with its type name only: no ToString, no enumeration of arbitrary objects.
- Property reads, field and property writes, and method calls exist only for members the project lists in a committed
  allow file, by exact type and member. A model cannot change that file.
- FromJson object construction, static access to non-project types, and asset handles are removed.

The check lives in the plugin, because the plugin is the side that holds the capability.

Limits, enforced by the runtime and refused at validation where possible: harness JSON size, expression depth and
evaluation count, observation count and result size, controller and episode time. Every end of a run, normal or not,
releases all held input.

A hand-written C# harness is also supported, compiled into the brain or a project assembly by the developer. It is
trusted like any other source code and never comes from a model.

## Consequences

- No execution of model output, so the sandbox problem is removed rather than ported.
- Less expressive than Python. Reflex play (per-tick dodging on an animation tell) needs controllers in the kit, so the
  kit grows where Autopilot's model wrote code. This is the main cost and it is expected to show in goal completion.
- Learning between runs stores harness JSON and notes, with no lib.py.
- Measure the loss on the same prompts before reconsidering.

## Alternatives rejected

- Roslyn in process: not a boundary.
- Roslyn out of process with an AppContainer or restricted token: real isolation on Windows only, a large surface to
  get right, and `rc` still has to be restricted. Reopen if the JSON harness cannot reach useful goals.
- Lua through MoonSharp: a real interpreter sandbox, but a second language, against ADR 0001's reason for existing.
