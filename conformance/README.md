# Conformance

OneModelContext is checked against the protocol's official conformance suite, run as a black box: it is installed from its published
package and driven through its command line. Its files are never read or copied into this repository.

| | |
|---|---|
| Suite | `@modelcontextprotocol/conformance` **0.2.0-alpha.12** (npm) |
| Licence | MIT (from the package's npm metadata) |
| Revisions with scenarios | 2025-11-25 and 2026-07-28 as requirement sets (`--requirements`), plus scenarios that apply to 2025-03-26, 2025-06-18 and extensions |
| Not covered by the suite | 2024-11-05 has no scenarios |

The newest stable release, 0.1.16, has no 2026-07-28 scenarios, which is why an alpha is pinned. A newer suite does not retroactively
change what a released version of this library was checked against.

## How the suite tests

- **Servers** are tested over HTTP only: `server --url <endpoint>`. The suite connects as a client; there is no stdio server mode.
- **Clients** are tested by running them: `client --command "<command>"`. The suite starts a test server, appends its URL to the command
  and sets `MCP_CONFORMANCE_SCENARIO` (and `MCP_CONFORMANCE_PROTOCOL_VERSION` when a version is requested).
- `--scenario <name>` runs one scenario; `--requirements <revision>` runs what a revision requires, frozen at its release.

## Running it

```sh
export ONEMODELCONTEXT_CONFORMANCE_CACHE=/some/directory/outside/the/repository
conformance/run.sh install                                     # once; installs the pinned suite there
conformance/run.sh list                                        # the suite's scenarios
conformance/run.sh server --url http://127.0.0.1:PORT/mcp --out results --scenario tools-list
conformance/run.sh client --command "dotnet run --project ..." --out results --requirements 2026-07-28
conformance/run.sh regression --out results --url http://127.0.0.1:PORT/mcp --command "..."
```

`regression` runs the scenarios named in [scenarios.txt](scenarios.txt), which is what continuous integration runs. Each line names
the revision whose variant of the scenario runs (`--spec-version`). Servers must bind a loopback address and a free port; read the
port back instead of fixing one.

## The conformance server

`conformance/Server` is a small program that serves a `ContextServer` full of the tools, resources and prompts the scenarios call.
It ships in no package.

```sh
dotnet run --project conformance/Server -- --http --port 0     # Streamable HTTP on 127.0.0.1; prints the URL
dotnet run --project conformance/Server -- --stdio             # newline-delimited JSON-RPC on stdin and stdout
```

The suite has no 2024-11-05 scenarios, and `--requirements` exists only for 2025-11-25 and 2026-07-28; the 2025-03-26 and 2025-06-18
variants run with `--scenario ... --spec-version`, or `--spec-version` alone for every applicable scenario.

## Results (suite 0.2.0-alpha.12, server over Streamable HTTP)

| Run | Result |
|---|---|
| `--requirements 2025-11-25` | 80 checks passed, 0 failed (pending and added-after-release scenarios pass too) |
| `--requirements 2026-07-28` | every scenario outside the extension scenarios passes; the Tasks scenarios are not run by CI |
| `--spec-version 2025-06-18` | 56 checks passed, 0 failed |
| `--spec-version 2025-03-26` | `server-session-lifecycle`, 3 of 3 |

The two `list_changed` checks of `server-stateless` are warnings (the suite calls them SHOULD requirements): the suite mutates the
server's lists and expects a notification on an open `subscriptions/listen` stream, and this server does not yet send one.

## The suite's scenario list (`list` output, 0.2.0-alpha.12)

Bracketed revisions are where a scenario applies.

```text
Server scenarios (test against a server):
  - server-initialize [2025-06-18,2025-11-25]
  - server-session-lifecycle [2025-03-26,2025-06-18,2025-11-25]
  - server-stateless [2026-07-28]
  - logging-set-level [2025-06-18,2025-11-25]
  - ping [2025-06-18,2025-11-25]
  - completion-complete [2025-06-18,2025-11-25,2026-07-28]
  - tools-list [2025-06-18,2025-11-25,2026-07-28]
  - tools-call-simple-text [2025-06-18,2025-11-25,2026-07-28]
  - tools-call-image [2025-06-18,2025-11-25,2026-07-28]
  - tools-call-audio [2025-06-18,2025-11-25,2026-07-28]
  - tools-call-embedded-resource [2025-06-18,2025-11-25,2026-07-28]
  - tools-call-mixed-content [2025-06-18,2025-11-25,2026-07-28]
  - tools-call-with-logging [2025-06-18,2025-11-25]
  - tools-call-error [2025-06-18,2025-11-25,2026-07-28]
  - tools-call-with-progress [2025-06-18,2025-11-25,2026-07-28]
  - tools-call-sampling [2025-06-18,2025-11-25]
  - tools-call-elicitation [2025-06-18,2025-11-25]
  - json-schema-2020-12 [2025-11-25,2026-07-28]
  - elicitation-sep1034-defaults [2025-11-25]
  - server-sse-polling [2025-11-25]
  - server-sse-multiple-streams [2025-11-25,2026-07-28]
  - elicitation-sep1330-enums [2025-11-25]
  - resources-list [2025-06-18,2025-11-25,2026-07-28]
  - resources-read-text [2025-06-18,2025-11-25,2026-07-28]
  - resources-read-binary [2025-06-18,2025-11-25,2026-07-28]
  - resources-templates-read [2025-06-18,2025-11-25,2026-07-28]
  - resources-subscribe [2025-06-18,2025-11-25]
  - resources-unsubscribe [2025-06-18,2025-11-25]
  - sep-2164-resource-not-found [2026-07-28]
  - sep-2640-skills-directory [extension]
  - sep-2640-skills-enumeration [extension]
  - sep-2640-skills-manifest [extension]
  - prompts-list [2025-06-18,2025-11-25,2026-07-28]
  - prompts-get-simple [2025-06-18,2025-11-25,2026-07-28]
  - prompts-get-with-args [2025-06-18,2025-11-25,2026-07-28]
  - prompts-get-embedded-resource [2025-06-18,2025-11-25,2026-07-28]
  - prompts-get-with-image [2025-06-18,2025-11-25,2026-07-28]
  - dns-rebinding-protection [2025-11-25,2026-07-28]
  - caching [2026-07-28]
  - http-header-validation [2026-07-28]
  - http-custom-header-server-validation [2026-07-28]
  - tasks-lifecycle [extension]
  - tasks-capability-negotiation [extension]
  - tasks-wire-fields [extension]
  - tasks-request-state-removal [extension]
  - tasks-mrtr-input [extension]
  - tasks-request-headers [extension]
  - tasks-dispatch-and-envelope [extension]
  - tasks-status-notifications [extension]
  - tasks-required-task-error [extension]
  - tasks-mrtr-composition [extension]
  - input-required-result-basic-elicitation [2026-07-28]
  - input-required-result-basic-sampling [2026-07-28]
  - input-required-result-basic-list-roots [2026-07-28]
  - input-required-result-request-state [2026-07-28]
  - input-required-result-multiple-input-requests [2026-07-28]
  - input-required-result-multi-round [2026-07-28]
  - input-required-result-missing-input-response [2026-07-28]
  - input-required-result-non-tool-request [2026-07-28]
  - input-required-result-result-type [2026-07-28]
  - input-required-result-unsupported-methods [2026-07-28]
  - input-required-result-tampered-state [2026-07-28]
  - input-required-result-capability-check [2026-07-28]
  - input-required-result-ignore-extra-params [2026-07-28]
  - input-required-result-validate-input [2026-07-28]

Client scenarios (test against a client):
  - initialize [2025-06-18,2025-11-25]
  - tools_call [2025-06-18,2025-11-25,2026-07-28]
  - elicitation-sep1034-client-defaults [2025-11-25]
  - sse-retry [2025-11-25]
  - request-metadata [2026-07-28]
  - auth/metadata-default [2025-11-25,2026-07-28]
  - auth/metadata-var1 [2025-11-25,2026-07-28]
  - auth/metadata-var2 [2025-11-25,2026-07-28]
  - auth/metadata-var3 [2025-11-25,2026-07-28]
  - auth/basic-cimd [2025-11-25,2026-07-28]
  - auth/scope-from-www-authenticate [2025-11-25,2026-07-28]
  - auth/scope-from-scopes-supported [2025-11-25,2026-07-28]
  - auth/scope-omitted-when-undefined [2025-11-25,2026-07-28]
  - auth/scope-step-up [2025-11-25,2026-07-28]
  - auth/scope-retry-limit [2025-11-25,2026-07-28]
  - auth/token-endpoint-auth-basic [2025-06-18,2025-11-25,2026-07-28]
  - auth/token-endpoint-auth-post [2025-06-18,2025-11-25,2026-07-28]
  - auth/token-endpoint-auth-none [2025-06-18,2025-11-25,2026-07-28]
  - auth/pre-registration [2025-11-25,2026-07-28]
  - auth/2025-03-26-oauth-metadata-backcompat [2025-03-26]
  - auth/2025-03-26-oauth-endpoint-fallback [2025-03-26]
  - auth/resource-mismatch [2026-07-28]
  - auth/offline-access-scope [2026-07-28]
  - auth/offline-access-not-supported [2026-07-28]
  - auth/authorization-server-migration [2026-07-28]
  - auth/iss-supported [2026-07-28]
  - auth/iss-not-advertised [2026-07-28]
  - auth/iss-supported-missing [2026-07-28]
  - auth/iss-wrong-issuer [2026-07-28]
  - auth/iss-unexpected [2026-07-28]
  - auth/iss-normalized [2026-07-28]
  - auth/metadata-issuer-mismatch [2026-07-28]
  - auth/client-credentials-jwt [extension]
  - auth/client-credentials-basic [extension]
  - auth/enterprise-managed-authorization [extension]
  - auth/dpop [extension]
  - auth/dpop-nonce [extension]
  - auth/wif-jwt-bearer [extension]
  - sep-2322-client-request-state [2026-07-28]
  - http-standard-headers [2026-07-28]
  - http-custom-headers [2026-07-28]
  - http-invalid-tool-headers [2026-07-28]
  - json-schema-ref-no-deref [2026-07-28]
  - json-schema-2020-12-preservation [2025-11-25,2026-07-28]
  - sep-2640-client-no-prefetch [extension]
  - sep-2640-client-verify-digest [extension]
  - sep-2640-client-verify-size [extension]
  - sep-2640-client-verify-frontmatter [extension]

Authorization server scenarios (test against an authorization server):
  - authorization-server-metadata-endpoint [2025-03-26,2025-06-18,2025-11-25,2026-07-28]
  - authorization-code-grant [2025-03-26,2025-06-18,2025-11-25,2026-07-28]
```
