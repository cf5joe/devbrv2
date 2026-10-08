# Acceptance checklist: clean-VM launch and two-machine migration

This is the manual acceptance pass for the gates in [devbr-plan.md](../devbr-plan.md) that need real
Windows: a clean VM, real UAC, two distinct user profiles, the real registry and environment broadcast,
real installers and known-folder redirection. Automated tests and simulated machines cover the logic;
this checklist covers the operating system.

Fill in the results tables and keep the completed file (or a copy) with the release as the
**two-machine migration acceptance report**.

## Report header

| Field | Value |
|---|---|
| DevBR version and ZIP name | |
| ZIP SHA-256 (from `SHA256SUMS`) | |
| `verify-release.ps1` result (attach output) | |
| Automated tests (`dotnet test --project tests/DevBR.Tests`: passed / total) | |
| Tester | |
| Date | |
| Source machine (name, Windows build, RAM, disk) | |
| Target machine (name, Windows build, RAM, disk) | |
| Source user profile / target user profile | |

Result codes: **P** pass, **F** fail (open an issue and link it), **N/A** with a reason.

## 0. Prepare

1. Build: `./build/publish.ps1 -Channel Release -CertificateThumbprint <thumbprint>` (or a
   development build for a dry run).
2. Validate: `./build/verify-release.ps1 artifacts/DevBR-<version>-win-x64.zip`. All checks must pass.
3. **VM A (clean)**: fresh Windows 11 x64, fully updated, no .NET SDK or runtime, no developer tools.
   Confirm: `Get-ChildItem 'C:\Program Files\dotnet' -ErrorAction SilentlyContinue` returns nothing and
   `dotnet` is not recognized. Take a snapshot.
4. **Source machine S**: a real developer setup (VS Code, Git, GitHub CLI, PowerShell 7, Windows
   Terminal and at least two AI tools such as Copilot CLI and Claude Code), a user **with a different
   user name** from the target, at least three repositories: one with an extra branch, a stash, a
   modified tracked file and an untracked file; one with a linked worktree; one with a submodule. Add a
   custom user environment variable and a user PATH entry, one machine-level custom variable, and a
   project under `C:\Projects`.
5. **Target machine T**: another Windows 11 installation (or VM B) with a different user name, Git and VS
   Code installed, a `D:` drive (or a second VHD), and an existing VS Code `settings.json` with at least
   one value that conflicts with the source.

## 1. Clean-VM portable launch (Phase 1 and Phase 6 "Clean deployment")

| # | Step | Expected | Result | Notes |
|---|---|---|---|---|
| 1.1 | On VM A, **disconnect the network** (VM settings: no adapter). Copy the ZIP in, check `Get-FileHash` against `SHA256SUMS`, extract to `C:\Tools\DevBR` | Hash matches | | |
| 1.2 | Right-click `DevBR.exe` → Properties → Digital Signatures | Release: valid signature from the expected publisher, timestamped. Dev: none | | |
| 1.3 | Run `DevBR.exe` | Starts without installing anything; no .NET prompt; SmartScreen behaviour recorded | | |
| 1.4 | Observe the first screen and Task Manager for 60 s | Overview shows **Discover this computer** and **Open a backup**; no discovery runs; no disk activity beyond `%LOCALAPPDATA%\DevBR` | | |
| 1.5 | With the network connected again and Resource Monitor → Network open, restart DevBR | No network activity from `DevBR.exe` or `DevBR.ArchiveWorker.exe` at startup | | |
| 1.6 | Copy the folder to a USB drive and launch from it | Starts; state still under `%LOCALAPPDATA%\DevBR` | | |
| 1.7 | Sidebar badge | Release: no **Development build** badge. Dev: badge shown | | |
| 1.8 | **Settings → Diagnostics → Run self-test** | All steps **Pass** | | |
| 1.9 | **Settings → Diagnostics → Simulate worker failure**, then **Check worker** | Banner **Background archive process stopped**; GUI stays open; next check succeeds | | |
| 1.10 | **Settings → Privileged helper → Test privileged helper**, decline UAC; repeat and approve | Decline: reported, no crash. Approve: helper responds | | |
| 1.11 | Themes: Graphite, Midnight, Plum, Light, Follow Windows; change accent | All readable; accent text stays legible | | |
| 1.12 | Turn on Windows high contrast (Left Alt+Left Shift+Print Screen) | **High contrast is on** shown; UI usable | | |
| 1.13 | Display scaling 100 %, 150 %, 200 % (Settings → Display) | No clipped or overlapping content | | |
| 1.14 | Unplug the mouse: Tab/Shift+Tab, Enter/Space, Ctrl+1…Ctrl+6 across all pages | Every action reachable; focus always visible | | |
| 1.15 | Narrator (Win+Ctrl+Enter) on Overview, Discovery and Restore | Buttons, lists and status changes are announced with names | | |
| 1.16 | Broker rejection (automated in `tests/DevBR.Tests/Ipc/BrokerTests.cs`); record the test run | Tests pass | | |

