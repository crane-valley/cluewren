# 0012 The product is named Cluewren

Status: accepted
Date: 2026-11-03

## Context

The working name playtest-agent is generic and cannot be registered or told apart from other tools. The name must not
be confusable with Aura or Autopilot, must work as a repository, package, plugin and command name, and must read well in
English and in katakana. Candidates from Codex, the advisor and the session were checked on 2026-11-02 for .com, .dev
and .io (RDAP; .io through whois.nic.io, since rdap.org answers 404 for every .io), NuGet, npm, PyPI, GitHub, Steam and
a web search.

## Decision

The product is Cluewren (kuruuren): a wren that goes about collecting clues, as the agent collects evidence from play.

- Repository: crane-valley/cluewren.
- .NET: projects and namespaces Cluewren and Cluewren.Kit; the command and the MCP server are `cluewren`.
- Plugins: Unity package com.cranevalley.cluewren, Godot addons/cluewren, Unreal plugin Cluewren.
- Run memory and keys: Cluewren in place of PlaytestAgent in each engine's folder and under LocalAppData.

Rejected:

- Doorwit, Roamwit, Questpip: also clear on every check; Roam is crowded in software (Roam Research), Quest and pip call
  up Meta Quest and Python's installer, and the owner chose Cluewren over Doorwit.
- bokken, tryout, understudy, sparring, redoubt, tameshi, testudo, homeplay, roamfin, stepwit: a domain, package,
  repository, game or trademark of that name is in use.

## Consequences

- The owner searched the registers on 2026-11-03: J-PlatPat simple trademark search and USPTO wordmark search (live and
  dead) for Cluewren returned nothing. Those are exact-word searches: marks that sound alike (J-PlatPat's phonetic
  search) and EUIPO were not searched. A conflict found later reopens this decision.
- The rename touches about 150 files and the plugin folders, and the Unity plugin's Autopilot.Playtest namespaces,
  assemblies, defines and keys become Cluewren with it. Existing MCP registrations, bench copies and the keys file under
  LocalAppData\PlaytestAgent move with it.
- ADRs 0001 to 0011 and docs/comparison-2026-11.md keep the working name as written: Playtest Agent and playtest-agent
  there are Cluewren.
