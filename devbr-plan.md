# DevBR — Windows Developer Backup and Restore Blueprint

**Target document:** `C:\code\Projects\DevBR\devbr-plan.md`  
**Status:** Phases 1–5 implemented (see README and git history). Phase 6 (hardening, performance, release) in progress.

## 1. Product definition and confirmed decisions

Build **DevBR**, a portable Windows application that discovers a developer’s environment, creates a selective backup, and restores supported configurations and personal development assets onto another computer.

The primary journey is:

**Discover → Review → Select → Back up → Transfer → Inspect → Preflight → Restore → Verify**

DevBR inventories installed applications and provides reinstall guidance. It does not back up or migrate installed application binaries.

### Confirmed scope

| Area | First-release decision |
|---|---|
| Platform | Windows 11 x64 |
| Distribution | Portable ZIP; extract and launch without installing a runtime |
| Discovery | User-initiated; scans system inventory sources and all fixed local drives |
| Default backup selection | Machine/application inventory and nonsecret system environment data |
| Optional selections | User settings, AI configurations, skills, plugins, workflow files, repositories, custom files and folders |
| Integrations | VS Code Stable/Insiders, Copilot CLI/Chat, Codex, Claude Code/Desktop, Cursor, Gemini CLI, Git/GitHub CLI, PowerShell, Windows Terminal |
| Dependencies | Supported missing extensions, plugins, and runtimes can be installed after approval of a concrete restore plan |
| Main applications | User installs these separately |
| Repository backup | Local Git state, including history, branches, stashes, local edits, and untracked files; reviewable exclusions |
| Conflict default | Preserve existing conflicting values; merge supported nonconflicting settings; retain rollback copies |
| Scale | Validate against 100 GB of selected source data and one million files |
| Network | Local-first; no account, telemetry, cloud upload, or central service |
| Encryption | Unencrypted by default; mandatory when recognized secrets are explicitly included |
| Themes | Polished dark presets, light/system modes, and accent selection |
| Relocation | Profile mapping and selected root mapping; rewrite only recognized path fields |
| WSL/Docker | Inventory and supported Windows-side configuration only |
| Workflows | Files, scripts, skills, and hooks; no Scheduled Task integration |

### Explicit boundaries

- No automatic application installation, full registry restoration, OS imaging, driver migration, or security-policy migration.
- No WSL distribution, VM disk, Docker image, container, or volume export.
- No cloud synchronization, incremental backups, scheduled backups, or remote machine administration.
- No copying Windows Credential Manager stores, DPAPI master keys, browser login databases, or machine-bound authentication caches.
- No arbitrary execution of discovered scripts, MCP servers, plugins, hooks, or repository code during discovery or preflight.
- No promise that every unknown application can be identified or every configuration restored automatically.
- No AI model dependency. Detection and restoration use deterministic, inspectable rules.

## 2. Architecture, contracts, and distribution

### Technology choices

Use **C#/.NET 10 LTS, WPF, and MVVM**. Start with WPF’s Fluent controls and build a consistent application design system over them. .NET 10 provides an appropriate support horizon, and WPF supports Fluent light/dark styling. ([.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core), [WPF themes](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/controls/control-styles-and-templates))

Use:

- `CommunityToolkit.Mvvm` for presentation state and commands.
- `Microsoft.Extensions.Hosting` for dependency injection and application lifecycle.
- `Microsoft.Data.Sqlite` for local discovery catalogs and job journals.
- `System.Text.Json`, a JSONC-preserving editor, and Tomlyn for supported configuration formats.
- Serilog for structured, locally retained logs.
- SharpSevenZip with a bundled x64 7-Zip library, isolated behind an archive interface.
- xUnit for engine tests and FlaUI/UI Automation for desktop workflows.

Pin dependencies and the SDK in source control. Use locked restores, package vulnerability checks, and third-party license notices.

### Process and component boundaries

| Component | Responsibility |
|---|---|
| Desktop GUI | Navigation, selections, previews, progress, remediation, themes |
| Application layer | Discovery, backup, preflight, restore, verification, cancellation |
| Domain layer | Inventory, artifacts, requirements, plans, conflicts, results |
| Windows infrastructure | Registry, environment, installed packages, filesystem, process control |
| Integration adapters | Tool-specific detection, capture, merge, prerequisites, validation |
| Archive worker | Compression, archive inspection, extraction, integrity verification |
| Elevated broker | Narrow privileged discovery and approved system changes |
| Local storage | Catalog, settings, job state, redacted logs, rollback records |

