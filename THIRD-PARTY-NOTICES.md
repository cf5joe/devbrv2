# Third-party notices

DevBR includes or depends on the following third-party components. Each is used under its own license.

| Component | License | Source |
|---|---|---|
| .NET runtime, WPF, Microsoft.Extensions.*, Microsoft.Data.Sqlite, System.Text.Json | MIT | https://github.com/dotnet |
| SQLite (via SQLitePCLRaw) | Public domain / Apache-2.0 | https://sqlite.org, https://github.com/ericsink/SQLitePCL.raw |
| CommunityToolkit.Mvvm | MIT | https://github.com/CommunityToolkit/dotnet |
| Serilog, Serilog.Sinks.File, Serilog.Extensions.Hosting | Apache-2.0 | https://github.com/serilog |
| SharpSevenZip | LGPL-3.0-or-later | https://github.com/JeremyAnsel/SharpSevenZip |
| 7-Zip (7z.dll) | GNU LGPL with unRAR restriction; BSD 3-clause for parts | https://www.7-zip.org/license.txt |
| Tomlyn | BSD-2-Clause | https://github.com/xoofx/Tomlyn |

## LGPL components

SharpSevenZip and 7-Zip are licensed under the GNU Lesser General Public License. DevBR uses them as
separate, unmodified dynamic libraries (`SharpSevenZip.dll`, `x64\7z.dll`) loaded only by
`DevBR.ArchiveWorker.exe`. You may replace these files with compatible builds of your choice. The
corresponding source code is available from the project links above.

Full license texts will be bundled with release packages (Phase 6 release deliverable).
