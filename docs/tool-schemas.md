# Tool signatures and JSON Schema

The `[Tool]` source generator and the reflection quick path (`server.Tool("name", delegate)`) write the same schema for the same
signature. This note is that mapping. It follows the table that OneLLM publishes for its tools (`docs/tool-schemas.md` in OneLLM), so a
method declared for both libraries lists the same schema in each; the differences are listed at the end.

Dialect: JSON Schema 2020-12. Only the keywords below are written, always in this order: `type`, `format`, `description`, `enum`,
`minimum`, `maximum`, `minLength`, `maxLength`, `pattern`, `contentEncoding`, `items`, `minItems`, `maxItems`, `uniqueItems`,
`properties`, `required`, `additionalProperties`, `default`.

## A tool

- **Name:** `[Tool(Name = ...)]` (or `[Tool("name")]`), else the method name in snake_case without a trailing `Async`
  (`GetDocketAsync` → `get_docket`, `HTTPRequest` → `http_request`). 1 to 128 ASCII letters, digits, `_`, `-` or `.` (the tools
  specification's rule).
- **Description:** `[Tool(Description = ...)]`, else `[Description]` on the method, else the `<summary>` doc comment (generator only), else none.
- **Parameters:** `RequestContext` and `CancellationToken` are filled in and never listed (at most one of each). Every other
  parameter is a property named exactly as the C# parameter. It is required unless it has a default value; a nullable type without a
  default is still required and accepts `null`. A string, number, boolean, char or enum default is written as `default`; a `null` default
  writes nothing. The object has `"additionalProperties": false`, and `required` is left out when empty.
  Parameter description: `[Description]`, else the method's `<param>`.
- **Annotations:** `ReadOnly`, `Destructive`, `Idempotent`, `OpenWorld` and `Title` on the attribute. A hint is listed only when the
  attribute assigns it.

## Types

| C# type | Fragment |
|---|---|
| `bool` | `{"type":"boolean"}` |
| `sbyte` `byte` `short` `ushort` `int` `uint` `long` `ulong` | `{"type":"integer","minimum":MIN,"maximum":MAX}` for the type's range |
| `float` `double` `decimal` | `{"type":"number"}` |
| `string` | `{"type":"string"}` |
| `char` | `{"type":"string","minLength":1,"maxLength":1}` |
| `Guid` | `{"type":"string","format":"uuid"}` |
| `DateTime`, `DateTimeOffset` | `{"type":"string","format":"date-time"}` |
| `DateOnly` / `TimeOnly` | `format: date` / `format: time` |
| `TimeSpan` | a string with the pattern of the invariant `c` form |
| `Uri` | `{"type":"string","format":"uri"}` |
| `byte[]` | `{"type":"string","contentEncoding":"base64"}` |
| enum | `{"type":"string","enum":[member names in declaration order]}`, matched exactly |
| `T?` | the fragment of `T` with `null` added to `type` (and to an enum's `enum` list) |
| arrays, `List<T>`, `IList<T>`, `ICollection<T>`, `IEnumerable<T>`, `IReadOnlyList<T>`, `IReadOnlyCollection<T>` | `{"type":"array","items":...}` |
| `HashSet<T>`, `ISet<T>`, `IReadOnlySet<T>` | the same plus `"uniqueItems":true` |
| `Dictionary<string,T>`, `IDictionary<string,T>`, `IReadOnlyDictionary<string,T>` | `{"type":"object","additionalProperties":...}` |
| `JsonElement`, `JsonNode` / `JsonObject` / `JsonArray` | `{}` / `{"type":"object"}` / `{"type":"array"}` |
| record, class, struct | a closed object (below) |

Not supported: `object`, other interfaces, delegates, multi-dimensional arrays, dictionaries keyed by anything but `string`, `ref`/`out`/`in`
parameters, open generics, other collection types.

**Records and classes:** public instance properties with a public getter, base class first, then declaration order; `[JsonIgnore]` removes
one and `[JsonPropertyName]` renames it. Names are camelCase when the type's `JsonSerializerContext` says `CamelCase` (or uses
`JsonSerializerDefaults.Web`) and as written when it names no policy; any other policy is refused. A property is required when it is
`required`, `[JsonRequired]`, or a parameter without a default of the type's only public constructor. Property descriptions come from
`[Description]`, then the property's `<summary>`, then (for a positional record) the record's `<param>`.

**Validation attributes:** `[Range]`, `[StringLength]`, `[MinLength]`, `[MaxLength]`, `[Length]`, `[RegularExpression]`, `[EmailAddress]`
and `[Url]` map to `minimum`/`maximum`, `minLength`/`maxLength` (`minItems`/`maxItems` on arrays), `pattern`, `format: email` and
`format: uri`.

**Doc comments** are read from the source tree, so they work without a documentation file: whitespace collapses, `<see cref="X"/>`
becomes `X`, `<paramref name="p"/>` becomes `p`, `<c>` and `<para>` keep their text. `[Description]` always wins.

## Reading arguments and writing results

The generated invoker reads arguments with System.Text.Json source-generated metadata: the built-in set for the scalar types above, nullable
scalars, `string[]`, `List<string>` and a few other common collections, and the `JsonSerializerContext` that lists the type for everything
else (records, other collections, dictionaries). Enums are matched by name without a context. A type no context lists is a build error
(`OMCG0005`), so nothing falls back to reflection.

| Return type | Result |
|---|---|
| `void`, `Task`, `ValueTask` | no content |
| `string` | one text block (`null` is the text `null`) |
| `ContentBlock` and subclasses, sequences of them | those blocks |
| `CallToolResult` | passed through |
| `JsonNode`, `JsonElement` | one text block of the JSON |
| numbers, `bool`, `Guid`, dates, `Uri`, collections | one text block of the JSON value |
| enum | its member name |
| record, class, dictionary | structured content, the same JSON as a text block, and an `outputSchema` |

`Task<T>` and `ValueTask<T>` are unwrapped. A nullable object return (`Note?`) keeps `"type":"object"` in its `outputSchema` (every revision before 2026-07-28 requires an object); a null result is sent as the text `null` with no `structuredContent`.

**Errors.** A missing, unknown, null or wrongly typed argument is a tool execution error (an `isError` result whose text names the argument,
for example `Argument 'items[2].qty' is not valid.`), because the tools specification asks for input validation errors to be fixable by
the model. A `ToolException` becomes an error result with its message. Any other exception becomes the result `The tool failed.`: its text
can hold internals and is never sent; it is published to the diagnostics stream instead.

## Where this differs from the OneLLM table

- Tool names may use `.` and be up to 128 characters, as the tools specification allows (the table says 1 to 64 without `.`).
- `outputSchema` and structured content are written only for object-shaped results (records, classes, dictionaries). The table writes one
  for any non-text result, but four of the five protocol revisions require `outputSchema.type` to be `"object"`, and clients validate
  listings against that.
- An exception from the tool is reported as `The tool failed.`, not with its type and message.
- `HashSet<T>` is listed with `uniqueItems`, but duplicates are not refused when reading (the serializer drops them).
- Generator diagnostics are `OMCG0001` to `OMCG0012` (the table's `OLT001` to `OLT011` in order, with the same meanings, plus the
  resource and prompt rules).
- `RequestContext` plays the part of the table's `ToolContext`; `EndsLoop`, `RequiresApproval`, `ParallelSafe` and `TimeoutSeconds` do
  not exist here (a request timeout is a server option).

## MCP Apps properties

`[Tool(UiResource = "ui://weather/view", UiVisibility = new[] { "model", "app" })]` names the `ui://` resource that renders the tool's
result and who may call it (default both). The generated registration opts the server in to the Apps extension and sets the tool's
`_meta.ui`; callers that did not negotiate Apps see an ordinary tool. Register the resource itself with `AddUiResource`. See
`extensions.md`.

## Resources and prompts

`[Resource("scheme://{template}")]` methods take template variables as parameters of the same name; `[Prompt]` methods take the prompt
arguments. Both read text: parameters are `string` or a type that parses from a string (numbers, `bool`, `Guid`, dates and times), required
unless they have a default value or are nullable. A bad value is an invalid-params error (-32602). Resources return `string` (text,
`text/plain` unless `MimeType` says otherwise), `byte[]` (blob), `ReadResourceResult` or resource contents; prompts return `string` (one
user message), `PromptMessage`s or `GetPromptResult`.

## Generator diagnostics

| Id | Meaning |
|---|---|
| OMCG0001 | The containing type, and every type around it, must be a non-static `partial` class |
| OMCG0002 | A parameter type has no schema (or a resource/prompt parameter is not text-like) |
| OMCG0003 | The return type is not supported, or the method is `async void` |
| OMCG0004 | The method is generic |
| OMCG0005 | A record, collection or dictionary needs a `JsonSerializerContext` that lists it |
| OMCG0006 | A type refers to itself |
| OMCG0007 | Two members of one class share a name |
| OMCG0008 | The name is not valid |
| OMCG0009 | More than one `RequestContext` or `CancellationToken` parameter |
| OMCG0010 | The containing class is generic |
| OMCG0011 | The serializer context uses a property naming policy other than `CamelCase` or none |
| OMCG0012 | `UiResource` is not a `ui://` URI, or `UiVisibility` holds something other than `model` or `app` |
