# Adapter development

An **adapter** teaches DevBR where one tool keeps its user-level configuration, what of it is safe and
useful to back up, and what is deliberately left behind. Adapters ship inside DevBR: the first release
does not load third-party adapter DLLs, and never executes rules or code found inside a backup archive.

This document describes the code as it is. The supported tools and versions are listed in
[INTEGRATIONS.md](INTEGRATIONS.md); the archive layout is in [ARCHIVE-FORMAT.md](ARCHIVE-FORMAT.md).

## How an adapter fits in

| Stage | Where | What the adapter contributes |
|---|---|---|
| Recognize the tool | `src/DevBR.Discovery/Catalog/KnownTools.cs` | A `KnownTool` entry: id, display name, executables, uninstall-name regex, Store package names, npm packages, and the `AdapterId` that handles its configuration |
| Discover | `src/DevBR.Discovery/Adapters/*.cs` | A `ToolAdapter` subclass returning inventory items, `MigrationArtifact`s and coverage |
| Register | `DiscoveryEngine.DefaultAdapters` in `src/DevBR.Discovery/DiscoveryEngine.cs` | One line adding the adapter instance |
| Capture (optional) | `src/DevBR.Backup/Capture/CaptureTransforms.cs` | A redaction transform for a specific artifact file (for example removing remembered trust approvals) |
| Merge (optional) | `MergeProfile.For` in `src/DevBR.Restore/JsonMerge.cs` | How a JSON/JSONC file merges: scoped keys, atomic paths, array identities, path fields that may be remapped |
| Restore guidance | `src/DevBR.Restore/RestorePlanner.cs`, `src/DevBR.Restore/Dependencies.cs` | Sign-in instructions (`Reauthentication`), host install hints (`PackageRecipes.HostHint`), and runtime/extension recipes |

Everything else — backup staging and hashing, path mapping, conflict decisions, journaling,
verification, rollback, reporting — is generic and driven by the artifact data the adapter produces.
Adapters never write to the machine; the restore executor performs every change.

## The contract

```csharp
public interface IToolAdapter
{
    string Id { get; }                               // "gh"; prefixes every artifact id ("gh:config")
    string DisplayName { get; }                      // "GitHub CLI"
    IReadOnlyList<string> SupportedVersions { get; } // versions with verified semantic handling
    AdapterDiscovery Discover(IMachine machine, CancellationToken cancellationToken);
}
```

Derive from `ToolAdapter`, which wraps the machine in an `AdapterScope` and builds the result:

```csharp
public sealed class GitHubCliAdapter : ToolAdapter
{
    public override string Id => "gh";
    public override string DisplayName => "GitHub CLI";
    protected override string ToolId => "gh";        // the KnownTools id the artifacts belong to

    protected override void Discover(AdapterScope s, CancellationToken cancellationToken)
    {
        var (root, _) = s.ResolveRoot(s.Combine(s.Folders.RoamingAppData, "GitHub CLI"), "GH_CONFIG_DIR");
        if (!s.Exists(root))
        {
            return;
        }

        s.ConfigurationItem("GitHub CLI configuration", root);
        s.Artifact("config", ArtifactKind.Settings, "Preferences and aliases", s.Combine(root, "config.yml"),
            "Editor, protocol and alias preferences.",
            excluded: ["Host authentication (run 'gh auth login' on the new computer)"]);
        // hosts.yml holds tokens when no keyring is used: list it as a credential, never back it up silently.
    }
}
```

`SupportedVersions` defaults to `["any (file-level)"]`: unknown versions fall back to explicit file
restore. Only list a version once a fixture proves its semantic handling.

### `AdapterScope` helpers

| Member | Use |
|---|---|
| `Machine`, `Fs`, `Folders` | Read-only access to the machine (`IMachine`): filesystem, registry, environment, known folders. Works identically on real and simulated machines. |
| `ResolveRoot(default, params overrideVariables)` | Honors documented environment overrides (`CODEX_HOME`, `GH_CONFIG_DIR`, …) before the default location, and reports which one was used |
| `Exists`, `Combine`, `Children` | Path helpers. `Children` records access-denied folders in coverage instead of throwing |
| `Artifact(key, kind, displayName, path(s), description, sensitivity, eligibility, capability, excluded, selectedByDefault, customRootKey)` | Adds a backup item for the paths that exist; returns null when none exist or the folder is empty. Small text files with recognizable secrets are automatically raised to `ContainsRecognizedSecrets` |
| `Credential(key, displayName, path, description, portable)` | Lists a credential file: `ExcludedByDefault` if portable (including it forces encryption), `Blocked` if machine-bound |
| `ConfigurationItem(name, location, properties)` | Adds an inventory record ("configuration found at its documented location") with optional properties shown in the details panel |
| `Item`, `AddArtifact` | Add prebuilt `InventoryItem` / `MigrationArtifact` records (extensions, modules) |
| `Coverage` | `CoverageBuilder` for skipped or inaccessible locations |

### Artifact fields that drive the rest of DevBR

- **Id**: `"{adapter Id}:{key}"`. Keep keys stable; they appear in archives, merge profiles, capture
  transforms and tests.
