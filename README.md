# DevBR

Portable Windows 11 x64 application that discovers a developer's environment, creates a selective
backup, and restores supported configuration and personal development assets on another computer.
The full product plan is in [devbr-plan.md](devbr-plan.md).

**Status:** Phases 1 (foundation and portable shell), 2 (discovery and migration catalog), 3 (backup
creation and archive inspection), 4 (restore planning and preflight) and 5 (restore execution, verification,
rollback and recovery) are implemented. Hardening and the two-machine acceptance pass follow in Phase 6.
The archive format is described in [docs/ARCHIVE-FORMAT.md](docs/ARCHIVE-FORMAT.md). Discovery and backup can run
against simulated machines; see [docs/SIMULATION.md](docs/SIMULATION.md). Supported integrations, their
verified versions and the unknown-version fallback are listed in [docs/INTEGRATIONS.md](docs/INTEGRATIONS.md).

## Solution layout

| Project | Role |
|---|---|
| `src/DevBR.Domain` | Domain models from the plan (inventory, artifacts, manifest, restore operations, events) |
| `src/DevBR.Application` | Contracts: discovery providers, migration adapters, archive service, restore executor, settings |
| `src/DevBR.Discovery` | Discovery engine, providers (registry, Store, App Paths, Start menu, PATH, package managers, environment, filesystem), 15 tool adapters, known-tool catalog |
| `src/DevBR.Backup` | Backup planner (exclusions that never drop Git-tracked content, overlaps, findings), staging runner with redaction and secret detection, backup reader |
| `src/DevBR.Restore` | Preflight and planning (path mapping, merge previews, MCP and dependency analysis, package recipes, plan approval) and execution: journaled per-item commits, comment-preserving JSONC merges, repository restore, verification levels, HTML/JSON reports, rollback and crash recovery |
| `src/DevBR.Simulation` | Fixture-backed simulated machines and the sample workstation / clean target |
| `src/DevBR.Infrastructure` | App paths, settings, SQLite state and restore journal, DPAPI-protected rollback store, activity and discovery catalog, logging, real Windows machine and writer, WinGet/editor installer, worker host, broker launcher and elevation |
| `src/DevBR.Ipc` | Versioned named-pipe protocol: framing, ACLs, OS peer verification, handshake, allow-listed dispatch |
| `src/DevBR.Archive` | 7z (LZMA2, non-solid, AES-256 + header encryption) via SharpSevenZip; safe extraction |
| `src/DevBR.ArchiveWorker` | Unelevated worker process; the only process that loads the native 7-Zip library |
| `src/DevBR.Broker` | Elevated broker with a narrow operation set: applies machine environment changes only when they match an approved job's effects in the user's journal |
| `src/DevBR.App` | WPF/MVVM desktop shell (`DevBR.exe`): navigation, themes, Restore inspection, diagnostics |
| `tests/DevBR.Tests` | xUnit v3 tests: archive security, IPC/broker rejection, worker crashes, contrast, state |
| `tests/DevBR.UiTests` | Opt-in FlaUI UI automation against `DevBR.exe` on simulated machines (see [docs/ACCESSIBILITY.md](docs/ACCESSIBILITY.md)) |

## Build, test, run

Requires the .NET 10 SDK (pinned in `global.json`).

```powershell
dotnet build DevBR.slnx
dotnet test --project tests/DevBR.Tests
$env:DEVBR_UI_TESTS = '1'; dotnet test --project tests/DevBR.UiTests   # optional, drives the desktop
dotnet run --project src/DevBR.App
```

## Portable package and release

Requires PowerShell 7.2+ and network access to nuget.org (or a warm NuGet cache) for the tool and
package restore.

```powershell
./build/publish.ps1                                                    # Development build -> artifacts/DevBR-<version>-win-x64-dev.zip
./build/publish.ps1 -Channel Release -CertificateThumbprint <sha1>     # Signed release (certificate store)
./build/publish.ps1 -Channel Release -PfxPath devbr.pfx -PfxPassword (Read-Host -AsSecureString)
./build/publish.ps1 -Channel Release -AllowUnsigned                    # Unsigned release, named -unsigned
./build/verify-release.ps1 artifacts/DevBR-<version>-win-x64-dev.zip   # Validate a ZIP
```