The GUI normally runs without elevation. Archive work runs in a separate unelevated worker so malformed archives or native-library failures do not terminate the GUI.

Use authenticated, versioned named-pipe IPC. Restrict pipe access, validate peer identity, reject unexpected messages, and associate requests with a job and approved plan.

The elevated broker must expose specific operations, such as reading a protected inventory source or applying an approved machine environment change. It must not expose a general-purpose elevated shell or arbitrary registry writer.

### Core interfaces

Implement asynchronous, cancellable interfaces:

```csharp
IDiscoveryProvider.DiscoverAsync(context, cancellationToken)
IMigrationAdapter.DescribeArtifactsAsync(context, cancellationToken)
IMigrationAdapter.CaptureAsync(selection, context, cancellationToken)
IMigrationAdapter.PreflightAsync(selection, target, cancellationToken)
IMigrationAdapter.PlanRestoreAsync(selection, target, cancellationToken)
IMigrationAdapter.ValidateAsync(result, cancellationToken)

IArchiveService.CreateAsync(request, progress, cancellationToken)
IArchiveService.InspectAsync(request, cancellationToken)
IArchiveService.ExtractSelectedAsync(request, progress, cancellationToken)

IRestoreExecutor.ExecuteAsync(plan, progress, cancellationToken)
IRollbackService.RollbackAsync(job, cancellationToken)
```

Restore adapters produce declarative operations. The executor performs writes, privilege checks, journaling, and rollback consistently.

Adapters ship with DevBR. The first release does not dynamically load third-party adapter DLLs or execute rules supplied inside backup archives.

### Required domain models

| Model | Required information |
|---|---|
| `InventoryItem` | Stable ID, category, name/version, publisher, scope, locations, discovery evidence, confidence, adapter ID |
| `MigrationArtifact` | Owner/tool, artifact kind, roots, sensitivity, backup eligibility, restore capability, dependencies |
| `DiscoveryCoverage` | Source/volume, start/end, scanned counts, exclusions, inaccessible locations, errors |
| `BackupManifest` | Format version, app version, archive ID, source OS/architecture, creation time, totals, index hashes |
| `ArchiveEntry` | Artifact ID, relative path, entry type, size, timestamp, SHA-256, attributes or link metadata |
| `PathMapping` | Source logical root, target root, mapping origin, validation status |
| `PreflightFinding` | Severity, affected items, prerequisite, reason, specific remediation, recheck capability |
| `RestoreOperation` | Operation ID, dependencies, target, action, conflict decision, privilege, expected target state |
| `OperationEvent` | Job/stage/item, counters, bytes, message, severity, timing, ETA confidence |
| `RestoreResult` | Applied/skipped/failed/blocked status, verification result, rollback availability, next steps |

Distinguish:

- **Detected** from **confirmed installed**.
- **Backed up** from **inventory only**.
- **Restored** from **functionally verified**.
- **Warnings** from **blocking prerequisites**.

### Packaging and application state

Publish a self-contained `win-x64` folder ZIP containing the GUI, workers, broker, native libraries, notices, and usage documentation. No separately installed .NET runtime is required. ([.NET deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview))

- Support launching from a user-writable local folder or removable drive.
- Store application state under `%LOCALAPPDATA%\DevBR`, separately from program files.
- Support a user-selected local scratch directory for large operations.
- Do not install a service, scheduled task, shell extension, or file association.
- Updates are manual replacement of the application folder.
- Production releases use Authenticode signing and published checksums.
- Unsigned development builds remain usable but must be identified as development builds.
- Do not bypass SmartScreen, enterprise execution controls, or UAC.

## 3. User experience, discovery, and backup

### Visual design

Use a restrained Fluent-inspired interface with strong typography, generous spacing, and compact data views where appropriate.

Ship these presets:

- **Graphite**, default: neutral charcoal with blue accent.
- **Midnight:** deep navy with cyan accent.
- **Plum:** dark violet with lavender accent.
- **Light**
- **Follow Windows**

Represent colors as semantic resources: background, surface, elevated surface, border, text, muted text, accent, success, warning, and error. Accent selection must preserve readable foreground contrast.

