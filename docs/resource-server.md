# Resource server

An MCP server that listens on HTTP can require OAuth 2.1 bearer tokens. OneModelContext plays the resource server role: it
serves protected resource metadata, challenges requests without a valid token, and checks that every token was issued for this
server. Authentication is optional; stdio servers take their credentials from the environment and do not use any of this.

```csharp
var validator = new JwtTokenValidator("https://auth.example.com", new HttpJwksProvider(new Uri("https://auth.example.com/jwks")));
var resourceServer = new ResourceServer(new ResourceServerOptions(new Uri("https://mcp.example.com/mcp"), validator)
{
    AuthorizationServers = ["https://auth.example.com"],
    ScopesSupported = ["files:read", "files:write"],
});

// Built-in endpoint (any host that passes requests to StreamableHttpEndpoint):
var endpoint = new StreamableHttpEndpoint(server, new StreamableHttpOptions { ResourceServer = resourceServer });

// ASP.NET Core:
builder.Services.AddAuthentication(ResourceServerDefaults.AuthenticationScheme).AddResourceServer(resourceServer);
builder.Services.AddAuthorization();
app.UseAuthentication(); app.UseAuthorization();
app.MapModelContext("/mcp", server, new StreamableHttpOptions { ResourceServer = resourceServer }).RequireAuthorization();
```

## What the endpoint does

| Request | Answer |
|---|---|
| `GET` of `/.well-known/oauth-protected-resource` or the same path with the resource's own path appended | 200, the metadata document (RFC 9728); public |
| any other request, no `Authorization` header or a scheme other than `Bearer` | 401, `WWW-Authenticate: Bearer resource_metadata="…"` (plus `scope="…"` when the server has required scopes) |
| two `Authorization` headers | 400 `invalid_request` |
| a token that is not valid for this server | 401, `error="invalid_token"` |
| a valid token without the server-wide scopes, or without the scopes of the tool, resource or prompt being used | 403, `error="insufficient_scope"`, `scope="…"` listing every scope the operation needs, `resource_metadata` |
| the issuer or introspection endpoint cannot be reached | 503 |

Per-operation scopes are the `RequiredScopes` property of `ToolDefinition`, `ResourceDefinition`, `ResourceTemplateDefinition` and
`PromptDefinition`. A `ScopeHierarchy` lets a broad scope (`admin`) satisfy narrower ones. Batches (2025-03-26 only) are refused as a whole
if any request in them lacks its scopes. Both protocol eras get the same checks; legacy sessions belong to the subject that created them, and
another caller presenting the session id gets 404.

The resource is configured, never derived from the `Host` header. Tokens must name it in `aud` (RFC 8707); the comparison lower-cases scheme
and host and drops a default port, and is otherwise exact. A token is never passed on to anything: handlers get a `ClaimsPrincipal` (`sub`,
`iss`, `client_id`, one `scope` claim per scope), available as `RequestContext.Principal` and in the audit record.

## Validators

| Validator | Notes |
|---|---|
| `JwtTokenValidator` | RS/PS/ES 256, 384 and 512 on BCL crypto. `iss` exact, `aud` = the resource, `exp` required, `nbf`/`iat` with at most 5 minutes of skew (default 60 s). Everything else, `none` and HMAC included, is refused. For EdDSA issuers, supply a custom `ITokenValidator` or `DelegateTokenValidator`; OneCrypto-based support is planned. |
| `HttpJwksProvider` | HTTPS only (HTTP on loopback), no redirects, 256 KiB and 10 s limits, cached 10 minutes. An unknown `kid` triggers one refetch, never more than one fetch per 30 s. A failed refresh keeps serving the cached keys. |
| `IntrospectionTokenValidator` | RFC 7662, HTTP Basic or bearer client authentication. Needs `active: true` and an `aud` naming the resource. Active answers are cached for at most 60 s and never past `exp`; inactive ones are not cached. |
| `StaticTokenValidator` | Per-user opaque tokens kept as SHA-256 hashes; every entry is compared in constant time. Tokens must be at least 16 characters. |
| `DelegateTokenValidator` | Your own check. Implement `ITokenValidator` for anything richer. |

Validator failures carry a short `Description` that is safe to send to the caller. Tokens are never logged, never part of an exception
message or a diagnostic event, and a description that contains the presented token is scrubbed before it is used.

## Legacy clients

The 2025-06-18 and 2025-11-25 revisions use the same metadata document and challenge. The 2025-03-26 revision predates protected resource
metadata: its clients look for authorization server metadata on the MCP server's own origin, which a resource server that delegates to a
separate authorization server does not serve.