`publish.ps1`:

1. restores the pinned local tools (`.config/dotnet-tools.json`) and all packages in locked mode
   (`packages.lock.json` must match), then fails if `dotnet list package --vulnerable --include-transitive`
   reports anything;
2. publishes `DevBR.exe`, `DevBR.ArchiveWorker.exe` and `DevBR.Broker.exe` self-contained for win-x64;
3. optionally Authenticode-signs `DevBR*.exe` and `DevBR*.dll` with `signtool` (SHA-256, RFC 3161
   timestamp from `-TimestampUrl`; signtool is found on PATH or in the Windows SDK, or pass `-SignToolPath`).
   A Release build refuses to produce a ZIP unless every DevBR binary has a valid signature, or
   `-AllowUnsigned` is passed, which names the ZIP `-unsigned` and adds `UNSIGNED-RELEASE.txt`;
4. writes a CycloneDX 1.6 SBOM with the pinned `CycloneDX` tool (NuGet packages plus the bundled .NET
   runtime packs, 7-Zip and hashes of DevBR's own binaries);
5. adds notices, license texts (`licenses\`), usage, migration and troubleshooting guides,
   `release-info.json` (version, channel, signed, commit) and an in-package `SHA256SUMS`;
6. writes to `artifacts\`: the ZIP, `<zip name>.cdx.json` (SBOM), `SHA256SUMS` (ZIP, SBOM and every
   executable, listed under the folder Explorer's *Extract All* creates) and the legacy `<zip>.sha256`.

`verify-release.ps1` checks the checksums, the required files, every file against the in-package
`SHA256SUMS`, the Authenticode status of the DevBR binaries (required for a signed Release), that the
channel and version in the file name, `release-info.json`, the SBOM and the assemblies'
`DevBR.ReleaseChannel` metadata agree, and that `DevBR.exe` starts and opens no sockets during the first
seconds (`-SkipLaunch` to skip). The fully offline clean-VM launch is a manual step in
[docs/ACCEPTANCE-CHECKLIST.md](docs/ACCEPTANCE-CHECKLIST.md).

Code signing needs a code-signing certificate (OV/EV or a cloud signing service) supplied by the
releaser; none is stored in this repository.

## Documentation

| Document | Audience |
|---|---|
| [docs/USAGE.md](docs/USAGE.md) | Quick start (shipped in the package) |
| [docs/MIGRATION-GUIDE.md](docs/MIGRATION-GUIDE.md) | End-to-end move from one PC to another (shipped) |
| [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md) | UAC, SmartScreen, locked files, passwords, space, FAT32, recovery, logs (shipped) |
| [docs/INTEGRATIONS.md](docs/INTEGRATIONS.md) | Supported tools and versions |
| [docs/ARCHIVE-FORMAT.md](docs/ARCHIVE-FORMAT.md) | The `.devbr` archive format |
| [docs/ADAPTER-DEVELOPMENT.md](docs/ADAPTER-DEVELOPMENT.md) | Adding a tool adapter |
| [docs/SIMULATION.md](docs/SIMULATION.md) | Simulated machines for development and tests |
| [docs/ACCEPTANCE-CHECKLIST.md](docs/ACCEPTANCE-CHECKLIST.md) | Clean-VM and two-machine acceptance report |
| [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) | Third-party components and licenses |

## Security model (Phase 1)

- The GUI runs unelevated and never loads the native archive library; archive work happens in a
  separate worker process that is restarted transparently if it crashes.
- IPC pipes are created as the first and only instance, ACL-restricted to the user, deny network
  access, verify the peer process ID/executable/user via the OS, and require a 32-byte handshake token
  (only its SHA-256 is ever placed on a command line).
- Requests are strictly typed and allow-listed; malformed or unknown requests are rejected, and the
  broker disconnects on the first rejected request.
- Archive entry names are validated before any write (traversal, absolute paths, drive letters,
  alternate data streams, reserved device names, case collisions), sizes are enforced while streaming,
  and every extracted file is hashed.
- Passwords are wrapped in `SecretText`, which never prints its value.
