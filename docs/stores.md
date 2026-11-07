# Entity Framework Core stores

`RecursiveAI.OneModelContext.EntityFrameworkCore` keeps the authorization server's state and a client's tokens in your own database, so
several instances of an application can share them. It references the core package and Entity Framework Core (the base package and
`Microsoft.EntityFrameworkCore.Relational`) and nothing else; your provider package is yours to choose.

| Store | Implements | Tables |
|---|---|---|
| `EfAuthorizationStore<TContext>` | `IAuthorizationStore` | `…Clients`, `…AuthorizationCodes`, `…RefreshTokens`, `…Revocations`, `…SeenAssertions` |
| `EfTokenStore<TContext>` | `ITokenStore` | `…TokenRecords` |
| `EfTaskStore<TContext>` | `ITaskStore` (Tasks extension, experimental) | `…Tasks` |

Table names start with `ModelContext` (change it with the `tablePrefix` argument; `schema` sets the database schema).

## Setting up

```csharp
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.ApplyContextStores();      // or ApplyAuthorizationStore() / ApplyTokenStore() / ApplyTaskStore()
}

services.AddDbContextFactory<AppDbContext>(o => o.UseNpgsql(connectionString));
var store = new EfAuthorizationStore<AppDbContext>(factory);   // one per process; it opens a context per call

var server = new AuthorizationServer(new AuthorizationServerOptions { /* … */ Store = store });
var tokens = new EfTokenStore<AppDbContext>(factory, protector);   // protector: an optional ITokenProtector
```

Create the tables with `Database.EnsureCreated()` for a database that only this model uses, or add a migration
(`dotnet ef migrations add AddContextStores`) to a database you already migrate. The row types are internal, so the migration is generated
from the model your context builds; no hand-written SQL is needed.

## What holds across instances

- **A code redeems once.** Redeeming is one statement, `UPDATE … SET redeemed = 1 WHERE code_hash = @h AND redeemed = 0`, and the number
  of rows it changed says who won. Of two instances racing on one code, one gets `Redeemed` and the other `AlreadyRedeemed`, which makes
  the authorization server revoke the family exactly as it does with the memory store.
- **A refresh token rotates once**, the same way, so a rotated token used from another instance is recognised as reuse.
- **An assertion identifier is accepted once** (`client_assertion` and ID-JAG `jti`): the first insert wins on the primary key; a record
  that has expired is taken over by one conditional update.
- **Revocations keep the later expiry**; saves of the same client, code or token overwrite.
- Everything else is the memory store's behaviour, including that an expired code or token is spent when it is presented.

The authorization server itself keeps no state between requests beyond the store, but both instances need the same signing keys and the
same `ResumeKey`.

## Tasks across instances

`EfTaskStore<TContext>` is the store to give `TaskServerOptions.Store` when more than one server instance serves the Tasks extension.
`UpdateAsync` is the compare-and-swap the interface defines: `UPDATE … SET Version = Version + 1 WHERE TaskId = @id AND Version = @version`,
and the affected-row count says whether the change applied. A task created through one instance can be polled, answered and cancelled
through another; the instance running the work sees the answer and the cancel request when it next reads the row.

The class is marked `[Experimental("OMCX0001")]` because the interface it implements is: using it needs the same opt-in as the Tasks
extension (`#pragma warning disable OMCX0001`, or `NoWarn`). The adapter itself builds without suppressing anything. The capacity limit
(default 10,000) counts rows before the insert, so with several instances it can be overshot by about the number of concurrent creators.
Task ids are at most 128 characters. Results and errors are stored as the JSON text the service wrote; protect the table like any other
table of user data.

## At rest

- Codes, refresh tokens and client secrets reach the store as SHA-256 hashes; the tables hold the hashes.
- Revocation and assertion keys can be long and are chosen by clients, so their tables hold a SHA-256 hash of the key, not the key.
- `EfTokenStore` keeps access tokens, refresh tokens and client secrets inside one value per record. With an `ITokenProtector` that value
  is sealed before it is written; without one it is plain JSON, so give the store a protector, or encrypt the database, wherever someone
  other than the application can read the tables. A value the protector cannot open reads as absent and is never put in an error.
- Client ids longer than 450 characters are refused on save (a portable key limit).

## Limits

- The stores need `ExecuteUpdate`/`ExecuteDelete`, which every relational provider supports. The in-memory provider does not.
- Instants are stored as UTC ticks in `bigint` columns, so comparisons run in SQL on every provider.
- Entity Framework Core builds its model by reflection and is not trim- or AOT-safe, so this package is not marked trimmable or AOT
  compatible. Building it with the trim analyzer on reports IL2067 and IL2091 on the generic `DbSet<T>` helpers. Servers that publish
  with NativeAOT use the memory or file stores, or their own `IAuthorizationStore`.
- Purging expired rows is the server's job: it calls `PurgeAsync` periodically.
