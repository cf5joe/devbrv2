# DevBR migration guide

This guide walks through moving a developer setup from one Windows 11 x64 computer (the **source**) to
another (the **target**) with DevBR. It follows the journey DevBR is built around:

**Discover → Review → Select → Back up → Transfer → Inspect → Preflight → Restore → Verify**

Labels in **bold** are the exact words shown in the app. If something goes wrong, see
[TROUBLESHOOTING.md](TROUBLESHOOTING.md).

## What DevBR moves, and what it does not

DevBR backs up configuration and personal development assets: editor and terminal settings, AI tool
configuration (MCP servers, agents, skills, hooks, instructions), Git and GitHub CLI settings,
PowerShell profiles, custom environment variables and PATH entries, local Git repositories (with
history, branches, stashes, local edits and untracked files), and any files or folders you add.

It does **not** copy installed programs. It records what is installed so the target can tell you what to
reinstall, and it can install a small set of supported dependencies (for example editor extensions)
once you approve a plan. Sign-ins, credential stores and machine-bound caches are never copied; you sign
in again on the target.

## Before you start

- Install nothing: extract the DevBR ZIP to a folder you can write to (a local folder or a USB drive)
  and run `DevBR.exe`. Use the same DevBR version on both computers.
- Release builds are signed. A sidebar badge reading **Development build** means an unsigned test build.
- DevBR runs as a standard user. Administrator approval (UAC) is requested only for specific
  machine-wide steps during a restore.
- Choose where the backup will travel: an NTFS or exFAT USB drive, a network share or any other copy
  method. FAT32 drives cannot hold files over 4 GB.

Starting DevBR does not scan anything or touch the network. The **Overview** page offers two actions:
**Discover this computer** and **Open a backup**. The sidebar has **Overview · Discovery · Backup ·
Restore · Activity · Settings** (Ctrl+1 … Ctrl+6).

## On the source computer

### 1. Discover

1. Choose **Discover this computer** on Overview, or open **Discovery**.
2. Under **Scan scope**, **All fixed local drives** is always scanned. Turn on **Include other users'
   profiles** only if you need them (off by default). Use **Add folder…** under **Additional folders** for
   removable, network or other folders you want searched.
3. Choose **Start discovery**. Progress shows **Found so far** and counts **By drive**; **Cancel** stops at
   any time and keeps what was found.

Discovery never launches programs it finds, never follows junctions and never downloads cloud-only
files. If an InfoBar says **These results are incomplete**, see the **Coverage** tab.

### 2. Review

- The **Inventory** tab lists every record. Use **Search** (name, version, publisher or path),
  **Category**, **Scope** and **Sort by**; **Clear filters** resets them.
- Select a record to see its **Migration support**, **Locations**, **Evidence** and **Details**.
- The **Coverage** tab lists every source that was searched, scopes that were skipped and why, and
  locations that were **Not entered** or **Could not be read**.

### 3. Select

Open the **Backup selection** tab. By default only the inventory and the non-secret machine environment
are selected (marked **Default**); repositories and personal files are never selected automatically.

- Tick the settings, AI configurations, repositories and other items you want to move.
- Items that contain credentials or secrets show an InfoBar: **Encryption will be required**.
- **Reset to defaults** returns to the baseline selection.

### 4. Back up

Open **Backup**. The wizard has six steps: **Select · Add files · Review · Output · Confirm · Back up**.

1. **Select**: **What will be backed up** lists your selection. **Change selection** returns to
   Discovery. If you have not run discovery yet, choose **Go to Discovery**.
2. **Add files**: under **Add your own files and folders** use **Add folder…** or **Add files…** for
   scripts, notes, dotfiles or project folders discovery did not offer.
3. **Review**: check the size summary, findings and **Excluded folders** (regenerable folders such as
   `node_modules`; a folder with Git-tracked files is always kept). Change the ticks and choose
   **Update plan** if needed. If you see **This backup must be encrypted**, you will set a password next.
4. **Output**: under **Where and how to save it** set the **Backup file** (default
   `Documents\DevBR Backups`) or choose **Browse…**, pick a **Compression** level and, if wanted or
   required, tick **Encrypt with a password (AES-256; file names are hidden too)** and enter
   **Password** and **Confirm password** (at least 8 characters). DevBR cannot recover a forgotten
   password. If a file with that name exists, you must tick the replace option explicitly.
   An unencrypted backup shows **Not encrypted**: custom files and Git history can contain secrets
   detection misses.