Use Segoe UI Variable, an 8-pixel spacing scale, consistent corner radii, and subtle transitions. Respect reduced-motion and Windows high-contrast settings.

Require keyboard navigation, visible focus, screen-reader names, scalable text, and non-color status indicators. Validate at 100%, 150%, and 200% display scaling.

### Navigation and workflows

Use a persistent sidebar:

**Overview · Discovery · Backup · Restore · Activity · Settings**

The initial screen contains two primary actions:

- **Discover this computer**
- **Open a backup**

Launching DevBR must not start discovery or inspect drives automatically.

#### Discovery view

Provide:

- Scan scope and elevation status.
- Live counts by category and drive.
- Search, sorting, category filters, and scope filters.
- A details panel showing source evidence, confidence, paths, and migration support.
- A coverage report listing skipped and inaccessible areas.
- Selection controls that distinguish backup content from inventory records.

#### Backup wizard

1. Review inventory and select supported artifacts.
2. Add multiple custom files or folders.
3. Review exclusions, sensitive items, dependencies, and estimated size.
4. Choose output file, scratch location, compression, and optional encryption.
5. Review the exact capture plan.
6. Run backup and inspect the final report.

Default to selecting the system baseline only. Do not automatically select repositories or entire profile directories.

#### Restore wizard

1. Browse for a backup file.
2. Enter its password if encrypted.
3. Read its overview and select artifacts.
4. Configure destination mappings.
5. Run target preflight.
6. Review conflicts, dependencies, system changes, and downloads.
7. Approve and execute the plan.
8. Present validation results and remaining actions.

Opening an archive does not start restore or execute anything in it. Full target discovery is optional; targeted preflight is mandatory.

### Discovery engine

Use layered discovery rather than relying on one Windows inventory source.

**Installed software and system sources**

- HKLM and HKCU uninstall records in both registry views.
- Registered MSIX/AppX packages through supported Windows APIs.
- App Paths, Start Menu shortcuts, known installation roots, and PATH directories.
- Available package-manager metadata, including WinGet, Chocolatey, Scoop, npm, pip/pipx/uv, and .NET tools.
- User and machine environment variables, preserving raw values and registry value types.
- Selected system/runtime facts required for compatibility.

