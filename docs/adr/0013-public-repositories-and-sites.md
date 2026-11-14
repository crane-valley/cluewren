# 0013 A public code repository, two site repositories and two sites on cluewren.dev

Status: accepted, the site repositories' visibility amended on 2026-11-12 (see Amendment)
Date: 2026-11-03

## Context

Cluewren is published as open source under the crane-valley organization. The owner asked for a landing page and a
documentation site laid out like Framedash's (framedash, framedash-www, framedash-docs), with main protected and
branches deleted on merge. The owner registered cluewren.dev only, at Cloudflare Registrar.

## Decision

- crane-valley/cluewren holds the code, the plugins and the agent-facing kit docs (docs/KIT*.md) and ADRs.
- crane-valley/cluewren-www is an Astro static site at https://cluewren.dev.
- crane-valley/cluewren-docs is an Astro Starlight site at https://docs.cluewren.dev, with user-facing documentation
  that links to the kit docs in the code repository instead of copying them.
- Both sites are English at the root and Japanese under /ja/, and are served by Cloudflare Pages from main.
- Each repository has delete branch on merge and one ruleset on the default branch: no deletion, no force push, a pull
  request with resolved threads (no required approvals, as the owner works alone), and Copilot review on push.
  cluewren also requires the CI test job.

Rejected:

- Five locales as on Framedash: every page would need five reviewed translations before the product has users.
- GitHub Pages: the domain's DNS is already at Cloudflare, and Framedash's sites use Pages there.
- An admin bypass on the ruleset, as framedash-www has: the owner asked for main to be protected.

## Consequences

- Every change to main, including the owner's, goes through a pull request.
- The Cloudflare Pages projects and their custom domains are created by the owner in the dashboard.
- Kit docs change in the code repository; the docs site's reference page only links to them.

## Amendment (2026-11-12)

On 2026-11-12 the owner made the two site repositories (crane-valley/cluewren-www and crane-valley/cluewren-docs)
private, matching Framedash's site repositories, which are private.

The code repository crane-valley/cluewren remains public. Both sites are still served by Cloudflare Pages from main.
The docs site's "Edit page" link was removed.
