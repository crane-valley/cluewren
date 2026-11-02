# 0005 The language model is the user's own coding agent

Status: accepted
Date: 2026-10-31

## Context

An earlier draft used the Anthropic NuGet package with a tool loop of its own, which needs API billing and costs more
than Autopilot. A later draft had the brain start `claude -p` on the user's login. That is close to what Claude Code's
terms keep from third-party products, which may not offer claude.ai login without Anthropic's approval, and this project
is to be published.

Autopilot's subscription path has another shape: the user's own Claude Code or Codex is the agent, and Autopilot is an
MCP server and a skill it uses (agents/external.py: "not an agent Autopilot runs"). Its repository shows no Anthropic
approval. Claude Code bills whatever the user logged in with, or an API key if the user set one, so this one path covers
subscriptions and API keys alike.

## Decision

The brain never starts or drives a language model. It is an MCP server plus a skill and a frame-describer subagent,
installed by a `connect` command for Claude Code and for Codex, as upstream's `autopilot connect` does.

- The user's agent reads the project and writes harness.cs and lib.cs with its own file tools. The brain adds no file
  tools. A run's folder and memory live under the project's UserSettings/PlaytestAgent, so the agent's normal project
  permissions cover them and they survive a Library rebuild.
- The agent calls start_run, probe, use_harness, run_harness, check_harness, wait, frames and report. The brain compiles and runs
  what it writes, as ADR 0004 says.
- Upstream's report rules stay: writes made by probes are told apart from play, a claim about looks needs frames, and
  unfinished work is inconclusive.
- The brain holds no language model key and never reads the agent's credentials. It makes no model calls.
- Jev remains the brain's own call, with the user's TypeSafe key.
- `connect` owns its skill and agent files and one marked entry in shared config such as Codex's config.toml. It changes
  nothing else, and `connect --remove` takes those out.
- Runs start from the user's agent. The editor window shows, steers and stops a run, as it does for upstream's connect
  runs. It does not start one, and the plugin's own run request (-Playtest, hello.run) is removed.

The bench under ADR 0006 and any CI are the user's own scripts around their own CLI. Probed on Claude Code 2.1.314 with a
claude.ai Max login (authMethod claude.ai, apiKeySource none), this locks a headless run down to the given tools:

    claude -p --tools Read,Grep,Glob,Write,Edit,Agent --disable-slash-commands
      --strict-mcp-config --mcp-config <file> --setting-sources ""
      --settings <allow rules> --permission-mode dontAsk --output-format stream-json --verbose

Reads and writes inside the working folder worked. Reads outside, writes outside by absolute and by parent-relative
path, and a shell command were all refused. The allow rules must scope writes, Write(./**) and Edit(./**), because a bare
Write allow let a write through to another drive. Agent stays for the frame describer, whose tools are limited by its
own definition. --safe-mode removes MCP servers and --bare does not read subscription credentials, so neither fits. The
docs recommend this recipe and it is the user's choice to run it.

## Consequences

- Same subscription economics as Autopilot, with no billing logic of our own to get wrong.
- Much less code: no agent process, stream parsing, model selection or cost cap in the brain. The user's agent owns turn
  limits and model choice.
- The editor has no Run button for a model-driven run, which upstream has with API keys. This is the main feature given
  up. Accuracy, speed and cost are not affected.
- In an interactive session the agent keeps the user's own tools, so a game's code reaches a session that may have a
  shell. The same holds upstream. Unattended runs should use the recipe above.
- The AppContainer in ADR 0004 still contains harness code, whatever the agent's session can do.

## Alternatives rejected

- Anthropic NuGet with our own loop: API billing, more code.
- Brain-started CLI for subscriptions: unclear under the terms for a published product, and a second path to maintain.
- Brain-started CLI for API keys only: Claude Code on an API key already covers this through `connect`.
