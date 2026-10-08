# Performance and scale

Phase 6 targets 100 GB of selected data and one million files, with bounded memory (peak working set below
2 GB) and published, measured results rather than a promised completion time. This page records what was
measured, how to repeat it, and what is still a risk at full scale.

## What streams and what is bounded

| Path | Behaviour |
|---|---|
| Backup staging (`BackupRunner`) | Files are copied to staging in 64 KB chunks while hashed. Only files that are redacted (≤ 8 MB) or secret-scanned (≤ 1 MB, unencrypted backups) are read whole, and the read is capped even when the source cannot report its length. |
| Entry index | `entries.ndjson` is written line by line while staging; the runner keeps counters, not a list of entries. |
| Archive create / verify / extract | 7-Zip streams each entry through `BoundedHashingStream`; payload hashes are checked without buffering. |
| Opening a backup (`BackupReader`) | Extracts only the manifest and index files (never `payload/…`); covered by `StreamingTests.Opening_a_backup_reads_indexes_only_and_never_payload`. |
| Restore create/replace (`RestoreExecutor`) | The verified staged copy is streamed to the target. A file being replaced is streamed into the rollback store while it is hashed, so the copy and the "unchanged since preflight" check come from the same read. |
| Structured merges, path rewrites, JSON validation | Read into memory only up to 8 MB (the preflight comparison limit); larger files are handled as whole files and streamed. |
| Repository rollback manifest | Serialized through a private file instead of one in-memory buffer. |
| Rollback store | Encrypts and decrypts in 1 MB chunks. |
| Worker IPC | Extraction requests naming more than 256 entries (or all entries) pass the list and results through a spool folder; a single IPC frame is limited to 1 MB. |

`StreamingTests.Backup_and_restore_replace_a_large_file_without_buffering_it` backs up a 64 MB file and restores it
over a different 64 MB file, sampling the managed heap: before these changes the restore grew the heap by 337 MB,
now it stays under the 32 MB budget.

## Running the benchmark

```powershell
dotnet build tools\DevBR.Bench -c Release
$bench = "tools\DevBR.Bench\bin\x64\Release\net10.0-windows10.0.19041.0\win-x64\DevBR.Bench.exe"

# many small files
& $bench --name small --small-files 200000 --small-size 1024
# large incompressible files
& $bench --name large --large-files 4 --large-size 1073741824
```

Options: `--compression Store|Fast|Normal|Maximum` (default Normal, as in the app), `--encrypt`, `--in-process`
(use `SevenZipArchiveService` directly instead of the worker process), `--work DIR` (dataset, staging, archive and
restore target; default `%TEMP%\devbr-bench\<time>`), `--results DIR`, `--keep` (keep generated data).

The bench builds a simulated source computer, writes the dataset into it, plans a discovery-free custom-folder
backup with `BackupPlanner`, runs `BackupRunner` against `DevBR.ArchiveWorker.exe`, opens the backup with
`BackupReader`, runs preflight and `RestoreExecutor` into an empty simulated target (real SQLite journal and DPAPI
rollback store), compares every restored file by SHA-256, and times 1,000 journal intent/outcome pairs. Working
sets of the bench and the worker are sampled every 200 ms. Results are written as JSON and Markdown and the
generated data is deleted. Sub-stage times are derived from progress events and are approximate.

Disk space needed is roughly 4–5× the dataset (source, staging, archive, restore staging, target).

## Results (2026-10-08)

Machine: Intel Core Ultra 7 165H (22 logical CPUs), 63.4 GB RAM, KIOXIA KBG60ZNV1T02 NVMe SSD (NTFS), Windows 11
build 26200, .NET 10.0.12, Release build, corporate endpoint protection active. Raw output: [`perf/`](perf/).

The suggested 200,000-file and 4 GB runs were scaled down to keep each run under 15 minutes and the generated data
under 5 GB on this machine.

| Workload | Stage | Time | Throughput | Peak WS bench | Peak WS worker |
|---|---|---:|---:|---:|---:|
| 40,000 × 1 KB | backup total | 210 s | 190 files/s | 85 MB | 265 MB |
| | – staging | 125 s | 321 files/s | | |
| | – compression (Normal) | 41 s | 980 files/s | | |
| | – verification | 45 s | 891 files/s | | |
| | open backup (indexes only) | 1.3 s | 30,500 entries/s | 90 MB | 130 MB |
| | restore preflight | 2.3 s | 17,600 ops/s | 199 MB | 127 MB |
| | restore execute | 353 s | 113 files/s | 453 MB | 135 MB |
| | – extraction to staging | 83 s | 482 files/s | | |
| | – apply + journal | 236 s | 169 files/s | | |
| | – validation | 31 s | 1,280 files/s | | |
| 2 × 512 MB random | backup total | 46 s | 22 MB/s | 54 MB | **2,369 MB** |
| | – staging | 3 s | 315 MB/s | | |
| | – compression (Normal) | 40 s | 25 MB/s | | |
| | – verification | 3 s | 358 MB/s | | |
| | restore execute | 4.2 s | 246 MB/s | 77 MB | 1,080 MB |
| Journal | 1,000 intent + outcome pairs | 1.2–1.4 s | ≈ 1.3 ms per restored file | | |