5. **Confirm**: **Confirm the plan** shows exactly what will be captured. Close editors, Docker Desktop and
   Git tools for the most consistent result, then choose **Back up now**.
6. **Back up**: progress shows copying, compressing and **Verifying every file in the backup**. **Cancel**
   removes the incomplete output. The result reads **Backup created and verified** (or **…with
   warnings** — read them; for example a file in use by another program is not captured). Use **Open
   folder** to find the `.devbr` file.

Large backups stage data in the scratch folder (**Settings → Storage → Scratch folder**). Choose a local
drive with plenty of free space.

## Transfer

Copy the single `.devbr` file to the target computer by any means. DevBR has no cloud upload. If the
backup is encrypted, transfer the password separately.

## On the target computer

### 5. Inspect

1. Extract and run DevBR, then choose **Open a backup** (Overview) or **Restore → Browse for a backup…**.
2. If the backup is encrypted, **This backup is encrypted** appears: enter the **Password** and choose
   **Unlock**. Encrypted backups show nothing (not even file names) until unlocked.
3. **Backup overview** shows the source machine, date, DevBR version, item counts, size and any
   **Captured with a warning** items. **What this backup contains** lists the categories.

Opening a backup reads only its index and manifest in an isolated background process. Nothing inside
it is extracted or run.

### 6. Preflight

1. Under **Plan the restore** (it names the computer you are **Restoring to**), tick the **Items to
   restore**.
2. Choose **Run preflight**. Preflight only reads this computer; nothing changes.
3. **Destinations** shows where each source location goes (for example `C:\Users\alice` → your profile).
   Use **Change…** to map a root elsewhere, such as `C:\Projects` → `D:\Projects`.
4. **Preflight findings** explain each problem, why it matters and what to do: a missing host
   application to install yourself, a running program to close, a destination conflict, missing disk
   space, a sign-in you must redo. Fix what you can and choose **Recheck**. Independent items stay
   ready even if others are blocked.

### 7. Review and approve

**Review the plan** groups every change. For each one:

- Badges: **Administrator** (needs UAC approval), **Cannot be rolled back** (installers), **Held back**
  (disabled until a prerequisite is resolved).
- Where a file already exists, choose **Merge (keep my values)** (default for supported settings),
  **Keep this computer's file**, **Use the backup's version** or **Restore next to it**.
- Expanders list **Paths rewritten for this computer**, **MCP servers in this backup** (unsupported
  commands stay manual) and **Software to reinstall yourself**.

Choose **Approve this plan**. Changing a choice or rerunning preflight can invalidate the approval if new
effects appear; approve again.

### 8. Restore

Choose **Restore now** and confirm. DevBR checks the computer again first and stops without changing
anything if the plan would now do something you did not approve. While **Restoring…**, **Cancel** stops
after the current change; changes already made are recorded and can be rolled back.

If a step needs administrator rights, Windows shows a UAC prompt for the DevBR privileged helper. If you
decline, user-level items still restore and the privileged ones are reported as blocked.

### 9. Verify

The result lists each change as **Restored**, **Restored · verified working**, **Restored · check
failed**, **Skipped**, **Blocked** or **Failed**. "Restored" means the configuration was applied;
"verified working" means DevBR also confirmed it functionally. **Open report** opens the HTML report;
HTML and JSON reports are saved under `%LOCALAPPDATA%\DevBR\reports` without setting values.

Then:

1. Install the applications listed under **Software to reinstall yourself**.
2. Sign in again to the tools listed in the findings (for example `gh auth login`, `codex login`).
3. Open a new terminal so new environment variables and PATH entries apply.
4. Check a restored repository: `git status`, `git branch -a`, `git stash list`.

### Rolling back

**Restore → Recent restores → Roll back** undoes that restore's file and environment changes, newest
first, and keeps anything changed since. Installed software is not removed. **Report** reopens its
report.

## Privacy

DevBR has no account, telemetry or cloud service. Logs (`%LOCALAPPDATA%\DevBR\logs`) and reports never
contain passwords or setting values. Credentials are only backed up if you include them explicitly,
which forces encryption.
