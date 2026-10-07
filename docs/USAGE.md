# Using DevBR

DevBR is portable: extract the ZIP to any folder you can write to (including a removable drive) and run
`DevBR.exe`. No installation and no separately installed .NET runtime are required. To update, replace
the folder with a newer one.

## What happens when you start DevBR

Nothing is scanned and nothing is sent anywhere. DevBR shows two choices:

- **Discover this computer** inventories installed tools, settings and repositories once you start it.
- **Open a backup** reads a `.devbr` file's index and manifest so you can see what it contains.
  Opening a backup never extracts or runs anything inside it.

## Where DevBR keeps its data

`%LOCALAPPDATA%\DevBR` holds settings, the local catalog, activity history, logs (kept 30 days, about
100 MB at most) and rollback records. Large operations stage data in a scratch folder you can change in
**Settings → Storage**.

## Administrator approval

DevBR runs as a standard user. Only specific machine-wide steps, such as system environment variables,
use a separate privileged helper that Windows asks you to approve. If you decline, everything that does
not need administrator rights still works, and the privileged steps are marked as blocked.

## Development builds

Builds marked **Development build** in the sidebar are unsigned and intended for testing. Windows
SmartScreen may warn before running them.
