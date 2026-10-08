# The .devbr archive format (version 1.0)

A `.devbr` file is a standard 7z archive: LZMA2, **non-solid** (each entry can be read on its own), and,
when encrypted, AES-256 with **header encryption** so file names are hidden without the password. The
format is versioned independently of the application; readers reject an unsupported major version
before extracting anything.

## Layout

```text
manifest.json                 format version, source machine, totals, SHA-256 of every index below
artifacts.ndjson              one line per captured item: key, artifact description, roots, status, warnings
entries.ndjson                one line per captured file or empty folder: archivePath, sha256, size, timestamps
inventory.ndjson              discovery records (when the inventory was selected); no environment values
coverage.json                 the discovery coverage report
reports/backup-report.json    outcomes, findings, warnings, exclusions and the secrets disclosure
payload/<key>/r<root>/<path>  captured bytes; <key> is a short artifact key such as a0001
payload/<key>/environment.json  captured environment variables (name, scope, registry kind, raw value)
```

Each artifact keeps its roots as **logical paths** (roaming/local AppData, profile, ProgramData,
custom root, repository root) plus the absolute source path, so restore can remap them on a different
computer or user.

## Integrity

- During capture every file is hashed while it is copied into a private staging folder.
- After compression the archive worker re-reads every payload file **from the new archive** and
  compares it with `entries.ndjson`; it also rejects payload the index does not describe. Only then is
  the archive renamed from its temporary name to the chosen file name.
- When a backup is opened, only the indexes are extracted (into a private folder) and each is checked
  against the SHA-256 in `manifest.json`. Every entry path is validated (no traversal, absolute paths,
  drive letters, alternate data streams, reserved device names or case collisions) and every record must
  belong to a listed artifact. Entry and artifact counts and the total size must equal the manifest's
  totals, and index records are read line by line with a fixed size limit.
- Version 1 archives never contain symbolic links or junctions. Archive entries flagged as links, index
  records with a link type or target, and extraction through an existing junction or link below the
  destination folder are all rejected.

Hashes detect corruption; they do not prove who made a backup. Every archive is treated as untrusted.

## What is never in an archive

Program files of installed applications, recognized credentials (unless explicitly included, which
forces encryption), sessions, histories, caches, machine identities, remembered trust approvals,
DevBR's own data, scratch and output folders, and regenerable folders excluded by the visible rules —
except where they contain files tracked by Git, which are always kept.
