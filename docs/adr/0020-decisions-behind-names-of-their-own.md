# 0020 Decisions behind names of their own

Status: accepted
Date: 2026-11-16

## Context

The decision model is TypeSafe's Jev, by a model id compiled into the binary, and the kit's public types carried its
names (JevDecision, JevRequest, Noul). Harness code and lib.cs that users keep are written against those names.

## Decision

- The kit's public names are Decision, DecisionRequest, YesNo, IDecider. Jev remains the one decider implemented.
- TYPESAFE_MODEL and TYPESAFE_BASE_URL (environment or keys.env) override the model id and the address. The address
  must be https without user information, and the client follows no redirects: the key goes only where the user said.
- The request and answer bodies on the wire are still TypeSafe's. No second decider is added.

## Consequences

- A model retired by TypeSafe is replaced by a setting, not by a release.
- Another decider needs a translation of the request and answer, which this does not build.

## Alternatives rejected

- Renaming nothing until a second decider exists: by then users' lib.cs would hold the old names.
