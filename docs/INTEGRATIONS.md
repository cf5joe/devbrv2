# Supported integrations

Every migration adapter declares a support descriptor (`IToolAdapter.Support`, see
`src/DevBR.Discovery/Adapters/AdapterSupport.cs`): the host versions its handling is verified against,
the locations it reads, its capabilities, and the prerequisite rules preflight applies. The matrix below
is generated from those descriptors; a test (`IntegrationMatrixTests`) fails if they drift apart. To
regenerate it after changing a descriptor, run the tests with `DEVBR_UPDATE_DOCS=1`:

```powershell
$env:DEVBR_UPDATE_DOCS = "1"; dotnet test --project tests/DevBR.Tests --filter-class "DevBR.Tests.Discovery.IntegrationMatrixTests"
```

## Capabilities

| Capability | Meaning |
|---|---|
| inventory | Installed items (extensions, modules, distributions, policy files) are recorded for reinstall guidance |
| capture | Configuration files are backed up and restored as whole files (keep, replace, or restore alongside) |
| structured merge | Settings, servers, and profiles are merged by identity instead of replacing the file |
| path rewrite | Declared path fields are rewritten for the new computer's folders |
| dependency recipes | Extensions or MCP server runtimes are installed through validated recipes after approval |

## Version rules

Structured merges and path rewrites are *semantic* edits, and DevBR makes them only for verified versions:

- The host installed on the new computer must report a verified version. (For the Windows environment,
  the host is the Windows build.)
- If the host is not installed yet, its items are blocked until it is; the version recorded in the backup
  decides in the meantime, and preflight checks again on Recheck.
- A version recorded on the old computer must be verified too, if one was recorded.

Otherwise (an unverified version, or one DevBR cannot read), the item is still captured and listed. Restore
falls back as follows:

- Files are handled as whole files. The new computer's file is kept unless you choose to replace it or
  restore the backup's copy alongside. Files are written exactly as captured, with no merge or path rewrite.
- Environment variables and PATH entries are listed for reference only.

Preflight reports one warning per tool (`unverified-version:<tool>`) that names the version it found (or
says it could not find one) and gives the verified range. Adapters without semantic capabilities restore
whole files at any version, so nothing falls back for them.

Each adapter has a supported-version fixture and two unverified ones, outdated and unreadable, in
`tests/DevBR.Tests/Restore/AdapterSupportTests.cs`. Those tests check discovery and the planned restore
action for each fixture.

## Matrix

<!-- BEGIN GENERATED: integration matrix (IntegrationMatrix.Render) -->

| Adapter | Host (version source) | Verified versions | Capabilities | Unknown or unverified version |
|---|---|---|---|---|
| Windows environment (`environment`) | Windows build (`windows`) | >= 17763 | capture, structured merge, path rewrite | Inventory only (no variables or PATH entries are changed) |
| VS Code (`vscode`) | VS Code (`vscode`) | >= 1.90, < 2.0 | inventory, capture, structured merge, path rewrite, dependency recipes | Whole-file restore (keep, replace or restore alongside); no merge or path rewrite |
| VS Code Insiders (`vscode-insiders`) | VS Code Insiders (`vscode-insiders`) | >= 1.90, < 2.0 | inventory, capture, structured merge, path rewrite, dependency recipes | Whole-file restore (keep, replace or restore alongside); no merge or path rewrite |
| Cursor (`cursor`) | Cursor (`cursor`) | >= 1.0, < 3.0 | inventory, capture, structured merge, path rewrite, dependency recipes | Whole-file restore (keep, replace or restore alongside); no merge or path rewrite |
| GitHub Copilot CLI (`copilot`) | GitHub Copilot CLI (`copilot-cli`) | >= 0.0.330, < 2.0 | capture, structured merge, path rewrite, dependency recipes | Whole-file restore (keep, replace or restore alongside); no merge or path rewrite |
| Codex (`codex`) | Codex CLI (`codex`) | >= 0.40, < 2.0 | capture | Same as supported (whole-file restore) |
| Claude Code (`claude-code`) | Claude Code (`claude-code`) | >= 1.0, < 3.0 | inventory, capture, structured merge, path rewrite, dependency recipes | Whole-file restore (keep, replace or restore alongside); no merge or path rewrite |
| Claude Desktop (`claude-desktop`) | Claude Desktop (`claude-desktop`) | >= 0.10, < 2.0 | inventory, capture, structured merge, path rewrite, dependency recipes | Whole-file restore (keep, replace or restore alongside); no merge or path rewrite |
| Gemini CLI (`gemini-cli`) | Gemini CLI (`gemini-cli`) | >= 0.5, < 2.0 | capture, structured merge, path rewrite, dependency recipes | Whole-file restore (keep, replace or restore alongside); no merge or path rewrite |
| Git (`git`) | Git (`git`) | >= 2.30, < 3.0 | inventory, capture | Same as supported (whole-file restore) |
| GitHub CLI (`gh`) | GitHub CLI (`gh`) | >= 2.40, < 3.0 | capture | Same as supported (whole-file restore) |
| PowerShell (`powershell`) | PowerShell 7 (`pwsh`) | >= 7.2, < 8.0 | inventory, capture | Same as supported (whole-file restore) |
| Windows Terminal (`windows-terminal`) | Windows Terminal (`windows-terminal`) | >= 1.18, < 2.0 | capture, structured merge, path rewrite | Whole-file restore (keep, replace or restore alongside); no merge or path rewrite |
| WSL (`wsl`) | WSL (`wsl`) | >= 2.0, < 3.0 | inventory, capture | Same as supported (whole-file restore) |
| Docker (`docker`) | Docker Desktop (`docker-desktop`) | >= 4.30, < 5.0 | capture | Same as supported (whole-file restore) |

