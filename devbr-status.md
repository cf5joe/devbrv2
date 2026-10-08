# DevBR — Project Status

**As of:** 2026-10-08 · **Branch:** `main` · **Plan:** [devbr-plan.md](devbr-plan.md)

## Summary

Phases 1–5 of the six-phase plan are implemented. **Phase 6 (hardening, performance, release) is about
80% complete in code**. What remains is mostly work that needs a real second Windows machine, a code-signing
certificate, or a product decision.

| Metric | Value |
|---|---|
| Build | `dotnet build DevBR.slnx`: 0 warnings (warnings are errors) |
| Unit/integration tests | **487 passing** (`dotnet test --project tests/DevBR.Tests`); was 205 at the end of Phase 5 |
| UI automation tests | **7 passing** (opt-in: `$env:DEVBR_UI_TESTS='1'; dotnet test --project tests/DevBR.UiTests`) |
| Portable dev package | `build\publish.ps1` + `build\verify-release.ps1`: pass (69.7 MB ZIP, unsigned dev build) |

## Phase status

| Phase | Status | Notes |
|---|---|---|
| 1 Foundation and portable shell | ✅ Code complete · ⏳ VM check | Clean-VM launch without .NET, and 150%/200% scaling, not yet checked on real hardware |
| 2 Discovery and migration catalog | ✅ Complete | Tested against simulated machines (`docs/SIMULATION.md`) |
| 3 Backup and archive inspection | ✅ Complete | |
| 4 Restore planning and preflight | ✅ Complete | |
| 5 Restore execution and recovery | ✅ Code complete · ⏳ real-Windows check | Two-profile migration, real UAC, real installers and real Git worktrees/submodules not yet run |
| 6 Hardening, performance, release | 🟡 Mostly done | See "Remaining work" |

## Completed this session (2026-10-08)

### Post-Phase 5 review fixes
- Restore cancellation during extraction, the admin prompt or an install now finishes as *Cancelled* and writes the report (the job used to stay "Running" and the error reached the UI).
- The rollback store streams copies in 1 MB chunks instead of buffering whole files.
- Interrupted restores are announced at startup with a **Review** action.
- The worker crash handler no longer reads a disposed `Process`.

### Phase 6 workstreams (all merged into `main`)
| Workstream | Delivered |
|---|---|
| **Integrations** | Every one of the 15 adapters declares an `AdapterSupport` descriptor (verified version ranges, locations, capabilities, prerequisites). An unknown or unverified host version falls back to whole-file restore (environment variables: inventory only), with an `unverified-version:<tool>` preflight warning. Supported, outdated and unknown-version fixtures for every adapter. `docs/INTEGRATIONS.md` is generated from the descriptors, and a test fails if they drift. |
| **Security** | 189 adversarial test cases: hostile entry names, links/junctions, zip-bomb sizes, tampered or oversized indexes, credential leakage (logs, command lines, errors, reports), malformed IPC frames, replays, broker approval tampering. Fixed: link entries and junction writes in extraction; unbounded index line reads; index totals not checked against the manifest; the broker accepting values not covered by the approved prior state; broker sessions not ending after a refused request. |
| **UI and accessibility** | 527 screen-reader naming problems → 0. Glyphs hidden from screen readers. Keyboard focus on the sidebar. Progress updates throttled to about 10 per second, with ETA from measured throughput only. "Cancelling…" shown immediately. Remaining UI-thread work moved off. FlaUI suite in `tests/DevBR.UiTests`. `DEVBR_DATA_ROOT` state override (dev builds only). `docs/ACCESSIBILITY.md`. |
| **Release** | `publish.ps1`: locked restore, vulnerability audit (fails on findings), optional `signtool` signing (thumbprint or PFX plus timestamp). Release channel refuses unsigned output unless `-AllowUnsigned`. CycloneDX 6.2.0 SBOM (pinned in `.config/dotnet-tools.json`), `SHA256SUMS`, `release-info.json`, bundled licenses. `verify-release.ps1` checks checksums, required files, signatures, channel/version consistency, SBOM, and makes no network connections on launch. Guides: `MIGRATION-GUIDE`, `TROUBLESHOOTING`, `ADAPTER-DEVELOPMENT`, `ACCEPTANCE-CHECKLIST`. |
| **Scale** | Restore streams created and replaced files (64 MB test: 337 MB → under 32 MB of heap growth). Merges, rewrites and JSON validation capped at 8 MB. The entry index is written while staging, and redaction reads are bounded. Worker IPC sends large extraction lists through spool files (the 1 MB frame limit previously broke restores of a few thousand files). Opening a backup is proven never to extract payload. `tools/DevBR.Bench` benchmark; results in `docs/PERFORMANCE.md`. |

