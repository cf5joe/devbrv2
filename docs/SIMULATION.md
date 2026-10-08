# Simulated machines

Discovery (and, from Phase 4, restore) reads a computer only through `IMachine`: registry, filesystem,
known folders, Store packages and machine facts. `WindowsMachine` is this PC; `SimulatedMachine` is a
folder that describes another one. This lets DevBR be exercised against realistic developer setups and
a separate "target" computer without a second PC or VM.

## Fixture layout

```text
<machine folder>\
  machine.json     user, known folders, drives, junctions, inaccessible folders,
                   cloud placeholders, Store packages, file version resources
  registry.json    HKLM64 / HKLM32 / HKCU → key path → { value name: { kind, data } }
  fs\C\...         real files at their virtual Windows paths (C:\Users\alice\.gitconfig)
  fs\D\...
```

Junctions are reported as reparse points and never entered, inaccessible folders throw "access
denied", placeholders behave like cloud-only files, and executables are inert stand-ins whose versions
come from `machine.json` (DevBR never runs anything it discovers, real or simulated).

## Ready-made machines

| Machine | Purpose |
|---|---|
| `SampleMachines.DeveloperWorkstation` ("ALICE-DEV") | Every Phase 2 discovery scenario: both registry views, user and machine installs, Store apps, a portable VS Code, two Python versions, package managers, all 13 tool integrations, a `CODEX_HOME` override, secrets, repositories (standard, linked worktree, submodule, bare, alternates, LFS, stash, locked, credentials in a remote URL), another user's profile, a junction, an inaccessible folder and a cloud placeholder |
| `SampleMachines.CleanTarget` ("NEW-LAPTOP") | A freshly set-up PC with only Git and VS Code: the restore target for Phases 4–5 |

Restoring to a simulated machine writes into its fixture folder and `registry.json`. The administrator
prompt is simulated with a Yes/No question (the approved-effects check is the same one the real broker
performs), and installations leave the traces a real installer would (an Add/Remove Programs entry and
executable, or an `extensions.json` entry) without downloading anything.

Build your own with `SimulatedMachineBuilder` (see `tests/DevBR.Tests/Discovery`). Its `osBuild` argument
sets the Windows build (empty simulates one that cannot be read); `tests/DevBR.Tests/Restore/AdapterSupportTests.cs`
builds machines with every adapter's host at verified, outdated and unreadable versions.

## Using them in the app (development builds only)

- **Settings → Simulated machines**: create the sample workstation or clean target, open any machine
  folder, or switch back to this computer. The sidebar shows a *Simulated machine* badge while one is active.
- Command line: `DevBR.exe --machine sample`, `--machine clean`, or `--machine <folder>`; add
  `--open-backup <file.devbr>` to open a backup on the Restore page directly.

Simulations are stored under `%LOCALAPPDATA%\DevBR\simulations`. Each machine keeps its own discovery
catalog and backup selection.

## What simulation cannot replace

The two-machine acceptance report (Phase 5/6) still needs a real second Windows installation: real
UAC prompts, a real second user profile, real registry and environment broadcasts, installers, and
known-folder redirection. Simulation covers logic and safety; the VM covers the operating system.
