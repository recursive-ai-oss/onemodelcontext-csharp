# Third-party notices

OneModelContext's own code is licensed `MIT OR Apache-2.0`. No package contains third-party code. The core package,
`RecursiveAI.OneModelContext`, depends on the .NET base class library only. Each adapter package depends on the core package and on the one
library it adapts, as a package dependency (nothing is copied into it):

| Package | Depends on | Licence |
|---|---|---|
| `RecursiveAI.OneModelContext.AspNetCore` | The ASP.NET Core shared framework (`Microsoft.AspNetCore.App`, a framework reference) | MIT |
| `RecursiveAI.OneModelContext.Hosting` | `Microsoft.Extensions.Hosting.Abstractions` | MIT |
| `RecursiveAI.OneModelContext.Extensions.AI` | `Microsoft.Extensions.AI.Abstractions` | MIT |
| `RecursiveAI.OneModelContext.EntityFrameworkCore` | `Microsoft.EntityFrameworkCore` and `Microsoft.EntityFrameworkCore.Relational` | MIT |
| `RecursiveAI.OneModelContext.OneLite` | `RecursiveAI.OneLite`, another Recursive AI library | MIT OR Apache-2.0 |
| `RecursiveAI.OneModelContext.Cli` | The core package only | |
| `RecursiveAI.OneModelContext.Templates` | Nothing | |

The `RecursiveAI.OneModelContext` package also carries its source generator as an analyzer. The generator is built with
`Microsoft.CodeAnalysis.CSharp` (MIT), which stays a build-time input and is not distributed.

Build and test tooling used by this repository, none of which is distributed in the packages:

- `xunit.v3` (Apache-2.0), referenced by the test projects only.
- `Microsoft.EntityFrameworkCore.Sqlite` (MIT), referenced by the EF Core adapter's tests only.
- `@modelcontextprotocol/conformance`, the protocol's conformance suite, installed from npm by `conformance/run.sh` and run as a separate
  process; nothing from it is copied into this repository. Its licence is recorded in `conformance/README.md`.
- The interop and benchmark projects under `interop/` and `bench/` restore other SDKs and servers from their package feeds and run them as
  separate programs; they are not part of any package. Versions and licences are listed in their READMEs.
- `SharpFuzz` (MIT), referenced by the fuzz targets only.