- **Kind** (`ArtifactKind`): `Settings`, `Keybindings`, `Snippets`, `McpConfiguration`, `Agents`, `Skills`,
  `Hooks`, `Plugins`, `Instructions`, `Scripts`, `Modules`, `Commands`, `Rules`, `ExtensionInventory`,
  `Credentials`, … Used for grouping and for special handling (repositories, extension inventories).
- **Sensitivity**: `None`, `MayContainSecrets` (MCP files, env blocks), `ContainsRecognizedSecrets`,
  `Credential`. Anything above `None` is shown in the backup review; recognized secrets and credentials
  force encryption.
- **Eligibility**: `Eligible`, `InventoryOnly` (record it, do not copy it), `ExcludedByDefault`, `Blocked`.
- **Capability** (flags): `ReinstallGuidance`, `ExplicitFileRestore`, `StructuredMerge` (requires a
  `MergeProfile`), `DependencyInstall`.
- **excluded**: human-readable list of what is intentionally left out (sessions, histories, caches,
  logs, sign-ins). Shown to the user.
- **selectedByDefault**: leave `false`. Only the inventory and the non-secret machine environment are
  selected by default.
- **customRootKey**: when the tool's root came from an override or lives outside the known folders, pass
  the root so paths are stored relative to a `CustomRoot` and can be remapped on restore.

Paths are stored as logical paths (`UserProfile`, `RoamingAppData`, `LocalAppData`, `ProgramData`,
`CustomRoot`, `RepositoryRoot` + relative path), which is what makes profile and drive mapping work.

## Rules every adapter must follow

1. **Read only.** Never launch the tool, run its scripts, start MCP servers, or modify anything. Use
   `IMachine` exclusively — no `System.IO.File`, `Registry` or `Process` calls — so simulated machines
   and tests exercise the same code.
2. **Exclude by default** sessions, histories, caches, logs, machine identities, remembered trust
   approvals and authentication tokens. List them in `excluded` so users know.
3. **Credentials are separate artifacts** created with `Credential(...)`, never bundled into a settings
   artifact.
4. **Bounded reads.** Use `Fs.ReadText(path, maxBytes)` and helpers in `Discovery/Support` such as
   `ConfigReaders`; parse defensively and treat every file as untrusted.
5. **Honor cancellation** in loops over directories.
6. **Respect enterprise-managed settings.** Record them (as inventory) but never offer them for restore.
7. **No new path rewriting by text replacement.** Only fields declared in a `MergeProfile` (`IsPathField`)
   are remapped.

## Optional: capture transforms

When a file mixes portable settings with machine-specific or secret state, add a case to
`CaptureTransforms.For(artifactId, fileName)`: `KeepOnly(...)`, `Remove(...)` or a custom transform. A
file that cannot be parsed is omitted rather than partially redacted. If the transform removes all
recognized secrets, add the artifact id to `RedactsSecrets` so encryption is not forced.

## Optional: structured merge

Without a merge profile, restoring an existing file offers whole-file choices (keep, replace, restore
alongside). To merge at setting level, add the adapter id and file name to `MergeProfile.For` and set
`RestoreCapability.StructuredMerge` on the artifact:

- `ScopeKeys`: only these top-level keys are merged; the rest of the target file is untouched.
- `AtomicPaths`: values compared and copied as a whole (`"mcpServers.*"`).
- `ArrayIdentities`: how array elements are matched (`profiles.list` by `guid`, keybindings by
  key|command|when).
- `IsPathField`: which properties hold paths that may be remapped.

Merges preserve comments and formatting (`JsoncEditor`), keep the target's value on conflict unless the
user chooses the backup's, and add only missing values.

## Optional: restore guidance and dependencies

- Add sign-in instructions to `RestorePlanner.Reauthentication` (keyed by `KnownTool` id).
- Add an install hint for the host application to `PackageRecipes.HostHint` so preflight can say how to
  install it. DevBR never installs main applications.
- Runtime and editor-extension recipes live in `PackageRecipes`; they must use validated package
  identities and existing package managers (WinGet, the editor's CLI). Never derive a command from a
  string found in a configuration file.

## Testing an adapter

1. Add the tool's files to the sample workstation in `src/DevBR.Simulation` (`SampleMachines`) or build
   a fixture with `SimulatedMachineBuilder` (`File`, `Directory`, `Executable`, `Environment`,
   `Inaccessible`, `Junction`, `LockedFile`, `Placeholder`, `RegistryKey`, `UninstallEntry`, …).
2. Add assertions to `tests/DevBR.Tests/Discovery/DiscoveryTests.cs` (the "Adapters" section shows the
   style): artifact ids exist, sensitivity and eligibility are right, overrides win, credentials are
   separate, coverage records inaccessible folders.
3. For transforms and merge profiles, add cases to `tests/DevBR.Tests/Backup` and
   `tests/DevBR.Tests/Restore` (round-trip through backup and restore against
   `SampleMachines.CleanTarget`).
4. Include an unknown-version fixture so the file-level fallback is exercised.
5. Run `dotnet test --project tests/DevBR.Tests`, then try it in the app with
   `DevBR.exe --machine sample` (development builds; see [SIMULATION.md](SIMULATION.md)).
6. Update [INTEGRATIONS.md](INTEGRATIONS.md) with the tool, the versions verified and what is excluded.