### Windows environment (`environment`)

Locations, in precedence order:

- `HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment`
- `HKCU\Environment`

Prerequisite rules:

- Administrator approval for machine variables
- Variables that look like credentials are excluded unless explicitly included (encrypted)
- PATH entries are held back until their folders exist

### VS Code (`vscode`)

Locations, in precedence order:

- `%APPDATA%\Code\User (and User\profiles\*)`
- `%USERPROFILE%\.vscode\extensions\extensions.json`

Prerequisite rules:

- Host installed on the new computer; otherwise its items are blocked with install guidance until Recheck
- Host closed during restore; preflight warns while it is running
- Runtimes used by MCP servers (Node.js, uv, Python, Docker) installed or approved for install
- Turn Settings Sync and accounts back on after restore

### VS Code Insiders (`vscode-insiders`)

Locations, in precedence order:

- `%APPDATA%\Code - Insiders\User (and User\profiles\*)`
- `%USERPROFILE%\.vscode-insiders\extensions\extensions.json`

Prerequisite rules:

- Host installed on the new computer; otherwise its items are blocked with install guidance until Recheck
- Host closed during restore; preflight warns while it is running
- Runtimes used by MCP servers (Node.js, uv, Python, Docker) installed or approved for install
- Turn Settings Sync and accounts back on after restore

### Cursor (`cursor`)

Locations, in precedence order:

- `%APPDATA%\Cursor\User`
- `%USERPROFILE%\.cursor`

Prerequisite rules:

- Host installed on the new computer; otherwise its items are blocked with install guidance until Recheck
- Host closed during restore; preflight warns while it is running
- Runtimes used by MCP servers (Node.js, uv, Python, Docker) installed or approved for install
- Sign in again after restore; credentials are excluded unless explicitly included (encrypted)

### GitHub Copilot CLI (`copilot`)

Locations, in precedence order:

- `%COPILOT_HOME%`
- `%XDG_CONFIG_HOME%\.copilot`
- `%USERPROFILE%\.copilot`

Prerequisite rules:

- Host installed on the new computer; otherwise its items are blocked with install guidance until Recheck
- Runtimes used by MCP servers (Node.js, uv, Python, Docker) installed or approved for install
- Sign in again after restore; credentials are excluded unless explicitly included (encrypted)

### Codex (`codex`)

Locations, in precedence order:

- `%CODEX_HOME%`
- `%USERPROFILE%\.codex`

Prerequisite rules:

- Host installed on the new computer; otherwise its items are blocked with install guidance until Recheck
- Sign in again after restore; credentials are excluded unless explicitly included (encrypted)

### Claude Code (`claude-code`)

Locations, in precedence order:

- `%CLAUDE_CONFIG_DIR%`
- `%USERPROFILE%\.claude`
- `%USERPROFILE%\.claude.json (mcpServers only)`
- `%ProgramData%\ClaudeCode\managed-settings.json (inventory only)`

