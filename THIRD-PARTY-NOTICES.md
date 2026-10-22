# Third-party software and licensing

The root [LICENSE](LICENSE) is the proposed **MIT license for Tenka-chan
source code that its contributors have the right to license**. It does **not**
change licenses belonging to packages, fonts, samples, trademarks or content
owned by others.

## Direct NuGet dependencies (source build)

The following list is drawn from the repository's current `PackageReference`
entries. License identifiers are pointers for the release review, **not an
exhaustive Software Bill of Materials or a substitute for the actual license
files distributed with the packages**. For a binary release, generate and
review the **transitive** dependency inventory from the restored packages and
ship all notices/license texts required for packages actually redistributed.

| Package or component | License / current reference | Role |
| --- | --- | --- |
| ModelContextProtocol 2.2.0 | [Apache-2.0 on NuGet](https://www.nuget.org/packages/ModelContextProtocol/2.2.0); upstream is transitioning from MIT, with notices for applicable contributions | MCP client/server SDK |
| Microsoft.CodeAnalysis.CSharp.Workspaces 5.9.0 | [Roslyn MIT](https://github.com/dotnet/roslyn/blob/main/License.txt) | C# source/semantic analysis |
| Microsoft.CodeAnalysis.Workspaces.MSBuild 5.9.0 | [Roslyn MIT](https://github.com/dotnet/roslyn/blob/main/License.txt) | MSBuild workspace |
| Microsoft.Build.Locator 1.11.2 and Microsoft.Build.Framework 17.11.48 | [MSBuild/Microsoft .NET licensing](https://github.com/dotnet/msbuild/blob/main/LICENSE) — verify package-specific notices before distributing binaries | MSBuild integration |
| Microsoft.Extensions.Hosting 10.0.12 | [.NET MIT](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT) | Hosting |
| Microsoft.Data.Sqlite 10.0.0 | [.NET MIT](https://github.com/dotnet/efcore/blob/main/LICENSE.txt) | Durable review history |
| SQLitePCLRaw.lib.e_sqlite3 2.1.13 | [Apache-2.0 on NuGet](https://www.nuget.org/packages/SQLitePCLRaw.lib.e_sqlite3/2.1.13); native SQLite and additional license/NOTICE files still require verification before binary redistribution | Native SQLite |
| Iced 1.21.0 | [MIT](https://github.com/icedland/iced/blob/master/LICENSE.txt) | x86/x64 disassembly |
| Microsoft.Diagnostics.Tracing.TraceEvent 3.2.8 | [MIT on NuGet](https://www.nuget.org/packages/Microsoft.Diagnostics.Tracing.TraceEvent/3.2.8) | EventPipe/TraceLog |
| Microsoft.NET.Test.Sdk 17.14.1 | [NuGet package](https://www.nuget.org/packages/Microsoft.NET.Test.Sdk/17.14.1); validate package license | Test infrastructure |
| xunit 2.9.3 / xunit.runner.visualstudio 3.1.5 | [xUnit Apache-2.0](https://github.com/xunit/xunit/blob/main/LICENSE) — check both package artifacts | Tests |
| BenchmarkDotNet | [MIT](https://github.com/dotnet/BenchmarkDotNet/blob/master/LICENSE.md); used by bundled benchmark examples/optional generated benchmarks, not an MCP server package reference | Benchmark examples |

Versions are those directly referenced at preparation time, **not a claim about
runtime/transitive package resolution**. Packages can bundle dependencies
subject to additional conditions. An Apache-2.0 library does not require
Tenka-chan's *own code* to switch to Apache-2.0, but redistribution requires
preserving applicable Apache license/NOTICE material and other obligations.

## External sample: ILSpy

ILSpy is **not vendored** in this Git repository. The documented sample
recipe in `samples/oss/ilspy.lock.json` fetches
[icsharpcode/ILSpy](https://github.com/icsharpcode/ILSpy) at a fixed commit
into the git-ignored `samples/External/` folder. ILSpy is [MIT-licensed](https://github.com/icsharpcode/ILSpy),
with its own third-party notices. Its fetched checkout, binaries, tests and
notices remain governed by upstream terms. If future distributions include
those files, preserve the appropriate notices and verify their exact contents.

## Release checklist for redistributed builds

1. Run `dotnet list <project>.csproj package --include-transitive` against the
   **restored** build and record exact versions in the release manifest.
2. Inspect each restored NuGet package's license/NOTICE metadata, including
   native packages and transitive dependencies. Retain required license texts
   with published binaries or in their distribution.
3. Verify the license rights to **all project-authored** code, names, visual
   assets and third-party contributions before merging the root MIT license.
4. Recheck this file whenever package versions, release packaging or examples
   change. Do not interpret its absence of a warning as legal clearance.
