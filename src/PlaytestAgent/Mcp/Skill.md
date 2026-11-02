---
name: playtest-agent
description: Playtest the game open in the user's Unity, Unreal or Godot editor with Playtest Agent (its `playtest-agent` MCP server) - explore the game for bugs, test it (go over it and report every problem), verify a claim about how it works or looks, or reach a goal in play. Use when the user asks to playtest, test, verify, check or explore their game, or to use Playtest Agent.
---

# Playtesting with Playtest Agent

Playtest Agent plays the game open in the user's editor: a fast decision model makes the choices in play, inside a
harness you write for this game and this request. You research the game's code, try things in the live game, write and
revise the harness, and report what play showed. Its tools come from the `playtest-agent` MCP server.

1. The game's project must be open in its editor with the Playtest Agent plugin. When start_run says no editor connected,
   tell the user that and stop.
2. Call `start_run` with the request in the user's words and its kind. A request is one of four types:
   - an exploration ("Explore the crafting system in Lvl_Main"): `explore`, with `minutes` of play when the user gives
     them;
   - a test ("Go through the currently open level and tell me everything that looks wrong"): `explore` too, as it
     plays as an exploration;
   - a verification ("Verify that the tree shakes when you hit it with an axe"): `verify`;
   - a goal ("Kill the boss while keeping your health above 20% of its maximum value"): `goal`.
   Pass `time_limit_minutes` when the user gives a time limit.
3. Read the INSTRUCTIONS.md it names, all of it. Follow it: it is how to work, what the kit is,
   and the rules a report must meet.
4. A call that answers "still going" (an episode, the game starting) isn't done: call `wait` until it gives the result.
5. To look at the game's picture, never call `frames` yourself: hand the episode, the game times and open questions
   (nothing about the request or what you expect) to the `frame-describer` subagent, as INSTRUCTIONS.md says.
6. If start_run asks for the decision model's key, tell the user where it goes. Never ask them to paste a key into this
   conversation.
7. Once `report` says "Reported", the run is over: tell the user its outcome in a few lines.

Write only in the run's folder and the game's memory folder, both named by start_run, and don't change the user's
project or the game's files: the game is acted on only through play, with the tools of `playtest-agent`.
