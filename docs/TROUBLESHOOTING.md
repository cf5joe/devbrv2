# Troubleshooting DevBR

Start with **Activity**: it lists what DevBR did on this computer. For details, **Activity → Open logs
folder** opens the technical logs.

## Where DevBR keeps things

| What | Location |
|---|---|
| Logs | `%LOCALAPPDATA%\DevBR\logs` — `gui-<date>.log`, `worker-<date>.log`, `broker-<date>.log`; 30 days, about 100 MB in total |
| Restore reports (HTML and JSON) | `%LOCALAPPDATA%\DevBR\reports` |
| Settings | `%LOCALAPPDATA%\DevBR\settings.json` |
| Catalog, activity and restore journal | `%LOCALAPPDATA%\DevBR\state\devbr.db` |
| Rollback copies (encrypted for your Windows account) | `%LOCALAPPDATA%\DevBR\rollback` |
| Default scratch folder | `%LOCALAPPDATA%\DevBR\scratch` (change it in **Settings → Storage**) |

Logs and reports never contain passwords or setting values, so they are safe to share with whoever is
helping you. Do not share rollback copies or the backup itself.

## Windows SmartScreen or "Windows protected your PC"

- **Release builds** are Authenticode-signed. Right-click `DevBR.exe` → **Properties → Digital
  Signatures** to check the signer. Compare the ZIP's SHA-256 with the published `SHA256SUMS`
  (`Get-FileHash DevBR-<version>-win-x64.zip`).
- SmartScreen can still warn about a new signed release until it builds reputation. Choose **More info**
  only if the signature and checksum match.
- **Development builds** (sidebar badge **Development build**, ZIP name ending `-dev`) and packages
  named `-unsigned` are not signed and always trigger SmartScreen. Use them only for testing.
- If Windows blocks the files after downloading, right-click the ZIP → **Properties → Unblock** before
  extracting.
- DevBR never bypasses SmartScreen, enterprise application control (AppLocker, WDAC) or UAC. If your
  organization blocks unsigned or unknown programs, ask your administrator.

## Administrator approval (UAC) was declined

Nothing breaks. Every item that does not need administrator rights is restored; machine-wide steps
(badge **Administrator**, for example system environment variables) are reported as **Blocked**. To
finish them, plan the restore again, keep only those items, and approve the UAC prompt. **Settings →
Privileged helper → Test privileged helper** checks that the helper can start.

If no UAC prompt appears at all, your account may not be allowed to elevate; ask an administrator to
run DevBR's privileged steps, or skip the machine-wide items.

## Locked or in-use files

- **During backup**: a file in use by another program is not captured, and the result reads **Backup
  created and verified, with warnings** naming it. Close the program (editors, Docker Desktop, Git
  tools, terminals) and back up again. A repository with a Git operation in progress is flagged.
- **During restore**: preflight reports running applications whose settings would change. Close them
  and choose **Recheck**. If a file becomes locked while restoring, that item fails, its earlier changes
  are undone, and the rest of the restore continues.

## Wrong password

"That password did not open this backup. Check it and try again." Passwords are case-sensitive; check
the keyboard layout and Caps Lock. DevBR cannot recover a forgotten password and has no back door. If
you are certain the password is right, the file may be damaged: copy it again from its source.

## The backup appears damaged

"DevBR could not read the archive." Copy the `.devbr` file again, preferably verifying it against a
checksum you took on the source computer. Every file's SHA-256 is checked when it is extracted; a
damaged backup is never partly restored without a report.

## Not enough disk space

- **Backup**: DevBR checks space before it starts ("Not enough free space: staging needs about … on …
  and the backup up to … on …"). Free space, choose another **Backup file** location, or move the
  scratch folder (**Settings → Storage → Scratch folder**) to a drive with more room. If a disk fills up
  during the backup, no backup file is written.
- **Restore**: preflight checks space for extraction, replacement and rollback copies and blocks the
  affected items. Free space and choose **Recheck**.

## FAT32 drives

FAT32 cannot hold files larger than 4 GB. DevBR refuses to write a backup to a FAT32 drive when it might
exceed that ("… uses FAT32, which cannot hold files larger than 4 GB … Choose an NTFS or exFAT drive"),
and preflight blocks restoring files over 4 GB to a FAT32 destination. Reformat the USB drive as exFAT
or NTFS, or save the backup elsewhere.

## An interrupted restore

If DevBR closed during a restore (crash, power loss, forced shutdown), the next start shows **A restore
was interrupted** with **Review**. On the Restore page each unfinished change is reported as made, not
made or not confirmed. DevBR never redoes changes automatically, because repeating an uncertain change
could overwrite something. Choose one:

- **Recent restores → Roll back** to undo the restore's file and environment changes (newest first;
  anything changed since is kept and listed), or
- open the backup again, run preflight and approve a new plan; items already in place show as
  unchanged.

## Rollback did not undo everything

Rollback keeps anything you changed after the restore and lists it. Installed software (extensions,
runtimes) is never uninstalled; remove it yourself if needed. Rollback must run on the computer where
the restore happened.

## "Background archive process stopped"

Archives are read and written by a separate process, `DevBR.ArchiveWorker.exe`, so a damaged file or a
native library failure cannot close DevBR. When the worker stops, DevBR shows this banner and starts a
new worker for the next operation; the current backup or inspection is reported as failed and can be
retried.

- **Settings → Diagnostics → Background archive process → Check worker** confirms it responds.
- **Settings → Diagnostics → Archive self-test → Run self-test** creates, inspects and extracts sample
  backups with and without encryption.
- If it keeps failing, check that antivirus has not quarantined `DevBR.ArchiveWorker.exe` or
  `x64\7z.dll`, and look in `worker-<date>.log`.

## Discovery seems incomplete

Read the **Coverage** tab: it lists skipped scopes (other users' profiles are skipped unless you opt in),
junctions that were not entered, cloud-only files that were not downloaded, and folders that could not
be read. Add missing locations under **Additional folders** and choose **Run discovery again**.

## Getting help

Collect: the DevBR version (sidebar), the **Activity** entries, the relevant log files and, for a
restore, its JSON report. Do not send backups, rollback copies or passwords.