Both restores completed with every file identical. Creating the 40,000 dataset files alone ran at about 560 files/s,
so per-file filesystem and anti-malware cost dominates the small-file numbers on this machine. A 5,000-file run gave
the same per-file rates, so no stage showed super-linear growth.

LZMA2 thread and memory settings for the 1 GB workload (worker peak working set):

| 7-Zip setting | Compression time | Worker peak (compress) | Worker peak (extract) |
|---|---:|---:|---:|
| `mt=on` (current) or `mt=8` | 40–42 s | 2,369 MB | 1,080 MB |
| `mt=4` | 76 s | 1,334 MB | 1,080 MB |
| `memuse=2g` | 80 s | 1,916 MB | 1,080 MB |
| `memuse=1g` | 153 s | 424 MB | 137 MB |
| Fast level, in-process | 8 s | 156 MB (one process) | 137 MB |

The current setting was kept; choosing between speed and the 2 GB target is a product decision (see below).

## Extrapolation to 100 GB and one million files

**These are linear extrapolations from the runs above, not measurements.** They assume per-file and per-byte costs
stay constant, which the 5,000 and 40,000-file runs support, but which has not been confirmed at full scale.

- **One million 1 KB files:** backup about 1.5 hours, restore about 2.5 hours on this machine (most of it per-file
  filesystem work). Restore needs about 1 million durable journal pairs, about 22 minutes of fsyncs alone.
- **100 GB incompressible at Normal:** compression at about 25 MB/s takes about 70 minutes; staging and verification
  add about 10 minutes; restore (extraction, write and validation) takes about 7–10 minutes.
- **Memory:** restore working set grew by about 9 KB per file (134 MB at 5,000 files, 453 MB at 40,000), which
  would be about 9 GB at one million files. The worker grew by about 5 KB per file during backup. **Both would miss
  the 2 GB target at one million files.**

## Bottlenecks and risks

1. **Per-file restore metadata (memory).** Restore preflight produces one `PlannedOperation`, approval effect
   key, journal entry and report row per file, and the executor's second preflight duplicates them; the entry
   index is loaded per artifact (`BackupReader.ReadEntries(…, int.MaxValue)`). To meet the 2 GB target at one million
   files, custom folders need to be planned and committed as folder-level operations (like `RestoreRepository`) with
   the file list streamed from `entries.ndjson`.
2. **Worker metadata (memory).** The worker keeps all archive entries, the expected-hash dictionary for
   verification and SharpSevenZip's file list in memory (about 5 KB per file measured).
3. **7-Zip multithreading (memory).** With `mt=on` on 22 logical CPUs, LZMA2 Normal used 2.4 GB to compress and
   1.1 GB to extract, regardless of the number of files. Capping threads or `memuse` meets the 2 GB target at a cost
   of 1.7–3.7× slower compression; the extraction peak depends on how the archive was compressed.
4. **Journal durability.** Each restored file costs two `synchronous=FULL` SQLite commits (about 1.3 ms). Journaling
   folder-level operations would remove most of this.
5. **Small-file I/O.** Backup creates every file twice (source to staging, then 7-Zip reads it), and restore creates
   it twice (extraction to staging, then an atomic temporary file and rename at the target), plus three hashing
   passes. On machines with active endpoint protection this per-file cost dominates.
6. **Verification passes.** Backup reads the new archive twice (7-Zip `Check()` and the payload hash pass); for
   large compressible data this doubles decompression time.

## Full validation on the reference machine

1. Use an NTFS volume with at least 550 GB free for the 100 GB run, and close other heavy workloads.
2. Build Release and run, one at a time:
   - `--name ref-1m --small-files 1000000 --small-size 1024 --work D:\bench`
   - `--name ref-100g --large-files 100 --large-size 1073741824 --work D:\bench`
   - optionally a mixed run (`--small-files 500000 --large-files 50`) with `--encrypt`.
3. Record the JSON and Markdown output in `docs/perf/` together with the machine's CPU, RAM, disk model and endpoint
   protection, and update the tables above. Fail the gate if any peak working set reaches 2 GB or a restored file
   differs.
