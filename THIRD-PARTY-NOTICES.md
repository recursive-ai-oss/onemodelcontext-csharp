# Third-party notices

OneModelContext's own code is licensed `MIT OR Apache-2.0`. The shipped packages contain no third-party code and depend on the .NET base
class library only.

Build and test tooling used by this repository, none of which is distributed in the packages:

- `Microsoft.CodeAnalysis.CSharp` (MIT), referenced by the source generator at build time only.
- `xunit.v3` (Apache-2.0), referenced by the test project only.
- `@modelcontextprotocol/conformance`, the protocol's conformance suite, installed from npm by `conformance/run.sh` and run as a separate
  process; nothing from it is copied into this repository. Its licence is recorded in `conformance/README.md`.

The adapter packages reference one library each, as a package dependency (nothing is copied into them). `RecursiveAI.OneModelContext.EntityFrameworkCore`
references `Microsoft.EntityFrameworkCore` and `Microsoft.EntityFrameworkCore.Relational` (MIT); its tests use `Microsoft.EntityFrameworkCore.Sqlite` (MIT).
