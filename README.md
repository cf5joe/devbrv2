# DevBR

Portable Windows 11 x64 application that discovers a developer's environment, creates a selective
backup, and restores supported configuration and personal development assets on another computer.
The full product plan is in [devbr-plan.md](devbr-plan.md).

**Status:** Phases 1 (foundation and portable shell), 2 (discovery and migration catalog) and 3 (backup
creation and archive inspection) are implemented. Restore planning and execution follow in Phases 4–5.
The archive format is described in [docs/ARCHIVE-FORMAT.md](docs/ARCHIVE-FORMAT.md). Discovery and backup can run
against simulated machines; see [docs/SIMULATION.md](docs/SIMULATION.md).

## Solution layout

| Project | Role |
|---|---|
| `src/DevBR.Domain` | Domain models from the plan (inventory, artifacts, manifest, restore operations, events) |
| `src/DevBR.Application` | Contracts: discovery providers, migration adapters, archive service, restore executor, settings |
| `src/DevBR.Discovery` | Discovery engine, providers (registry, Store, App Paths, Start menu, PATH, package managers, environment, filesystem), 15 tool adapters, known-tool catalog |
| `src/DevBR.Backup` | Backup planner (exclusions that never drop Git-tracked content, overlaps, findings), staging runner with redaction and secret detection, backup reader |
| `src/DevBR.Simulation` | Fixture-backed simulated machines and the sample workstation / clean target |
| `src/DevBR.Infrastructure` | App paths, settings, SQLite state, activity and discovery catalog, logging, real Windows machine, worker host, broker launcher |
| `src/DevBR.Ipc` | Versioned named-pipe protocol: framing, ACLs, OS peer verification, handshake, allow-listed dispatch |
| `src/DevBR.Archive` | 7z (LZMA2, non-solid, AES-256 + header encryption) via SharpSevenZip; safe extraction |
| `src/DevBR.ArchiveWorker` | Unelevated worker process; the only process that loads the native 7-Zip library |
| `src/DevBR.Broker` | Elevated broker skeleton with a narrow operation set; refuses unapproved plans |
| `src/DevBR.App` | WPF/MVVM desktop shell (`DevBR.exe`): navigation, themes, Restore inspection, diagnostics |
| `tests/DevBR.Tests` | xUnit v3 tests: archive security, IPC/broker rejection, worker crashes, contrast, state |

## Build, test, run

Requires the .NET 10 SDK (pinned in `global.json`).

```powershell
dotnet build DevBR.slnx
dotnet test --project tests/DevBR.Tests
dotnet run --project src/DevBR.App
```

## Portable package

```powershell
./build/publish.ps1                    # Development build -> artifacts/DevBR-<version>-win-x64-dev.zip
./build/publish.ps1 -Channel Release   # Release channel (must then be Authenticode-signed)
```

The output folder contains `DevBR.exe`, `DevBR.ArchiveWorker.exe`, `DevBR.Broker.exe`, the bundled .NET
runtime, `x64\7z.dll`, notices and usage notes. A `.sha256` checksum is written next to the ZIP.

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