## 2. Source machine: discover and back up (Phases 2–3 on real Windows)

| # | Step | Expected | Result | Notes |
|---|---|---|---|---|
| 2.1 | **Discover this computer → Start discovery**; during the scan press **Cancel** once, then run again | Cancel acknowledged immediately; UI responsive throughout; counts update **By drive** | | |
| 2.2 | Compare **Inventory** with Add/Remove Programs, Store apps and the tools in step 0.4 | Installed tools found with evidence; duplicates merged; no program launched (check Task Manager) | | |
| 2.3 | **Coverage** tab | Other users' profiles listed as skipped; junctions **Not entered**; inaccessible folders listed | | |
| 2.4 | **Backup selection** defaults | Only inventory and non-secret environment preselected | | |
| 2.5 | Select editor, AI tool, Git, terminal, PowerShell settings, the three repositories, the custom variables and PATH entry; add `C:\Projects\notes` via **Add folder…** | Selection summary correct | | |
| 2.6 | Include one credential (for example GitHub CLI hosts) | **Encryption will be required**; backup cannot continue unencrypted | | |
| 2.7 | Keep VS Code open with a file being written; run the backup with a password | **Backup created and verified** (or with warnings naming locked files) | | |
| 2.8 | Try saving to a FAT32 USB drive with a backup that could exceed 4 GB | Refused with the FAT32 message | | |
| 2.9 | Run a second backup to the same file name without ticking replace | Not overwritten | | |
| 2.10 | Record: files, bytes, archive size, duration, peak working set of `DevBR.exe` and `DevBR.ArchiveWorker.exe` (Task Manager → Details → Peak working set) | Peak < 2 GB | | |

## 3. Target machine: inspect, preflight, restore (Phases 4–5 on real Windows)

