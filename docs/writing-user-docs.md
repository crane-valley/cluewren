# Writing the user-facing documents

Applies to README.md, SECURITY.md and every page of the docs site and the landing page (both languages). It does not
apply to CHANGELOG.md, docs/adr, commit messages or pull requests. `eng/check-docs.ps1` enforces the mechanical part
in CI; the rest is checked by reading.

## Who reads them

A developer who installs and uses Cluewren, and a security reviewer who decides whether to allow it. Both want the
product as it is in the current release: what it does, what it guarantees, what it does not, and what to do.

## What goes where

- What the product does now, its limits, how to install, verify, upgrade and troubleshoot: README, SECURITY.md, site.
- What changed in a release, which issue it closed, what was fixed: CHANGELOG.md only.
- Why a design was chosen and what was rejected: docs/adr only. A user document links to an ADR in a list of design
  records, never in a heading or in the middle of a sentence.
- How something was measured or checked (machine, repetitions, load, tools): CHANGELOG.md or the ADR. A user
  document states the result the reader needs ("on Godot through the editor the frame rate was 6-11% lower during
  a run"), not the lab notes.

## Rules for a user document

1. Present tense, current release. No "now", "no longer", "before", "previously", "since 0.1.1", "as in 0.1.0".
   Versions are named only where the reader acts on them: download names, upgrade steps, compatibility (which
   brain works with which plugin), and files an older version left on disk.
2. No issue or pull request numbers, except an open issue given as the place where a known limitation is tracked,
   written as a link.
3. No names from the source: files, classes, methods, constants, tests. Say what the thing does. Allowed: commands,
   options, environment variables, files and folders the user sees on disk, MCP tool names, quoted program output.
4. Nothing about how Cluewren is built or tested (its test suite, CI, review). A reviewer is told what is
   guaranteed; evidence belongs to the release notes.
5. One fact, one place on a page. A summary section links to the detail instead of repeating it.
6. Words a user cannot act on are explained or replaced ("messages the harness has the brain send to the game",
   not "posts" or "frames", unless quoting program output).
7. No drafting leftovers: TODO, PENDING, placeholder dates.

## Briefing a writer

Give the writer statements already in reader form. Keep history, issue numbers, measurements' provenance and
review findings in a separate part of the brief marked "for CHANGELOG.md and ADRs only". Point the writer at this
file. When reviewing the result, read it once as the developer and once as the reviewer, apart from checking it
against the facts.
