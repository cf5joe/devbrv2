using DevBR.Application.Machine;

namespace DevBR.Simulation;

/// <summary>
/// Ready-made simulated computers. <see cref="DeveloperWorkstation"/> exercises every discovery scenario
/// the plan's acceptance gates name; <see cref="CleanTarget"/> is a freshly set-up PC to restore onto.
/// </summary>
public static class SampleMachines
{
    public const string WorkstationUser = "alice";
    public const string TargetUser = "alex";

    public static SimulatedMachine DeveloperWorkstation(string root)
    {
        var b = new SimulatedMachineBuilder(WorkstationUser, "ALICE-DEV");
        var f = b.Folders;
        var home = f.UserProfile;
        var roaming = f.RoamingAppData;
        var local = f.LocalAppData;

        b.Drive(@"D:\", "Data");

        // --- Installed software: both registry views, user and machine scope -------------------------
        b.UninstallEntry(RegistryHive.LocalMachine, RegistryView.Registry64, "Git_is1", "Git", "2.55.0", "The Git Development Community", @"C:\Program Files\Git\")
         .Executable(@"C:\Program Files\Git\cmd\git.exe", "2.55.0")
         .Executable(@"C:\Program Files\Git\bin\git.exe", "2.55.0")
         .File(@"C:\Program Files\Git\etc\gitconfig", "[core]\n\tautocrlf = true\n");
        b.UninstallEntry(RegistryHive.CurrentUser, RegistryView.Registry64, "{771FD6B0-FA20-440A-A002-3B3BAC16DC50}_is1", "Microsoft Visual Studio Code (User)", "1.105.1", "Microsoft Corporation",
            $@"{local}\Programs\Microsoft VS Code\", $@"{local}\Programs\Microsoft VS Code\Code.exe")
         .Executable($@"{local}\Programs\Microsoft VS Code\Code.exe", "1.105.1")
         .File($@"{local}\Programs\Microsoft VS Code\bin\code.cmd", "@echo off");
        b.UninstallEntry(RegistryHive.CurrentUser, RegistryView.Registry64, "62625861-8486-5be9-9e46-1da50df5f8ff", "Cursor 1.7.0 (User)", "1.7.0", "Anysphere", $@"{local}\Programs\cursor\")
         .Executable($@"{local}\Programs\cursor\Cursor.exe", "1.7.0");
        b.UninstallEntry(RegistryHive.LocalMachine, RegistryView.Registry64, "{A1B2C3D4-0000-4000-8000-NODEJS000001}", "Node.js", "22.11.0", "Node.js Foundation", @"C:\Program Files\nodejs\")
         .Executable(@"C:\Program Files\nodejs\node.exe", "22.11.0");
        // Two Python versions side by side: a user install and a machine install must stay distinct.
        b.UninstallEntry(RegistryHive.CurrentUser, RegistryView.Registry64, "{PY312-USER}", "Python 3.12.7 (64-bit)", "3.12.7150.0", "Python Software Foundation", $@"{local}\Programs\Python\Python312\")
         .Executable($@"{local}\Programs\Python\Python312\python.exe", "3.12.7");
        b.UninstallEntry(RegistryHive.LocalMachine, RegistryView.Registry64, "{PY311-MACHINE}", "Python 3.11.9 (64-bit)", "3.11.9150.0", "Python Software Foundation", @"C:\Program Files\Python311\")
         .Executable(@"C:\Program Files\Python311\python.exe", "3.11.9");
        b.UninstallEntry(RegistryHive.LocalMachine, RegistryView.Registry64, "{PWSH7}", "PowerShell 7-x64", "7.5.3.0", "Microsoft Corporation", @"C:\Program Files\PowerShell\7\")
         .Executable(@"C:\Program Files\PowerShell\7\pwsh.exe", "7.5.3");
        b.UninstallEntry(RegistryHive.LocalMachine, RegistryView.Registry64, "{GHCLI}", "GitHub CLI", "2.80.0", "GitHub, Inc.", @"C:\Program Files\GitHub CLI\")
         .Executable(@"C:\Program Files\GitHub CLI\gh.exe", "2.80.0");
        b.UninstallEntry(RegistryHive.LocalMachine, RegistryView.Registry64, "Docker Desktop", "Docker Desktop", "4.47.0", "Docker Inc.", @"C:\Program Files\Docker\Docker")
         .Executable(@"C:\Program Files\Docker\Docker\Docker Desktop.exe", "4.47.0");
        b.UninstallEntry(RegistryHive.LocalMachine, RegistryView.Registry64, "{DOTNETSDK}", "Microsoft .NET SDK 10.0.401 (x64)", "10.4.126.42413", "Microsoft Corporation")
         .Executable(@"C:\Program Files\dotnet\dotnet.exe", "10.0.12");
        // 32-bit view only.
        b.UninstallEntry(RegistryHive.LocalMachine, RegistryView.Registry32, "7-Zip", "7-Zip 24.08", "24.08", "Igor Pavlov", @"C:\Program Files (x86)\7-Zip\");
        b.Directory(@"C:\Program Files (x86)\7-Zip");
        // Filtered out: hidden system component and an update record.
        b.UninstallEntry(RegistryHive.LocalMachine, RegistryView.Registry64, "{VCREDIST-HIDDEN}", "Microsoft Visual C++ 2022 X64 Minimum Runtime", "14.40", "Microsoft Corporation", systemComponent: true);
        b.RegistryValue(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\KB5040000", "DisplayName", RegistryValueKind.String, "Update for Node.js (KB5040000)")
         .RegistryValue(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\KB5040000", "ParentKeyName", RegistryValueKind.String, "Node.js");

        // --- Store packages, App Paths, shortcuts -----------------------------------------------------
        b.StorePackage("Microsoft.WindowsTerminal", "8wekyb3d8bbwe", "Windows Terminal", "1.23.12681.0")
         .StorePackage("Microsoft.DesktopAppInstaller", "8wekyb3d8bbwe", "App Installer", "1.26.430.0")
         .StorePackage("Claude", "pzs8sxrjxfjjc", "Claude", "1.0.211.0", "CN=Anthropic, PBC")
         .StorePackage("MicrosoftCorporationII.WindowsSubsystemForLinux", "8wekyb3d8bbwe", "Windows Subsystem for Linux", "2.6.1.0");
        b.AppPath(RegistryHive.LocalMachine, "pwsh.exe", @"C:\Program Files\PowerShell\7\pwsh.exe");
        b.Shortcut($@"{f.StartMenuUser}\Visual Studio Code\Visual Studio Code.lnk", $@"{local}\Programs\Microsoft VS Code\Code.exe")
         .Shortcut($@"{f.StartMenuCommon}\Git\Git Bash.lnk", @"C:\Program Files\Git\git-bash.exe")
         .Executable(@"C:\Program Files\Git\git-bash.exe", "2.55.0");

        // Portable VS Code on the data drive (data folder = portable mode) and Claude Code's native install.
        b.Executable(@"D:\Tools\VSCode-portable\Code.exe", "1.104.0").Directory(@"D:\Tools\VSCode-portable\data\user-data");
        b.Executable($@"{home}\.local\bin\claude.exe", "2.1.0");

        // --- Environment ------------------------------------------------------------------------------
        b.Environment(RegistryHive.LocalMachine, "Path", @"%SystemRoot%\system32;%SystemRoot%;C:\Program Files\Git\cmd;C:\Program Files\nodejs\;C:\Program Files\PowerShell\7\;C:\Program Files\dotnet\;C:\Program Files\GitHub CLI\", expand: true)
         .Environment(RegistryHive.LocalMachine, "DOTNET_CLI_TELEMETRY_OPTOUT", "1")
         .Environment(RegistryHive.CurrentUser, "Path", @"%USERPROFILE%\.local\bin;%LOCALAPPDATA%\Programs\Microsoft VS Code\bin;%APPDATA%\npm;C:\Missing\Tool\bin", expand: true)
         .Environment(RegistryHive.CurrentUser, "PROJECTS", @"D:\Projects")
         .Environment(RegistryHive.CurrentUser, "EDITOR", "code --wait")
         .Environment(RegistryHive.CurrentUser, "GITHUB_TOKEN", "ghp_SIMULATEDsimulatedSIMULATED0123456789")
         .Environment(RegistryHive.CurrentUser, "CODEX_HOME", @"D:\ai\codex");

        // --- Package managers ---------------------------------------------------------------------------
        b.File($@"{roaming}\npm\node_modules\@openai\codex\package.json", """{ "name": "@openai/codex", "version": "0.50.0" }""")
         .File($@"{roaming}\npm\node_modules\@google\gemini-cli\package.json", """{ "name": "@google/gemini-cli", "version": "0.9.0" }""")
         .File($@"{roaming}\npm\node_modules\@github\copilot\package.json", """{ "name": "@github/copilot", "version": "0.0.340" }""")
         .File($@"{roaming}\npm\node_modules\typescript\package.json", """{ "name": "typescript", "version": "5.9.3" }""")
         .File($@"{home}\scoop\apps\jq\current\manifest.json", """{ "version": "1.7.1" }""")
         .File(@"C:\ProgramData\chocolatey\lib\ripgrep\ripgrep.nuspec", "<package><metadata><id>ripgrep</id><version>14.1.1</version></metadata></package>")
         .Directory($@"{local}\Microsoft\WinGet\Packages\sharkdp.fd_Microsoft.Winget.Source_8wekyb3d8bbwe")
         .Directory($@"{home}\.dotnet\tools\.store\dotnet-ef\9.0.0")
         .File($@"{home}\pipx\venvs\black\pipx_metadata.json", """{ "main_package": { "package": "black", "package_version": "24.10.0" } }""")
         .Directory($@"{roaming}\uv\tools\ruff")
         .Directory($@"{local}\Programs\Python\Python312\Lib\site-packages\requests-2.32.3.dist-info");

        // --- Tool configuration ---------------------------------------------------------------------------
        var code = $@"{roaming}\Code\User";
        b.File($@"{code}\settings.json", "{\n  // Editor\n  \"editor.fontSize\": 14,\n  \"files.autoSave\": \"afterDelay\",\n}\n")
         .File($@"{code}\keybindings.json", """[ { "key": "ctrl+k ctrl+t", "command": "workbench.action.selectTheme" } ]""")
         .File($@"{code}\snippets\python.json", """{ "main": { "prefix": "main", "body": ["if __name__ == '__main__':"] } }""")
         .File($@"{code}\mcp.json", """{ "servers": { "github": { "type": "http", "url": "https://api.githubcopilot.com/mcp/" } } }""")
         .File($@"{code}\prompts\review.prompt.md", "Review the change for correctness.")
         .File($@"{code}\globalStorage\storage.json", """{ "userDataProfiles": [ { "location": "-5a2f1c", "name": "Data Science" } ] }""")
         .File($@"{code}\profiles\-5a2f1c\settings.json", """{ "python.defaultInterpreterPath": "C:\\Users\\alice\\AppData\\Local\\Programs\\Python\\Python312\\python.exe" }""")
         .File($@"{code}\workspaceStorage\abc\state.vscdb", "state")
         .File($@"{home}\.vscode\extensions\extensions.json", """[ { "identifier": { "id": "ms-python.python" }, "version": "2025.14.0" }, { "identifier": { "id": "github.copilot-chat" }, "version": "0.31.0" } ]""");

        b.File($@"{roaming}\Cursor\User\settings.json", """{ "editor.fontSize": 13 }""")
         .File($@"{home}\.cursor\mcp.json", """{ "mcpServers": { "docs": { "command": "npx", "args": ["-y", "docs-mcp"] } } }""")
         .File($@"{home}\.cursor\extensions\extensions.json", "[]");

        b.File($@"{home}\.copilot\config.json", """{ "model": "gpt-5", "trusted_folders": ["D:\\Projects\\webapp"] }""")
         .File($@"{home}\.copilot\mcp-config.json", """{ "mcpServers": { "playwright": { "command": "npx", "args": ["@playwright/mcp@latest"] } } }""")
         .File($@"{home}\.copilot\agents\reviewer.md", "---\nname: reviewer\n---\nReview code.")
         .File($@"{home}\.copilot\skills\deploy\SKILL.md", "---\nname: deploy\n---\nDeploy steps.")
         .File($@"{home}\.copilot\session-state\s1.json", "{}");

        // Codex lives at CODEX_HOME (an override on the data drive).
        b.File(@"D:\ai\codex\config.toml", "model = \"gpt-5-codex\"\n\n[mcp_servers.docs]\ncommand = \"npx\"\nargs = [\"-y\", \"docs-mcp\"]\n\n[mcp_servers.linear]\nurl = \"https://mcp.linear.app/sse\"\n")
         .File(@"D:\ai\codex\AGENTS.md", "Prefer small, reviewed changes.")
         .File(@"D:\ai\codex\prompts\changelog.md", "Write a changelog entry.")
         .File(@"D:\ai\codex\skills\release\SKILL.md", "---\nname: release\n---\n")
         .File(@"D:\ai\codex\auth.json", """{ "tokens": { "access_token": "simulated" } }""")
         .File(@"D:\ai\codex\sessions\2025\rollout.jsonl", "{}");

        b.File($@"{home}\.claude\settings.json", """{ "permissions": { "allow": ["Bash(npm test)"] }, "hooks": { "PostToolUse": [ { "matcher": "Edit", "hooks": [ { "type": "command", "command": "~/.claude/hooks/format.ps1" } ] } ] } }""")
         .File($@"{home}\.claude\CLAUDE.md", "Use British spelling.")
         .File($@"{home}\.claude\agents\tester.md", "---\nname: tester\n---\n")
         .File($@"{home}\.claude\skills\pdf\SKILL.md", "---\nname: pdf\n---\n")
         .File($@"{home}\.claude\commands\ship.md", "Ship it.")
         .File($@"{home}\.claude\hooks\format.ps1", "# format")
         .File($@"{home}\.claude\plugins\installed_plugins.json", """{ "plugins": {} }""")
         .File($@"{home}\.claude\.credentials.json", """{ "claudeAiOauth": { "accessToken": "simulated" } }""")
         .File($@"{home}\.claude\projects\D--Projects-webapp\session.jsonl", "{}")
         .File($@"{home}\.claude.json", """{ "mcpServers": { "filesystem": { "command": "npx", "args": ["-y", "@modelcontextprotocol/server-filesystem", "D:\\Projects"] } }, "oauthAccount": { "emailAddress": "alice@example.com" } }""")
         .File(@"C:\ProgramData\ClaudeCode\managed-settings.json", """{ "permissions": { "deny": ["WebFetch"] } }""");

        b.File($@"{roaming}\Claude\claude_desktop_config.json", """{ "mcpServers": { "sqlite": { "command": "uvx", "args": ["mcp-server-sqlite"] } } }""")
         .Directory($@"{roaming}\Claude\Claude Extensions\ant.dir.filesystem");

        b.File($@"{home}\.gemini\settings.json", """{ "theme": "GitHub", "mcpServers": { "git": { "command": "uvx", "args": ["mcp-server-git"] } } }""")
         .File($@"{home}\.gemini\GEMINI.md", "Be concise.")
         .File($@"{home}\.gemini\commands\fix.toml", "prompt = \"Fix it\"")
         .File($@"{home}\.gemini\oauth_creds.json", """{ "access_token": "simulated" }""");

        b.File($@"{home}\.gitconfig", "[user]\n\tname = Alice Example\n\temail = alice@example.com\n[core]\n\texcludesfile = ~/.gitignore_global\n[include]\n\tpath = ~/.gitconfig-work\n[credential]\n\thelper = manager\n")
         .File($@"{home}\.gitconfig-work", "[user]\n\temail = alice@work.example\n")
         .File($@"{home}\.gitignore_global", "*.log\n.DS_Store\n")
         .File($@"{home}\.git-credentials", "https://alice:ghp_SIMULATEDsimulatedSIMULATED0123456789@github.com\n");
        b.File($@"{roaming}\GitHub CLI\config.yml", "git_protocol: https\naliases:\n    co: pr checkout\n")
         .File($@"{roaming}\GitHub CLI\hosts.yml", "github.com:\n    user: alice\n    git_protocol: https\n");

        var docs = f.Documents;
        b.File($@"{docs}\PowerShell\Microsoft.PowerShell_profile.ps1", ". \"$PSScriptRoot\\Scripts\\aliases.ps1\"\nImport-Module posh-git\nSet-PSReadLineOption -EditMode Emacs\n")
         .File($@"{docs}\PowerShell\Scripts\aliases.ps1", "Set-Alias g git")
         .File($@"{docs}\PowerShell\Modules\MyTools\MyTools.psm1", "function Get-Thing {}")
         .File($@"{docs}\PowerShell\Modules\posh-git\1.1.0\PSGetModuleInfo.xml", "<Objs/>")
         .File($@"{docs}\WindowsPowerShell\profile.ps1", "Set-Location D:\\Projects");

        b.File($@"{local}\Packages\Microsoft.WindowsTerminal_8wekyb3d8bbwe\LocalState\settings.json",
                "{ \"profiles\": { \"list\": [ { \"name\": \"PowerShell\" }, { \"name\": \"Ubuntu\" } ] }, \"schemes\": [ { \"name\": \"Night Owl\" } ] }")
         .File($@"{local}\Packages\Microsoft.WindowsTerminal_8wekyb3d8bbwe\RoamingState\background.png", [0x89, 0x50, 0x4E, 0x47])
         .File($@"{local}\Packages\Microsoft.WindowsTerminal_8wekyb3d8bbwe\LocalState\state.json", "{}");

        b.RegistryValue(RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\Lxss", "DefaultDistribution", RegistryValueKind.String, "{1d5b5c2a-0000-4000-8000-000000000001}")
         .RegistryKey(RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\Lxss\{1d5b5c2a-0000-4000-8000-000000000001}",
            ("DistributionName", "Ubuntu-24.04"), ("BasePath", $@"{local}\Packages\CanonicalGroupLimited.Ubuntu24.04LTS\LocalState"), ("Version", 2))
         .File($@"{home}\.wslconfig", "[wsl2]\nmemory=8GB\n");

        b.File($@"{home}\.docker\config.json", """{ "auths": { "registry.example.com": { "auth": "YWxpY2U6c2ltdWxhdGVk" } }, "credsStore": "desktop" }""")
         .File($@"{roaming}\Docker\settings-store.json", """{ "memoryMiB": 8192 }""");

        // --- Repositories -----------------------------------------------------------------------------------
        b.GitRepository(@"D:\Projects\webapp", remoteUrl: "https://github.com/alice/webapp.git")
         .File(@"D:\Projects\webapp\CLAUDE.md", "Run npm test before committing.")
         .File(@"D:\Projects\webapp\.claude\settings.json", "{}")
         .File(@"D:\Projects\webapp\.vscode\mcp.json", """{ "servers": {} }""")
         .File(@"D:\Projects\webapp\.github\copilot-instructions.md", "Use TypeScript.")
         .File(@"D:\Projects\webapp\.gitmodules", "[submodule \"libs/shared\"]\n\tpath = libs/shared\n\turl = https://github.com/alice/shared.git\n")
         .File(@"D:\Projects\webapp\node_modules\left-pad\index.js", "module.exports = 1;")
         .File(@"D:\Projects\webapp\libs\shared\.git", "gitdir: ../../.git/modules/libs/shared\n")
         .File(@"D:\Projects\webapp\.git\modules\libs\shared\HEAD", "ref: refs/heads/main\n")
         .File(@"D:\Projects\webapp\.git\modules\libs\shared\config", "[core]\n\tbare = false\n");

        b.GitRepository(@"D:\Projects\api", branch: "develop", remoteUrl: "https://alice:ghp_SIMULATEDsimulatedSIMULATED0123456789@github.com/alice/api.git", lfs: true)
         .File(@"D:\Projects\api\.git\refs\stash", "0123456789abcdef0123456789abcdef01234567\n")
         .File(@"D:\Projects\api\.git\worktrees\api-hotfix\HEAD", "ref: refs/heads/hotfix\n")
         .File(@"D:\Projects\api\.git\worktrees\api-hotfix\commondir", "../..\n")
         .File(@"D:\Projects\api-hotfix\.git", "gitdir: D:/Projects/api/.git/worktrees/api-hotfix\n")
         .File(@"D:\Projects\api-hotfix\README.md", "# hotfix");

        b.GitRepository(@"D:\Projects\monorepo-fork", remoteUrl: "git@github.com:alice/monorepo.git")
         .File(@"D:\Projects\monorepo-fork\.git\objects\info\alternates", "D:\\Mirrors\\monorepo.git\\objects\n");

        b.File(@"D:\Mirrors\monorepo.git\HEAD", "ref: refs/heads/main\n")
         .File(@"D:\Mirrors\monorepo.git\config", "[core]\n\tbare = true\n")
         .Directory(@"D:\Mirrors\monorepo.git\objects\pack")
         .Directory(@"D:\Mirrors\monorepo.git\refs\heads");

        b.GitRepository($@"{home}\source\repos\scratch", locked: true);

        // A folder outside any fixed drive, offered as a custom discovery root.
        b.GitRepository(@"E:\Archive\old-tool", remoteUrl: "https://example.com/old-tool.git");

        // --- Things discovery must not enter, and must report ----------------------------------------------
        b.GitRepository(@"C:\Users\bob\Documents\private-repo")
         .File(@"C:\Users\bob\.claude\settings.json", "{}");
        b.Junction($@"{home}\Links\projects", @"D:\Projects");
        b.Inaccessible(@"C:\ProgramData\Restricted");
        b.Placeholder($@"{home}\OneDrive\Videos\big-recording.mp4");
        b.Directory(@"C:\$Recycle.Bin\S-1-5-21");
        b.Directory(@"C:\System Volume Information");
        b.File(@"C:\Windows\System32\wsl.exe", "MZ");
        // A git.exe inside the Windows folder (not on PATH): the filesystem pass must not enter C:\Windows.
        b.Executable(@"C:\Windows\WinSxS\amd64_git_decoy\git.exe", "1.0.0");

        return b.Build(root);
    }

    /// <summary>A newly set-up PC: Windows, Git and VS Code installed, nothing configured yet.</summary>
    public static SimulatedMachine CleanTarget(string root)
    {
        var b = new SimulatedMachineBuilder(TargetUser, "NEW-LAPTOP");
        var local = b.Folders.LocalAppData;

        b.UninstallEntry(RegistryHive.LocalMachine, RegistryView.Registry64, "Git_is1", "Git", "2.56.0", "The Git Development Community", @"C:\Program Files\Git\")
         .Executable(@"C:\Program Files\Git\cmd\git.exe", "2.56.0");
        b.UninstallEntry(RegistryHive.CurrentUser, RegistryView.Registry64, "{771FD6B0-FA20-440A-A002-3B3BAC16DC50}_is1", "Microsoft Visual Studio Code (User)", "1.106.0", "Microsoft Corporation",
            $@"{local}\Programs\Microsoft VS Code\")
         .Executable($@"{local}\Programs\Microsoft VS Code\Code.exe", "1.106.0");
        b.StorePackage("Microsoft.WindowsTerminal", "8wekyb3d8bbwe", "Windows Terminal", "1.24.12741.0")
         .StorePackage("Microsoft.DesktopAppInstaller", "8wekyb3d8bbwe", "App Installer", "1.27.0.0");
        b.Environment(RegistryHive.LocalMachine, "Path", @"%SystemRoot%\system32;%SystemRoot%;C:\Program Files\Git\cmd", expand: true)
         .Environment(RegistryHive.CurrentUser, "Path", @"%LOCALAPPDATA%\Programs\Microsoft VS Code\bin", expand: true);
        b.File($@"{b.Folders.RoamingAppData}\Code\User\settings.json", """{ "editor.fontSize": 16 }""");
        return b.Build(root);
    }
}