| # | Step | Expected | Result | Notes |
|---|---|---|---|---|
| 3.1 | Copy the backup to T. **Open a backup**, enter a wrong password, then the right one | Wrong: "That password did not open this backup…". Right: **Backup overview** shown; nothing extracted (check scratch folder) | | |
| 3.2 | Select all items, **Run preflight** | Nothing changes on T (compare `settings.json` hash, `HKCU\Environment`, PATH before/after) | | |
| 3.3 | **Destinations**: source profile → target profile; **Change…** `C:\Projects` → `D:\Projects` | Mappings scoped; no other `C:` paths rewritten (check **Paths rewritten for this computer**) | | |
| 3.4 | Findings | Missing host apps (for example Claude Code) give install instructions; sign-in steps listed; MCP servers with unsupported commands remain manual | | |
| 3.5 | Conflicting VS Code setting | Default **Merge (keep my values)** keeps T's value | | |
| 3.6 | **Approve this plan**, then change a decision | Approval invalidated when new effects appear | | |
| 3.7 | Start VS Code on T, **Recheck** | Running-application finding; close it, **Recheck** clears it | | |
| 3.8 | **Restore now**; **decline** the UAC prompt | User-level items restored; machine variable reported **Blocked** | | |
| 3.9 | Results | Statuses distinguish **Restored** from **Restored · verified working**; **Open report** shows HTML; JSON in `%LOCALAPPDATA%\DevBR\reports` contains no setting values | | |
| 3.10 | Repositories in a new terminal: `git status`, `git branch -a`, `git stash list`, `git worktree list`, `git submodule status` | Branches, stash, modified and untracked files present; worktree and submodule metadata valid at the mapped location | | |
| 3.11 | `reg query HKCU\Environment` and `reg query "HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment"` | User variables only in HKCU; machine scope untouched (UAC declined); `REG_EXPAND_SZ` preserved | | |
| 3.12 | Plan again with only the machine variable, approve UAC | Applied to HKLM only; new terminal sees it | | |
| 3.13 | Run the **same restore again** | No duplicate PATH entries or settings; items show as unchanged | | |
| 3.14 | Settings in the restored tools (VS Code, Windows Terminal, Copilot/Claude MCP) | Present; comments in JSONC preserved; tools start | | |

## 4. Recovery (Phase 5)

| # | Step | Expected | Result | Notes |
|---|---|---|---|---|
| 4.1 | Start a large restore; while **Restoring…**, end `DevBR.exe` in Task Manager | — | | |
| 4.2 | Start DevBR again | **A restore was interrupted** with **Review**; each change made / not made / not confirmed; nothing re-run | | |
| 4.3 | **Recent restores → Roll back** | File and environment changes undone newest first; changes made after the restore kept and listed; installs not removed | | |
| 4.4 | Edit a restored file, then roll back another restore that touched it | Edited file kept and reported | | |
| 4.5 | Restore to a nearly full drive (or a small VHD) | Preflight blocks for space; no partial writes | | |
| 4.6 | Restore while a target file is held open without sharing (in PowerShell: `$h = [IO.File]::Open($path, 'Open', 'Read', 'None')`) | That item fails and is undone; others continue | | |

## 5. Performance reference (Phase 6)

Record on the documented reference machine (16 GB RAM, SSD) with release builds.

| Workload | Files | Bytes | Backup time | Throughput | Restore time | Peak working set (GUI / worker) | Cancel acknowledged within | Result |
|---|---|---|---|---|---|---|---|---|
| Many small files (≈1,000,000) | | | | | | | | |
| Incompressible (≈100 GB) | | | | | | | | |
| Typical developer profile | | | | | | | | |

## Sign-off

| Gate | Result | Evidence |
|---|---|---|
| Portable ZIP launches on a clean Windows 11 x64 VM without .NET installed (1.1–1.7) | | |
| Startup performs no discovery or network access (1.4–1.5) | | |
| Theme switching, keyboard navigation, scaling and high contrast work (1.11–1.15) | | |
| Worker failures are reported without crashing the GUI (1.9) | | |
| Broker rejects unauthorized or malformed requests (1.10, 1.16) | | |
| Representative migration succeeds between two distinct Windows user profiles (section 3) | | |
| Repositories retain branches, stashes, local changes and untracked files (3.10) | | |
| Linked worktrees and submodules retain valid metadata after mapping (3.10) | | |
| User and machine environment scopes remain separate (3.11–3.12) | | |
| UAC refusal does not prevent independent user-level restoration (3.8) | | |
| Interrupted restores recover without silently repeating uncertain operations (4.1–4.2) | | |
| A second identical restore does not duplicate PATH entries or settings (3.13) | | |
| Reports distinguish copied configuration from verified functionality (3.9) | | |
| Signed production executables and published checksums (0.2, 1.2) | | |
| Memory stays bounded (peak < 2 GB) on the scale fixture (section 5) | | |

Accepted by: ____________________  Date: ____________
