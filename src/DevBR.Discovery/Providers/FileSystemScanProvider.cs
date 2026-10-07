using System.Diagnostics;
using System.Threading.Channels;
using DevBR.Application.Discovery;
using DevBR.Application.Machine;
using DevBR.Discovery.Catalog;
using DevBR.Discovery.Support;
using DevBR.Domain;

namespace DevBR.Discovery.Providers;

/// <summary>
/// Walks every fixed local drive (and any user-added roots) looking for recognized signatures:
/// repositories, tool executables (portable or unregistered installs) and project-level AI
/// configuration. Bounded concurrency, lazy per-directory listing, no file content is read unless a
/// detector needs it, and every skipped or inaccessible scope is reported.
/// </summary>
public sealed class FileSystemScanProvider(int maxParallelism = 4) : IDiscoveryProvider
{
    private const int MaxDepth = 64;

    /// <summary>Regenerable dependency and build folders: not entered, never silently.</summary>
    private static readonly Dictionary<string, string> PrunedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["node_modules"] = "Dependency folder (regenerable)",
        ["__pycache__"] = "Build cache (regenerable)",
        [".venv"] = "Python virtual environment (regenerable)",
        [".tox"] = "Build cache (regenerable)",
        [".gradle"] = "Build cache (regenerable)",
        [".next"] = "Build output (regenerable)",
        [".nuxt"] = "Build output (regenerable)",
        [".pytest_cache"] = "Build cache (regenerable)",
        [".mypy_cache"] = "Build cache (regenerable)",
        ["obj"] = "Build output (regenerable)",
        ["$Recycle.Bin"] = "Recycle bin",
        ["System Volume Information"] = "Protected operating-system data",
        ["$WinREAgent"] = "Protected operating-system data",
        ["$SysReset"] = "Protected operating-system data",
        ["$Windows.~BT"] = "Protected operating-system data",
        ["$Windows.~WS"] = "Protected operating-system data",
        ["Config.Msi"] = "Protected operating-system data",
        ["Recovery"] = "Recovery data",
    };

    /// <summary>Project-level configuration signatures → (tool id, description).</summary>
    private static readonly Dictionary<string, (string Tool, string Description)> ProjectConfigDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        [".claude"] = ("claude-code", "Claude Code project settings"),
        [".cursor"] = ("cursor", "Cursor project rules and settings"),
        [".gemini"] = ("gemini-cli", "Gemini CLI project settings"),
        [".codex"] = ("codex", "Codex project settings"),
    };

    // Exact-case names: lowercase variants inside application bundles are documentation, not project configuration.
    private static readonly Dictionary<string, (string Tool, string Description)> ProjectConfigFiles = new(StringComparer.Ordinal)
    {
        ["CLAUDE.md"] = ("claude-code", "Claude Code project instructions"),
        [".mcp.json"] = ("claude-code", "Project MCP servers (.mcp.json)"),
        ["AGENTS.md"] = ("codex", "Agent instructions (AGENTS.md)"),
        ["GEMINI.md"] = ("gemini-cli", "Gemini CLI project instructions"),
        [".cursorrules"] = ("cursor", "Cursor rules (.cursorrules)"),
    };

    public string Id => "filesystem";

    public string DisplayName => "Local drives";

    public async Task<DiscoveryResult> DiscoverAsync(DiscoveryContext context, CancellationToken cancellationToken)
    {
        var machine = context.Machine;
        var roots = new List<string>();
        if (context.ScanFixedDrives)
        {
            roots.AddRange(machine.FileSystem.GetFixedDrives().Select(d => Paths.Normalize(d.Root)));
        }

        foreach (var extra in context.ExtraRoots.Select(Paths.Normalize))
        {
            if (!roots.Any(r => Paths.IsUnder(extra, r)))
            {
                roots.Add(extra);
            }
        }

        var rules = new ScanRules(machine, context);
        var items = new System.Collections.Concurrent.ConcurrentBag<InventoryItem>();
        var coverages = roots.ToDictionary(r => r, r => new CoverageBuilder(DisplayName, r), StringComparer.OrdinalIgnoreCase);
        var queue = Channel.CreateUnbounded<(string Path, int Depth, string Root)>();
        var pending = 0;
        var progressClock = Stopwatch.StartNew();
        var progressLock = new Lock();

        foreach (var root in roots)
        {
            if (!machine.FileSystem.DirectoryExists(root))
            {
                coverages[root].Error($"{root} does not exist or is not available.");
                continue;
            }

            Interlocked.Increment(ref pending);
            queue.Writer.TryWrite((root, 0, root));
        }

        if (pending == 0)
        {
            queue.Writer.Complete();
        }

        void Found(InventoryItem item)
        {
            items.Add(item);
            context.OnItemFound?.Invoke(item);
        }

        async Task WorkerAsync()
        {
            await foreach (var (path, depth, root) in queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    ScanDirectory(path, depth, root);
                }
                finally
                {
                    if (Interlocked.Decrement(ref pending) == 0)
                    {
                        queue.Writer.TryComplete();
                    }
                }
            }
        }

        void ScanDirectory(string path, int depth, string root)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var coverage = coverages[root];

            IReadOnlyList<FileSystemEntry> entries;
            try
            {
                entries = [.. machine.FileSystem.EnumerateEntries(path)];
            }
            catch (UnauthorizedAccessException)
            {
                coverage.Inaccessible(path, "Access denied.");
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (IOException ex)
            {
                coverage.Inaccessible(path, ex.Message);
                return;
            }

            coverage.CountDirectory();
            coverage.CountFiles(entries.Count(e => !e.IsDirectory));
            ReportProgress(path, root);

            var names = new HashSet<string>(entries.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
            // The profile root holds user-level config (handled by adapters); install trees hold bundled docs.
            var notAProject = Paths.Equal(path, machine.Folders.UserProfile) || rules.IsInstallTree(path);

            // Repositories.
            var isRepository = names.Contains(".git");
            if (isRepository && RepositoryInspector.Inspect(machine, path, bare: false) is { } repository)
            {
                Found(repository);
            }
            else if (!isRepository && LooksLikeBareRepository(names, entries) && RepositoryInspector.Inspect(machine, path, bare: true) is { } bare)
            {
                Found(bare);
                return; // Git internals: nothing else to find inside.
            }

            foreach (var entry in entries)
            {
                if (!entry.IsDirectory)
                {
                    InspectFile(entry, path, notAProject);
                    continue;
                }

                if (entry.Name.Equals(".git", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (entry.IsReparsePoint)
                {
                    coverage.Exclude(entry.FullPath, "Junction or symbolic link (not followed)");
                    continue;
                }

                if (entry.IsCloudPlaceholder)
                {
                    coverage.Exclude(entry.FullPath, "Cloud placeholder (not downloaded)");
                    continue;
                }

                if (rules.ExclusionReason(entry.FullPath, entry.Name) is { } reason)
                {
                    coverage.Exclude(entry.FullPath, reason);
                    continue;
                }

                if (!notAProject && ProjectConfigDirectories.TryGetValue(entry.Name, out var config))
                {
                    Found(ProjectConfig(entry.FullPath, path, config.Tool, config.Description));
                }

                if (!notAProject && entry.Name.Equals(".github", StringComparison.OrdinalIgnoreCase) && machine.FileSystem.FileExists(Paths.Combine(entry.FullPath, "copilot-instructions.md")))
                {
                    Found(ProjectConfig(Paths.Combine(entry.FullPath, "copilot-instructions.md"), path, "copilot-cli", "Copilot repository instructions"));
                }

                if (!notAProject && entry.Name.Equals(".vscode", StringComparison.OrdinalIgnoreCase) && machine.FileSystem.FileExists(Paths.Combine(entry.FullPath, "mcp.json")))
                {
                    Found(ProjectConfig(Paths.Combine(entry.FullPath, "mcp.json"), path, "vscode", "VS Code workspace MCP servers"));
                }

                if (depth + 1 > MaxDepth)
                {
                    coverage.Exclude(entry.FullPath, $"Deeper than {MaxDepth} levels");
                    continue;
                }

                Interlocked.Increment(ref pending);
                queue.Writer.TryWrite((entry.FullPath, depth + 1, root));
            }
        }

        void InspectFile(FileSystemEntry file, string directory, bool notAProject)
        {
            if (!notAProject && ProjectConfigFiles.TryGetValue(file.Name, out var config))
            {
                Found(ProjectConfig(file.FullPath, directory, config.Tool, config.Description));
                return;
            }

            if (KnownTools.ByExecutableName(file.Name, directory) is not { } tool || file.IsCloudPlaceholder)
            {
                return;
            }

            var scope = ItemFactory.ScopeForPath(machine.Folders, directory);
            var properties = new Dictionary<string, string>();
            if (tool.Id is "vscode" or "vscode-insiders" && machine.FileSystem.DirectoryExists(Paths.Combine(directory, "data")))
            {
                properties["portableMode"] = "true";
                scope = InstallScope.Portable;
            }

            Found(ItemFactory.Installation(machine, tool, tool.DisplayName, machine.FileSystem.GetFileVersion(file.FullPath), null, scope, directory,
                new DiscoveryEvidence("filesystem", $"{file.Name} found on disk", file.FullPath), Confidence.Medium, properties.Count == 0 ? null : properties));
        }

        void ReportProgress(string path, string root)
        {
            if (context.Progress is null)
            {
                return;
            }

            lock (progressLock)
            {
                if (progressClock.ElapsedMilliseconds < 150)
                {
                    return;
                }

                progressClock.Restart();
            }

            var coverage = coverages[root];
            context.Progress.Report(new OperationEvent(context.JobId, OperationStage.Discovery, root, coverage.Files, null, 0, null,
                path, EventSeverity.Information, DateTimeOffset.UtcNow, TimeSpan.Zero, null, EtaConfidence.None));
        }

        var workers = Enumerable.Range(0, Math.Max(1, maxParallelism)).Select(_ => Task.Run(WorkerAsync, cancellationToken)).ToArray();
        await Task.WhenAll(workers).ConfigureAwait(false);

        return new DiscoveryResult([.. items], [.. coverages.Values.Select(c => c.Build())]);
    }

    private static bool LooksLikeBareRepository(HashSet<string> names, IReadOnlyList<FileSystemEntry> entries)
        => names.Contains("HEAD") && names.Contains("config")
            && entries.Any(e => e.IsDirectory && e.Name.Equals("objects", StringComparison.OrdinalIgnoreCase))
            && entries.Any(e => e.IsDirectory && e.Name.Equals("refs", StringComparison.OrdinalIgnoreCase));

    private static InventoryItem ProjectConfig(string path, string project, string toolId, string description)
    {
        var tool = KnownTools.Get(toolId);
        return new InventoryItem(
            ItemIds.For("AiConfiguration", description, path),
            InventoryCategory.AiConfiguration,
            description,
            null,
            null,
            InstallScope.Unknown,
            [Paths.Normalize(path)],
            [new DiscoveryEvidence("filesystem", $"{Path.GetFileName(path)} in {project}", path)],
            Confidence.High,
            DetectionStatus.Detected,
            tool?.AdapterId,
            toolId,
            new Dictionary<string, string> { ["project"] = Paths.Normalize(project), ["level"] = "project" });
    }

    /// <summary>Folders that are never entered, each with the reason shown in the coverage report.</summary>
    private sealed class ScanRules
    {
        private readonly List<(string Path, string Reason)> _paths = [];
        private readonly string[] _installRoots;
        private readonly string _usersRoot;
        private readonly string _currentUser;
        private readonly bool _includeOtherUsers;

        public ScanRules(IMachine machine, DiscoveryContext context)
        {
            var f = machine.Folders;
            _usersRoot = Paths.Normalize(f.UsersRoot);
            _currentUser = Path.GetFileName(Paths.Normalize(f.UserProfile));
            _includeOtherUsers = context.IncludeOtherUserProfiles;
            _installRoots = [f.ProgramFiles, f.ProgramFilesX86, Paths.Combine(f.LocalAppData, "Programs")];

            _paths.Add((f.Windows, "Windows system folder"));
            _paths.Add((Paths.Combine(f.ProgramFiles, "WindowsApps"), "Packaged apps (listed through the packaging API instead)"));
            _paths.Add((Paths.Combine(f.ProgramData, "Package Cache"), "Installer cache"));
            _paths.Add((Paths.Combine(f.ProgramData, "Microsoft", "Windows", "WER"), "Crash reports"));
            _paths.Add((Paths.Combine(f.LocalAppData, "Temp"), "Temporary files"));
            _paths.Add((Paths.Combine(f.LocalAppData, "Packages"), "Packaged-app data (read by tool adapters directly)"));
            _paths.Add((Paths.Combine(f.LocalAppData, "Microsoft"), "Windows and Microsoft app caches"));
            _paths.Add((Paths.Combine(f.LocalAppData, "CrashDumps"), "Crash reports"));

            // Tool-owned data folders: read by their adapters, and full of caches, bundled runtimes and
            // downloaded plugins that would otherwise look like separate installations or projects.
            const string ToolData = "Tool data folder (inspected by its adapter instead)";
            foreach (var name in new[] { ".claude", ".codex", ".copilot", ".gemini", ".cursor", ".vscode", ".vscode-insiders", ".cache", ".docker", ".dotnet", ".nuget", ".npm", "scoop", "pipx" })
            {
                _paths.Add((Paths.Combine(f.UserProfile, name), ToolData));
            }

            foreach (var name in new[] { "Claude", "Code", "Code - Insiders", "Cursor", "npm", "npm-cache", "uv", "Python", "GitHub CLI", "Docker" })
            {
                _paths.Add((Paths.Combine(f.RoamingAppData, name), ToolData));
            }

            foreach (var name in new[] { @"OpenAI\Codex", "AnthropicClaude", "Programs\\cursor\\resources", "pip", "uv", "npm-cache", "Docker", "NuGet" })
            {
                _paths.Add((Paths.Combine(f.LocalAppData, name), ToolData));
            }
            foreach (var excluded in context.ExcludedPaths)
            {
                _paths.Add((excluded, "DevBR's own data, scratch or output folder"));
            }
        }

        public bool IsInstallTree(string path) => _installRoots.Any(root => Paths.IsUnder(path, root));

        public string? ExclusionReason(string path, string name)
        {
            foreach (var (excluded, reason) in _paths)
            {
                if (Paths.Equal(path, excluded))
                {
                    return reason;
                }
            }

            if (PrunedNames.TryGetValue(name, out var pruned))
            {
                return pruned;
            }

            if (Paths.Equal(Paths.Parent(path) ?? string.Empty, _usersRoot))
            {
                if (name is "Default" or "Default User" or "All Users")
                {
                    return "Profile template";
                }

                if (!_includeOtherUsers && !name.Equals(_currentUser, StringComparison.OrdinalIgnoreCase) && !name.Equals("Public", StringComparison.OrdinalIgnoreCase))
                {
                    return "Another user's profile (not entered)";
                }
            }

            return null;
        }
    }
}
