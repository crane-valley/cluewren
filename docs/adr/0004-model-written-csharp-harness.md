# 0004 Model-written C# harness in an AppContainer

Status: accepted
Date: 2026-10-31
Supersedes: 0002

## Context

ADR 0002 made harnesses JSON. That removes code execution but loses expressiveness: per-tick reflexes, tactics of the
game's own and lib.py-style learned helpers are what let Autopilot's runs improve. The owner's requirement is that
nothing may be worse than Autopilot on cost, performance or accuracy, and that security is not a goal to buy with those.
ADR 0002 also reduced `rc` to field reads, which takes abilities away from the harness for the same bad trade.

Measured on the development machine (scratch proof of concept, Windows 11, .NET 10): a framework-dependent .NET child
in a Windows AppContainer, with stdin and stdout pipes:

- first output 61 ms after CreateProcess, including a JSON-free stdin round trip
- 100M-iteration loop in 57 ms, with no visible slowdown
- refused: reading the user's profile, reading another project, listing the profile, writing elsewhere on disk,
  connecting to 1.1.1.1 and to loopback
- allowed: its own container temp folder, and starting a child, which stays in the container

## Decision

The model writes C# harnesses with the same shape and abilities as Autopilot's Python harness: the kit, Option, Plan,
lib.cs for learned helpers, and probes. KIT.md is ported. Executors are async methods that await one tick at a time,
because C# has no `yield from` for composing controllers, and awaiting a nested controller reads the same as calling it.

- The brain compiles with Roslyn in its own process with no analyzers or source generators. Compilation runs no
  model code, and compiler errors go back to the model at once. References are the kit and the BCL.
- The compiled assembly runs in a child process in an AppContainer, with a Job Object that holds it to one process and
  limits its memory, and kills it with the brain. It reaches the game and Jev only through the brain over stdio, as
  Autopilot's sandbox does. Executors and the kit run in the child, so per-tick control adds no extra round trip.
- Each harness revision gets a new child, killed through the Job Object. Startup is about 60 ms, and unloading an
  assembly cannot stop a revision's leftover threads.
- Linux uses bubblewrap as Autopilot does. macOS uses a plain child with no keys, as Autopilot does, until a Seatbelt
  profile is proven.
- If the container cannot be created, the run says so and uses a plain child with no keys in its environment. That is
  Autopilot's own level on Windows, where the child can still read the user's files, and it never fails a run.

The container exists because `run_harness` is an MCP tool: without it, code the model wrote would run with none of the
tool permission prompts the user's agent applies to a shell. It stops direct file, network and process access by that
code. It does not limit the user's agent itself, and it is not a boundary against `rc`.

`rc` keeps Autopilot's abilities, including writes, non-public members and reading delegate targets. The plugin refuses
only the System.IO, System.Diagnostics, System.Net, System.Reflection, System.Runtime.InteropServices, Mono and
Microsoft.Win32 namespaces, so the container is not undone by naming File in one call. A game's own methods, and Unity
APIs that touch files, stay reachable as upstream. The list is not grown to chase those, since that takes abilities away.

## Consequences

- Expressiveness equals Autopilot's, and executors run as compiled code rather than Python.
- A model writes C# rather than Python. Whether that costs turns is measured under ADR 0006.
- More native Windows code in the brain: profile creation, ACL grants on the run folder, attribute lists, Job Objects.
- The proof of concept shows no slowdown from the container itself. The whole path is measured under ADR 0006.

## Alternatives rejected

- JSON harness (ADR 0002): lower accuracy.
- Roslyn in the brain's process: AssemblyLoadContext is not a boundary, and keys are in that process.
- Interpreted C# scripting: slower than compiled, with no benefit.
