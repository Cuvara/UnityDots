# Releasing and adopting `com.cuvara.dots`

How a change to this package reaches a game. Every step is a place where "it worked on my machine"
has, at some point, been the only evidence — this page is the list of what replaces that.

## 1. The two repositories

| | Package (`Cuvara/UnityDots`) | Consumer (the Unity client) |
|---|---|---|
| Holds | source, tests, CI, `CHANGELOG.md`, `package.json` | `Packages/manifest.json` + `Packages/packages-lock.json` naming one tag/commit of the package |
| Changes by | branch → PR → merge → **tag** | manifest **and** lock bumped together, in one commit |
| Never | edits `Library/PackageCache` | edits `Library/PackageCache` |

`Library/PackageCache/com.cuvara.dots@<hash>/` is a download, not a source tree. A fix typed into
it works until the next resolve and is then gone, and nothing records that it existed. Fixes go to
the package repository and arrive by release.

## 2. Shipping a package change

1. **Branch** off `main` (`feat/…`, `fix/…`). Every change carries a `CHANGELOG.md` entry under
   `## [Unreleased]` and, when it alters behaviour, an update to the `Documentation~` page that
   describes it. Every new file has a `.meta` — `validate` fails without one.
2. **Assembly gates.** Optional code lives in its own assembly with `defineConstraints` +
   `versionDefines` naming the package it needs (`Runtime.Physics`, `Runtime.DI`, …). The core
   assembly references nothing optional. A new optional dependency means a new assembly, a new
   gated test assembly, and a CI row where the gate is satisfied *and* rows where it is not — see
   `SUPPORT-MATRIX.md §4`.
3. **PR** into `main`. CI must be green on the merge commit: six Unity rows, each with its
   inventory and floors, plus `validate`. A red row is a finding, not an obstacle — do not feed a
   row the dependency the package failed to declare.
4. **Version.** Bump `package.json › version` (semver: breaking → major once ≥ 1.0, minor before;
   additive → minor; fixes → patch) and rename `## [Unreleased]` to `## [X.Y.Z] - YYYY-MM-DD` in
   `CHANGELOG.md`, **in the commit that will be tagged**. `release.yml` refuses a tag whose
   `package.json` disagrees with it, and extracts the release notes from that heading —
   `release-reminder.yml` warns on every push to `main` while the version has no tag.
5. **Migration notes.** Anything a consumer must change goes under `### Migration` in the
   version's changelog section, with the old and new call side by side (0.28.0's
   `new ViewConfigRef { Index }` → `catalog.CreateRef(index)` is the shape).
6. **Tag** `vX.Y.Z` on that commit and push the tag. `release.yml` creates the GitHub Release and
   publishes `@cuvara/dots@X.Y.Z` to GitHub Packages. `npm publish` cannot be undone: tag once,
   after CI is green on exactly that commit.

## 3. Adopting a release in the consumer

1. In `Packages/manifest.json`, change the `#vX.Y.Z` (or commit hash) on `com.cuvara.dots`.
2. Let the Editor resolve, **then commit `Packages/packages-lock.json` in the same commit**. The
   lock pins the resolved commit hash; a manifest-only bump with a stale lock is silently ignored
   by other machines and by CI, which keep resolving the old hash. Same rule for
   `com.cuvara.netcode` and `com.rpgmmo.shared-gamelogic`.
3. Follow the version's `### Migration` notes; build the client; run its tests.
4. **Clean-consumer check** before merging the bump: the package's own CI rows are this check for
   the package alone, and the client's CI (fresh checkout, no `Library/`) is it for the client.
   A green Editor on a machine that already had the previous version in `Library/PackageCache`
   proves nothing about resolution.

### Rollback

Restore the previous **known package set** — `manifest.json` and `packages-lock.json` together,
from the last commit where the client was green — and let the Editor resolve. Never "fix forward"
by editing `Library/PackageCache`, and never roll back one of the three Cuvara/RPG packages alone
when the changelog says they moved together (netcode and shared-gamelogic pins have broken each
other twice; see the header of `.github/workflows/ci.yml`).

## 4. Compatibility evidence

A statement that version X of this package works with version Y of a dependency is recorded, not
remembered. The record is:

```
package:        com.cuvara.dots v0.28.0  (tag → commit 8b5822f)
unity:          6000.3.9f1
netcode:        com.cuvara.netcode v0.31.0
shared-gamelogic: sgl-v0.3.0 (package CI) / sgl-v0.3.1 (client lock)
unity.physics:  1.4.7          vcontainer: 1.16.9      messagepipe: 1.8.1      unitask: 2.5.10
evidence:       CI run <url> — rows: core-only, netcode-absent, netcode-present, physics, full-stack, gamefoundation
                client build <url> — platform, scripting backend, stripping level
```

Where it lives: the version's section in `CHANGELOG.md` (one line per moved pin) and
`SUPPORT-MATRIX.md §4` (the row table). A pin that moved without that line is undocumented and
should be treated as untested. Platform claims beyond the Editor rows (Android IL2CPP, WebGL)
need the device/build evidence `SUPPORT-MATRIX.md §5` describes — none exists today.

## 5. Checklist

Package PR: `[Unreleased]` entry · docs updated · `.meta` for every new file · gated assembly if
a new optional dependency · CI green in all rows · floors raised if a row's count no longer can
fail.

Release: `package.json` bumped · `## [X.Y.Z]` heading with date · `### Migration` if needed ·
compatibility line for every moved pin · tag on the green commit · release workflow green.

Consumer bump: manifest **and** lock · migration notes applied · fresh-checkout CI green ·
rollback = restore both files, never touch `Library/PackageCache`.