## Remaining work

### Needs a decision
1. **Archive worker memory vs speed.** The worker peaked at **2.37 GB** compressing large incompressible files (plan gate: under 2 GB). Capping 7-Zip threads or memory meets the gate but makes compression **1.7–3.7× slower** (measurements in `docs/PERFORMANCE.md`). Options: cap always, cap only above a size threshold, or make it a setting.
2. **Adapter version ranges.** The verified ranges in the descriptors (for example VS Code ≥ 1.90 and < 2.0, GitHub CLI ≥ 2.40 and < 3.0, Copilot CLI ≥ 0.0.330) are estimates. Confirm them against real tool versions before release.

### Needs engineering (can be done in a coding session)
3. **Restore memory at 1M files.** It grows about 9 KB per file (extrapolated to about 9 GB for one million files). Restore custom folders as one folder-level operation (as repositories already are) instead of one planned operation per file.
4. **Worker per-file memory during backup.** About 5 KB per file. Reduce the per-entry bookkeeping in the worker for million-file archives.
5. **Journal throughput.** About 1.3 ms of disk syncing per restored file (about 22 minutes for one million files). Consider batching journal commits per artifact while still recording each intent before its effect.
6. **Small-file throughput.** Each file is written twice on backup (staging) and twice on restore. Endpoint protection makes file creation the main cost (about 560 creates/s). Investigate staging-free capture for files with no transform.
7. **Untested edge cases** found by the security work: disk filling up mid-extraction (only the up-front space check is tested); an index up to 256 MB is extracted before its size check; replaying an already-completed broker request is not blocked (it writes nothing).
8. **CI.** No `.github/workflows` yet. Add build plus unit tests on `windows-latest`; keep UI tests and the bench manual.

### Needs real hardware or credentials (manual)
9. **Clean-VM acceptance** (Phase 1 gate): launch the portable ZIP on Windows 11 x64 without .NET, offline, and from a removable drive; at 100%, 150% and 200% scaling; with high contrast; and with Narrator. Follow `docs/ACCEPTANCE-CHECKLIST.md` and `docs/ACCESSIBILITY.md`.
10. **Two-machine / two-profile migration** (Phase 5 gate and the release acceptance report): real UAC (approve and decline), the broker's machine environment changes, WinGet/extension installs, repositories with branches, stashes, worktrees, submodules and LFS, interrupted restore and rollback, and an identical second restore making no changes.
11. **Full-scale validation**: 100 GB and one million files on the documented 16 GB RAM SSD reference machine (`docs/PERFORMANCE.md` explains how to run it).
12. **Code signing**: obtain an Authenticode certificate, install the Windows SDK `signtool`, then run `.\build\publish.ps1 -Channel Release -CertificateThumbprint <thumbprint>` followed by `verify-release.ps1`.

## Recommended next session

1. Decide on items 1–2 (worker memory policy and version ranges). These are quick and unblock release.
2. Implement item 3 (folder-level restore of custom folders), then re-run the benchmark (`tools/DevBR.Bench`) at 200,000 files to confirm restore memory is flat.
3. Add CI (item 8) so both remotes show build and test status.
4. Prepare a Windows 11 VM (Hyper-V or Windows Sandbox for the clean-launch check; a VM with two user accounts for migration). Run `docs/ACCEPTANCE-CHECKLIST.md` and record results there.
5. Once signing is available, cut a signed `0.1.0` release candidate and attach the ZIP, SBOM and `SHA256SUMS`.

## Working notes for future sessions

- **Remotes:** `origin` = `github.com/cf5joe/devbrv2` (personal account `cf5joe`); `work` = `github.com/Joseph-Church_publix/DevBR2` (work account, an Enterprise Managed User, so it cannot fork or open PRs on outside repos).
- **Pushing:** commit directly to `main`, then `git pushall` (a local alias: pushes `main` to `origin`, then `work`). This repo's local git config has a credential helper for each remote URL that runs `gh auth token --user <account>`, so both pushes work whichever `gh` account is active. Both accounts must stay signed in to `gh`. The alias and helpers live in `.git/config`, so a fresh clone must set them up again.
- **Simulation:** development builds accept `--machine sample|clean|<folder>` and `--open-backup <file>`; `DEVBR_DATA_ROOT` isolates app state for testing.
- **Docs regeneration:** `$env:DEVBR_UPDATE_DOCS='1'; dotnet test --project tests/DevBR.Tests --filter-class "*IntegrationMatrixTests"`.
- **Parallel work:** independent workstreams ran well in separate `git worktree`s merged back to `main`. Give each agent absolute worktree paths; some wrote to the main checkout by mistake.
