# Third-party notices

DevBR includes or depends on the following third-party components. Each is used under its own license.
Full license texts are in the `licenses` folder of every release package (`build/licenses` in the source
tree). The machine-readable list of shipped packages, with versions and hashes, is the CycloneDX SBOM
(`sbom.cdx.json`) in the same package.

| Component | Version | License | License text | Source |
|---|---|---|---|---|
| .NET runtime and Windows Desktop runtime (WPF), bundled self-contained | 10.0.x (see SBOM) | MIT | `MIT.txt` | https://github.com/dotnet/runtime, https://github.com/dotnet/wpf |
| Microsoft.Extensions.Hosting, Microsoft.Extensions.* (configuration, DI, logging, options) | 10.0.12 | MIT | `MIT.txt` | https://github.com/dotnet/runtime |
| Microsoft.Data.Sqlite, Microsoft.Data.Sqlite.Core | 10.0.12 | MIT | `MIT.txt` | https://github.com/dotnet/efcore |
| System.Security.Cryptography.ProtectedData, System.Diagnostics.EventLog | 10.0.12 | MIT | `MIT.txt` | https://github.com/dotnet/runtime |
| SQLitePCLRaw (bundle_e_sqlite3, core, lib.e_sqlite3, provider.e_sqlite3) | 2.1.12 | Apache-2.0 | `Apache-2.0.txt` | https://github.com/ericsink/SQLitePCL.raw |
| SQLite (native `e_sqlite3.dll`) | via SQLitePCLRaw | Public domain | — | https://sqlite.org/copyright.html |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | `MIT.txt` | https://github.com/CommunityToolkit/dotnet |
| Serilog, Serilog.Extensions.Hosting, Serilog.Extensions.Logging, Serilog.Sinks.File | 4.3.0 / 10.0.0 / 10.0.0 / 7.0.0 | Apache-2.0 | `Apache-2.0.txt` | https://github.com/serilog |
| SharpSevenZip | 2.0.128 | LGPL-3.0-or-later | `LGPL-3.0-or-later.txt` | https://github.com/JeremyAnsel/SharpSevenZip |
| 7-Zip (`x64\7z.dll`, shipped inside the SharpSevenZip package) | 26.03 | GNU LGPL with unRAR restriction; BSD 3-clause and BSD 2-clause for parts | `7-Zip.txt`, `LGPL-3.0-or-later.txt`, `BSD-3-Clause.txt`, `BSD-2-Clause.txt` | https://www.7-zip.org/license.txt, https://github.com/ip7z/7zip |
| Tomlyn | 2.10.1 | BSD-2-Clause | `BSD-2-Clause.txt` | https://github.com/xoofx/Tomlyn |

The .NET runtime's own third-party notices are published at
https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT.

Build-time tools that are not shipped (the .NET SDK, xUnit and the test SDK, the CycloneDX SBOM generator
pinned in `.config/dotnet-tools.json`) are not redistributed and are not listed here.

## LGPL components

SharpSevenZip and 7-Zip are licensed under the GNU Lesser General Public License. DevBR uses them as
separate, unmodified dynamic libraries (`SharpSevenZip.dll`, `x64\7z.dll`) loaded only by
`DevBR.ArchiveWorker.exe`. You may replace these files with compatible builds of your choice. The
corresponding source code is available from the project links above. The LGPL text in the `licenses`
folder includes the GNU GPL version 3 that it incorporates.

## unRAR restriction (7-Zip)

7-Zip's RAR decompression code may not be used to develop a RAR (WinRAR) compatible archiver. DevBR
creates and reads only its own 7z-based `.devbr` archives; see `licenses\7-Zip.txt` for the full terms.