Do not use `Win32_Product`; enumeration can trigger installer consistency checks and repairs. ([Microsoft documentation](https://learn.microsoft.com/de-ch/previous-versions/windows/desktop/msiprov/win32-product))

**Filesystem pass**

Traverse all fixed local drives for recognized executable, repository, configuration, plugin, skill, and workflow signatures.

- Use bounded concurrency and lazy enumeration.
- Inspect file content only when a detector needs it.
- Never execute an unknown executable to identify it.
- Resolve duplicate observations without collapsing distinct installations or versions.
- Record evidence and confidence for heuristic matches.
- Do not follow directory junctions or symbolic links automatically.
- Do not enter virtual filesystem images or hydrate cloud placeholders automatically.
- Skip other users’ private profile contents by default.
- Exclude protected OS internals, recovery data, recycle bins, and DevBR’s own scratch/output.
- Report every excluded or inaccessible scope.

“Scan the whole system” means a comprehensive, reported search of accessible sources—not bypassing permissions or reading every registry value and file byte.

Known user-level roots and environment overrides must take precedence over assumed default locations. Allow users to add extra discovery roots.

### Integration coverage

| Adapter | Capture and restore behavior |
|---|---|
| Windows environment | Capture machine baseline and optional user variables; restore approved custom values and merged PATH entries |
| VS Code Stable/Insiders | Profiles, settings, keybindings, snippets, tasks, extension inventory, MCP and AI customization files |
| Copilot CLI/Chat | Version-aware settings, instructions, agents, skills, hooks, plugin references, local customizations, MCP definitions |
| Codex | User/project configuration, instructions, skills, local plugin sources and references, MCP configuration |
| Claude Code | Supported user/project settings, instructions, agents, skills, hooks, plugin references, MCP definitions |
| Claude Desktop | Supported local MCP configuration and extension inventory; unsupported internal state remains inventory/manual handling |
| Cursor | Supported editor settings, extension inventory, rules, skills, hooks, and MCP definitions |
| Gemini CLI | User/project configuration, custom commands, instructions, extensions, and MCP definitions |
| Git/GitHub CLI | Git configuration and includes, ignore/attributes files, nonsecret CLI preferences, repository inventory |
| PowerShell | Both Windows PowerShell and PowerShell 7 profiles, referenced scripts, module inventory, selected custom modules |
| Windows Terminal | Supported settings, schemes, and local assets |
| WSL/Docker | Installation/distribution inventory and supported Windows-side configuration; authentication redacted |
| Custom files/folders | Explicitly selected content with reviewed exclusions and destination mapping |

Each adapter must declare supported versions, locations, capabilities, prerequisite rules, and test fixtures. Unknown versions must fall back to inventory or explicit file restoration rather than unverified semantic edits.

VS Code profiles and MCP configurations have multiple valid locations and formats. Preserve their scopes instead of flattening them into a single configuration. ([VS Code profiles](https://code.visualstudio.com/docs/configure/profiles), [MCP reference](https://code.visualstudio.com/docs/agents/reference/mcp-configuration))

Copilot’s configuration directory separates settings, plugins, session data, and credential-related content; use selective capture. ([Copilot configuration reference](https://docs.github.com/en/copilot/reference/copilot-cli-reference/cli-config-dir-reference))

Codex authentication may be file-based or held in an OS credential store. Treat credential transfer as a separate explicit capability, never as part of copying ordinary settings. ([Codex authentication](https://learn.chatgpt.com/docs/auth))

Claude, Cursor, and Gemini adapters must preserve their documented user/project scopes and configuration precedence. ([Claude settings](https://code.claude.com/docs/en/settings), [Cursor MCP](https://prod.cursor.com/help/customization/mcp), [Gemini configuration](https://geminicli.com/docs/reference/configuration/))

### Repository and custom-content rules

Selected repositories include `.git`, working files, local branches, stashes, and untracked content.

- Do not use `.gitignore` as a blanket backup exclusion.
- Exclude recognized regenerable caches/build outputs through a visible, editable exclusion list.
- Never silently exclude tracked content.
- Detect bare repositories, submodules, linked worktrees, external Git directories, and object alternates.
- Include required external Git storage or block that repository’s “complete backup” status with specific remediation.
- Preserve Git objects verbatim; do not rewrite history to remove possible secrets.
- Record available Git LFS objects and disclose when additional downloads will be required.
- Restore repositories into empty/new destinations by default. Do not merge two `.git` directories.
- Detect active Git locks and require a stable repository during capture.

Custom folder selection must detect overlapping roots, avoid duplicate payloads, and prevent the archive or scratch directory from recursively including itself.

Installed applications and runtimes are not automatically captured. Developer-authored scripts and local plugin/MCP source projects are eligible personal content.

### Archive format and capture pipeline

Use one **`.devbr` file containing a standard 7z archive**.

Use LZMA2 compression with non-solid storage so selected items and indexes can be accessed without decompressing unrelated content. For encrypted backups, use 7z AES-256 encryption with header encryption. The native wrapper supports streaming and password-based operations. ([7z format](https://www.7-zip.org/7z.html), [SharpSevenZip](https://github.com/JeremyAnsel/SharpSevenZip))

Archive layout:

```text
manifest.json
inventory.ndjson
artifacts.ndjson
entries.ndjson
coverage.json
reports/backup-report.json
payload/<artifact-id>/<relative-path>
```

The manifest contains summary counts, format compatibility, source metadata, and hashes of the indexes. Entry records contain SHA-256 hashes of captured file bytes.

Version the archive contract independently of the application. Reject unsupported major versions before extraction. Optional fields can evolve within a major version.

Capture sequence:

1. Expand selections and calculate a file plan.
2. Apply exclusions and sensitive-item decisions.
3. Check free space for staging and a conservative archive-size bound.
4. Copy selected bytes into a private, access-restricted staging area while hashing.
5. Detect changed or unreadable source files and report affected artifacts.
6. Generate indexes from staged content.
7. Compress to a temporary file beside the chosen output.
8. Reopen the archive and verify indexes and payload hashes.
9. Rename to the final filename only after successful verification.
10. Clean staging and temporary output.

Streaming must avoid loading large files into memory. Staging intentionally trades temporary disk space for reproducible archive contents.

No VSS-based snapshot support in v1. Explain when applications must be closed. Never label a live, inconsistent repository or database capture as verified.

### Sensitive content

- Exclude recognized credentials by default.
- Offer individual inclusion of supported portable secrets.
- Require encryption when such secrets are selected or detected in selected content.
- Redact structured configuration fields where the adapter can do so safely.
- Omit opaque credential files instead of corrupting them through partial redaction.
- Do not log passwords, tokens, raw secret values, or authenticated URLs.
- Pass archive passwords in memory, never through command-line arguments.
- Exclude sessions, histories, caches, machine identities, and remembered trust approvals by default.

Show a concise disclosure for unencrypted backups: custom files and Git history can contain secrets that detection misses. Do not claim that “no known secrets found” means the archive is secret-free.

## 4. Restore engine, elevation, and operational behavior

### Archive inspection and validation

Read the manifest first to display:

- Source machine and OS.
- Backup date and DevBR version.
- Categories and artifact counts.
- Application inventory.
- File counts and uncompressed size.
- Encryption status.
- Known omissions and capture warnings.
- Available restoration capabilities.

Parse archive indexes as bounded data into a locally created catalog. Do not load executable plugins, SQL databases, scripts, or restoration commands from the archive.

Before writing any restored data:

- Validate archive signature, schema, sizes, counts, and index relationships.
- Reject traversal paths, absolute archive entry paths, duplicate/case-colliding destinations, reserved device names, and alternate data stream paths.
- Validate link targets and destination ancestry.
- Check extraction limits against actual available space.
- Extract selected content to private staging and verify its hashes.

Hashes detect corruption; they do not establish who created an archive. Treat every archive as untrusted input, including encrypted archives.

### Mandatory preflight

Preflight evaluates the selected items against the actual destination machine.

| Check | Required result |
|---|---|
| Application presence/version | Compatible host application or explicit manual installation instructions |
| Runtime/dependency availability | Resolved executable/package requirement or approved installation operation |
| Destination mapping | Valid writable target with no unresolved root conflicts |
| Disk capacity | Enough space for extraction, replacement, and rollback |
| Existing content | Explicit merge/keep/replace/alternate decision |
| Running applications/locks | Safe to modify or instructions to close/retry |
| Privilege | User-level, broker-required, or blocked |
| Credentials | Portable secret supplied or reauthentication required |
| Configuration compatibility | Supported schema/version or manual handling |
| Policy restrictions | Respect current organization policies and permitted install sources |
| Filesystem compatibility | Long paths, links, case collisions, and unsupported metadata identified |

Each finding must contain the problem, why it matters, affected artifacts, exact next steps, and a **Recheck** action.

Block only dependent items where possible. Let users proceed with independent ready items after an updated preview.

### Dependency restoration

Represent restore as a directed dependency graph:

**Host application → runtime → package/plugin → configuration → validation**

- Main applications remain manual prerequisites.
- Automatically install only supported dependencies included in the approved plan.
- Use adapter-defined recipes with validated package identities and sources.
- Prefer captured versions; if unavailable or incompatible, require a revised plan before substituting.
- Use existing supported package managers; do not bootstrap a new package manager automatically.
- Do not derive shell commands directly from arbitrary strings in MCP configurations.
- Distinguish npm/Python package references, local executable references, source projects, container references, and remote MCP endpoints.
- Do not run unknown build scripts or initialize arbitrary MCP servers to test them.
- Show download sources, versions, elevation, and package execution implications in the preview.
- Respect offline failures, proxies, authentication requirements, and enterprise restrictions without disabling certificate verification.

Installers may have effects DevBR cannot reverse. Mark those operations separately from rollback-capable file and environment changes.

### Configuration merging and relocation

Supported structured merges operate at named settings, profile, server, or extension identity—not through blanket text replacement.

Default rules:

- Identical values: no change.
- Missing destination values: add.
- Conflicting values: keep destination unless the user selects the backup value.
- Arrays and lists: use adapter-specific identity rules.
- Unsupported/binary files: keep existing, replace, or restore alongside.
- Preserve comments and formatting where supported.
- If safe editing cannot be guaranteed, present whole-file conflict handling.
- Do not override current enterprise-managed settings.

Logical roots include the source profile, roaming/local application data, explicitly selected custom roots, and repository roots. Map these to the target using Windows known-folder APIs and user-selected destinations.

For relocation:

- Apply longest matching root mappings at path boundaries.
- Rewrite only adapter-declared path fields and recognized Git metadata references.
- Preserve environment-variable expressions where valid.
- Show all transformed values in a preview.
- Flag unresolved absolute paths.
- Do not rewrite arbitrary scripts, Git objects, binaries, or unknown configuration values.
- A destination such as `D:\Projects` must not imply replacing every occurrence of `C:`.

### Environment variables and PATH

Capture user and machine environments separately. Do not copy the merged process environment back into both scopes.

During restore:

- Preserve target system-defined variables.
- Propose changes only for selected custom variables and PATH entries.
- Preserve raw `REG_SZ` versus `REG_EXPAND_SZ` semantics.
- Resolve mapped paths and prerequisites before enabling proposed additions.
- Append missing entries by default, preserving target ordering.
- Compare entries case-insensitively without unnecessarily changing their spelling.
- Remove only duplicate additions introduced by the restore.
- Keep missing-path entries disabled until dependencies are resolved or the user explicitly accepts them.
- Recheck the registry immediately before committing to avoid overwriting concurrent edits.
- Broadcast the environment-change notification and explain that existing terminals may need restarting.

Machine-level changes require the elevated broker.

### Elevation behavior

- Show an elevation badge on affected actions.
- Offer **Run privileged steps** only when needed.
- If UAC is declined, continue eligible user-level work and mark privileged steps blocked.
- Never change ownership or ACLs to bypass denied access.
- Preserve the initiating user’s SID and resolved profile context when elevation uses another administrator account.
- Do not accidentally restore the initiating user’s settings into the administrator’s HKCU/profile.
- Validate the approved plan again inside the broker.
- Keep archive parsing and decompression unelevated.

Background processes run with hidden windows and redirected output. Interactive authentication or unavoidable installers are surfaced deliberately with an explanation.

### Journaling, recovery, and rollback

Persist each operation’s intent before its side effect and its outcome afterward.

- Revalidate the target against preflight immediately before replacement.
- Stage replacements on the destination volume and use atomic file replacement where available.
- Keep pre-change file/registry values for supported rollback.
- Commit per artifact; do not claim a single transaction across all files, registry changes, and installers.
- On an artifact failure, restore its prior state where possible.
- Continue independent artifacts and block dependents.
- On restart, offer to inspect, recheck, retry, or roll back an interrupted restore.
- Do not blindly rerun operations whose outcome is uncertain.
- Preserve rollback records until the user explicitly removes them.
- Protect sensitive recovery content with restricted access and encrypted storage.

Cancel cooperatively at safe checkpoints. Backup cancellation removes incomplete output; restore cancellation preserves an accurate record of committed and pending actions.

### Progress and logging

Show overall stage, current item, processed files/bytes, elapsed time, throughput, warning count, and cancellation status.

- During open-ended discovery, show indeterminate progress plus counts and current scope.
- Once work totals are known, use determinate progress.
- Separate staging, compression, verification, installation, and restore stages.
- Estimate ETA from measured throughput only when meaningful.
- Do not show fabricated percentages or ETA for arbitrary installers or unbounded enumeration.
- Throttle UI updates while retaining detailed worker events.

Use structured local logs with job and operation IDs. Display concise errors and expandable technical details. Retain rotating logs for 30 days with a 100 MB cap; keep recovery journals separately.

Final reports must list applied, verified, skipped, blocked, and failed items, along with actionable next steps. Export human-readable HTML and machine-readable JSON reports with sensitive values removed.

## 5. Six implementation phases and acceptance gates

### Phase 1 — Foundation and portable desktop shell

Build the solution boundaries, dependency injection, contracts, navigation, theme resources, local state, worker IPC, and narrow broker skeleton.

Validate the chosen archive library with password handling, non-solid extraction, Unicode paths, and large-file streaming.

**Acceptance gate:**

- Portable ZIP launches on a clean Windows 11 x64 VM without .NET installed.
- Startup performs no discovery or network access.
- Theme switching, keyboard navigation, scaling, and high contrast work.
- Worker failures are reported without crashing the GUI.
- Broker rejects unauthorized or malformed requests.

### Phase 2 — Discovery and migration catalog

Implement system inventory providers, drive traversal, environment capture, repository detection, and all selected tool adapters’ discovery capabilities.

Add evidence/confidence, coverage reporting, filters, custom roots, and backup selection.

**Acceptance gate:**

- Fixtures cover registry views, user/system installs, Store apps, portable apps, duplicate versions, custom roots, and inaccessible locations.
- Discovery does not launch found programs or modify installed applications.
- Full-drive scans remain cancellable and responsive.
- Other-user profile exclusions and junction handling are visible.
- Baseline selections match the confirmed defaults.

### Phase 3 — Backup creation and archive inspection

Implement capture staging, sensitivity review, exclusions, repository dependency handling, archive/index generation, encryption, verification, and backup browsing.

**Acceptance gate:**

- One self-contained backup file is produced.
- Unencrypted and encrypted round trips preserve selected bytes and metadata.
- Known included secrets force encryption.
- Encrypted archives conceal filenames and require a password before preview.
- Wrong passwords, corrupt archives, disk-full conditions, locked files, and cancellation produce accurate outcomes.
- A final archive never replaces an existing output without an explicit overwrite decision.

### Phase 4 — Restore planning and prerequisite resolution

Implement target checks, dependency graphs, root mapping, structured previews, conflict resolution, package recipes, and remediation/recheck flows.

**Acceptance gate:**

- Preflight makes no restoration changes.
- Missing host apps, runtimes, permissions, credentials, destinations, and incompatible versions produce specific guidance.
- Existing values are preserved by default.
- Plan changes invalidate prior approval where new effects are introduced.
- Unsupported MCP commands remain manual requirements.
- `C:\Users\Alice` to another profile and `C:\Projects` to `D:\Projects` produce correct scoped mappings.

### Phase 5 — Restore execution, verification, and recovery

Implement file/configuration operations, approved dependency installation, PATH merging, brokered system changes, journaling, rollback, and final reporting.

**Acceptance gate:**

- Representative migrations succeed between two distinct Windows user profiles.
- Selected repositories retain branches, stashes, local changes, and untracked files.
- Linked worktrees and submodules retain valid metadata after supported mapping.
- User and machine environment scopes remain separate.
- UAC refusal does not prevent independent user-level restoration.
- Interrupted restores recover without silently repeating uncertain operations.
- A second identical restore does not duplicate PATH entries or settings.
- Reports distinguish copied configuration from verified functionality.

### Phase 6 — Hardening, performance, and release

Complete adversarial archive tests, integration fixtures, UI automation, performance validation, signing, notices, release packaging, and migration documentation.

**Required test matrix:**

| Area | Scenarios |
|---|---|
| Clean deployment | No developer tools/runtime installed; offline launch; removable-drive launch |
| Discovery | Multiple drives, large trees, inaccessible folders, cloud placeholders, portable and Store installations |
| Archive security | Traversal, duplicate names, malformed indexes, oversized metadata, corrupt payloads, links escaping roots |
| Credentials | Redaction, explicit inclusion, encryption enforcement, password/log leakage |
| Filesystems | Unicode, spaces, long paths, empty files/directories, read-only targets, FAT32 oversized-output rejection |
| Repositories | Bare repos, worktrees, submodules, LFS, external object storage, active locks, embedded secret-history disclosure |
| Restore safety | Concurrent target edits, target app running, insufficient space, UAC cancellation, changed policy |
| Recovery | Forced termination between journal/write steps, rollback conflicts, failed installers |
| Integrations | Supported version fixtures and unknown-version fallback for every adapter |
| UX | Keyboard-only operation, screen reader, dark/light/high contrast, DPI scaling |
| Scale | 100 GB and one million files, including incompressible and many-small-file workloads |

Performance gates on a documented 16 GB RAM SSD reference machine:

- No UI-thread filesystem enumeration, hashing, compression, or package operations.
- Visible progress continues during long operations.
- Cancellation is acknowledged immediately and takes effect at a documented safe checkpoint.
- Backup/restore memory remains bounded; target peak working set below 2 GB on the scale fixture.
- Manifest overview does not require payload extraction.
- Measure throughput and memory in release builds; publish results rather than promising a universal completion time.

**Release deliverables:**

- Portable release ZIP and checksums.
- Signed production executables.
- Third-party notices and software bill of materials.
- Supported integration/version matrix.
- Archive format and adapter-development documentation.
- User migration guide and troubleshooting guide.
- Automated test results and a two-machine migration acceptance report.

Implementation is complete only when the full discovery-to-verified-restore journey passes these gates. A working GUI, successful compression, or successful file copying alone does not constitute completion.