Prerequisite rules:

- Host installed on the new computer; otherwise its items are blocked with install guidance until Recheck
- Runtimes used by MCP servers (Node.js, uv, Python, Docker) installed or approved for install
- Sign in again after restore; credentials are excluded unless explicitly included (encrypted)
- Organization-managed settings are never overridden

### Claude Desktop (`claude-desktop`)

Locations, in precedence order:

- `%APPDATA%\Claude`
- `%LOCALAPPDATA%\Packages\Claude_*\LocalCache\Roaming\Claude`

Prerequisite rules:

- Host installed on the new computer; otherwise its items are blocked with install guidance until Recheck
- Host closed during restore; preflight warns while it is running
- Runtimes used by MCP servers (Node.js, uv, Python, Docker) installed or approved for install
- Sign in again after restore; credentials are excluded unless explicitly included (encrypted)

### Gemini CLI (`gemini-cli`)

Locations, in precedence order:

- `%GEMINI_CLI_HOME%\.gemini`
- `%USERPROFILE%\.gemini`

Prerequisite rules:

- Host installed on the new computer; otherwise its items are blocked with install guidance until Recheck
- Runtimes used by MCP servers (Node.js, uv, Python, Docker) installed or approved for install
- Sign in again after restore; credentials are excluded unless explicitly included (encrypted)

### Git (`git`)

Locations, in precedence order:

- `%GIT_CONFIG_GLOBAL%`
- `%USERPROFILE%\.gitconfig and its include.path files`
- `%XDG_CONFIG_HOME%\git (or %USERPROFILE%\.config\git)`
- `core.excludesfile / core.attributesfile targets`
- `%ProgramFiles%\Git\etc\gitconfig (inventory only)`

Prerequisite rules:

- Credential helper sign-in on first push or pull; .git-credentials only when explicitly included (encrypted)

### GitHub CLI (`gh`)

Locations, in precedence order:

- `%GH_CONFIG_DIR%`
- `%APPDATA%\GitHub CLI`

Prerequisite rules:

- Host installed on the new computer; otherwise its items are blocked with install guidance until Recheck
- Run 'gh auth login' after restore; tokens in hosts.yml are excluded unless explicitly included (encrypted)

### PowerShell (`powershell`)

Locations, in precedence order:

- `%USERPROFILE%\Documents\PowerShell (PowerShell 7)`
- `%USERPROFILE%\Documents\WindowsPowerShell (Windows PowerShell 5.1)`
- `Scripts dot-sourced or imported by path from a profile`

Prerequisite rules:

- Host installed on the new computer; otherwise its items are blocked with install guidance until Recheck (PowerShell 7 profiles; Windows PowerShell 5.1 ships with Windows)
- Gallery modules reinstalled from the PowerShell Gallery

### Windows Terminal (`windows-terminal`)

Locations, in precedence order:

- `%LOCALAPPDATA%\Packages\Microsoft.WindowsTerminal_8wekyb3d8bbwe (LocalState, RoamingState)`
- `%LOCALAPPDATA%\Packages\Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe`
- `%LOCALAPPDATA%\Microsoft\Windows Terminal (unpackaged settings and Fragments)`

Prerequisite rules:

- Host installed on the new computer; otherwise its items are blocked with install guidance until Recheck
- Host closed during restore; preflight warns while it is running

### WSL (`wsl`)

Locations, in precedence order:

- `HKCU\Software\Microsoft\Windows\CurrentVersion\Lxss (distribution inventory)`
- `%USERPROFILE%\.wslconfig`

Prerequisite rules:

- Host installed on the new computer; otherwise its items are blocked with install guidance until Recheck
- Distributions are reinstalled (or imported from 'wsl --export') by the user

### Docker (`docker`)

Locations, in precedence order:

- `%DOCKER_CONFIG%`
- `%USERPROFILE%\.docker (config.json, contexts\meta, daemon.json)`
- `%APPDATA%\Docker (Desktop settings)`

Prerequisite rules:

- Host installed on the new computer; otherwise its items are blocked with install guidance until Recheck
- Host closed during restore; preflight warns while it is running
- Run 'docker login' again; inline registry credentials are removed at capture

<!-- END GENERATED: integration matrix -->
