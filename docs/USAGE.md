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

## Restoring

1. **Open a backup** and choose the items to restore.
2. **Run preflight.** DevBR reads this computer (it changes nothing) and shows where each item will go,
   what will be created, merged, replaced or kept, and what blocks an item and how to fix it.
3. **Approve the plan**, then **Restore now**. DevBR checks the computer again first and stops without
   changing anything if the plan would now do something you did not approve.
4. Each item is restored on its own. If one of its changes fails, that item's earlier changes are undone
   and the rest of the restore continues. Changed settings files are merged, keeping comments and this
   computer's values unless you chose otherwise.
5. The result shows each change as restored, skipped, blocked or failed, and how far it was verified
   ("configuration applied" or "verified working"). An HTML and a JSON report are saved in
   `%LOCALAPPDATA%\DevBR\reports`; neither contains setting values.

**Rolling back.** Under **Recent restores**, *Roll back* undoes a restore's file and environment changes,
newest first. Anything you changed after the restore is kept and listed. Installed software is not
removed. Copies of replaced files are kept, encrypted for your Windows account, until the restore is
fully rolled back.

**If a restore is interrupted** (a crash or power loss), DevBR checks the unfinished changes the next time
you open Restore: each is reported as made, not made, or not confirmed. Nothing is redone automatically;
roll it back or plan the restore again.

## Administrator approval

DevBR runs as a standard user. Only specific machine-wide steps, such as system environment variables,
use a separate privileged helper that Windows asks you to approve. If you decline, everything that does
not need administrator rights still works, and the privileged steps are marked as blocked.

## Development builds

Builds marked **Development build** in the sidebar are unsigned and intended for testing. Windows
SmartScreen may warn before running them.
