# Hydrus Backend

Contract checked against the [official Client API documentation](https://hydrusnetwork.github.io/hydrus/developer_api.html).

- Observation uses `GET /verify_access_key`. It requires a valid key but no specific permission; `permits_everything` and `basic_permissions` describe access.
- Passive search uses `GET /get_files/search_files` with a percent-encoded JSON `tags` array. The documented `system:limit=16` example confirms limit predicates; this client appends `system:limit=5`. There is no separate `limit` parameter. Results are not a total count or pagination.
- At most five file IDs are sent to `GET /get_files/file_metadata`, with `only_return_basic_information=true` and `include_services_object=false`. Search/fetch permission (3) is required. Tag-restricted keys can fetch only IDs from their most recent search; the singleton client serializes its search/metadata pairs. Other applications must use different access keys to avoid out-of-process interference.
- Basic metadata has `file_id` and `mime`, but no title. The client generates `File {id}` labels and preserves search order.
- The documentation does **not** list `/manage_pages/add_search_page`. The documented endpoint is `POST /manage_pages/new_page`, with `page_type=6`, `tags`, and `focus_page=true`. Only an explicit `OpenSearchAsync` call creates this page, using the original predicates without the preview limit. Manage Pages permission (4) is required. There is no fallback or automatic retry of this side effect.

## Public API

All types are in `PersonalServerManager.Hydrus`.

```csharp
public sealed record HydrusInstance(Guid Id, string Name, Uri ApiEndpoint, string? ApiKey);
public sealed record HydrusFile(long Id, string Title, string Mime);
public sealed record HydrusObservation(Guid InstanceId, string InstanceName, bool IsReachable, DateTimeOffset CheckedAt, string Message);

public HydrusClient(HttpMessageHandler? handler = null, TimeSpan? requestTimeout = null);
public Task<HydrusObservation> ObserveAsync(HydrusInstance instance, CancellationToken cancellationToken = default);
public Task<IReadOnlyList<HydrusFile>> SearchAsync(HydrusInstance instance, IReadOnlyList<string> tags, CancellationToken cancellationToken = default);
public Task<string> OpenSearchAsync(HydrusInstance instance, IReadOnlyList<string> tags, CancellationToken cancellationToken = default);
public void Dispose();

public JsonHydrusInstanceStore(string filePath, IDataProtector protector);
public Task<IReadOnlyList<HydrusInstance>> GetAllAsync(CancellationToken cancellationToken = default);
public Task SaveAsync(HydrusInstance instance, CancellationToken cancellationToken = default);
public Task RemoveAsync(Guid id, CancellationToken cancellationToken = default);
```

`IHydrusClient` and `IHydrusInstanceStore` expose the corresponding asynchronous methods. `OpenSearchAsync` returns the created page key. Predicates are separate strings: a multiword tag remains one string, not whitespace-split text. At most 64 predicates of 1024 characters each are accepted.

## Feed Search

`SearchFeedAsync` accepts blank queries and Hydrus advanced Boolean expressions. It supports parentheses, `not`/`!`/`-`, `and`/`&&`, `or`/`||`, `nand`, `nor`, `xor`, `implies`/`=>`, and `xnor`/`iff`/`<=>`, with Hydrus operator precedence and left-associative binary operators. Backslash escapes expression characters; Hydrus itself cannot express a literal wildcard or a leading hyphen in a tag, so those escaped forms are rejected. Wildcards otherwise retain Hydrus semantics.

`HydrusFeedQuery.Parse(query)` is public for local validation and returns the API's AND list of strings and nested OR lists. Supported system predicates are `system:everything` alone, `system:inbox`, `system:archive`, `system:untagged`, `system:has tags`, `system:has audio`, `system:no audio`, `system:has duration`, and `system:no duration`. Width, height, and duration accept `=`, `==`, `!=`, `<`, `>`, `<=`, `>=`, or `~=` comparisons. Width and height accept an integer optionally followed by `px`, `pixel`, or `pixels`; duration requires ordered integer units, for example `system:duration > 5 seconds` or `system:duration <= 1 hour 30 minutes`. Supported duration units are `h`, `hr`, `hour(s)`, `mins`, `minute(s)`, `s`, `sec(s)`, `second(s)`, `ms`, `msec(s)`, and `millisecond(s)`. Each numeric component is limited to 999,999,999 and must not have leading zeroes.

Multiple system predicates, including ranges and OR groups, are allowed. Negation is supported only for inbox/archive and has/no audio; contradictory top-level inbox/archive or has/no audio pairs are rejected because Hydrus may not preserve their conjunction. User-supplied `system:limit`, other system predicates, unsupported syntax, and excessively complex expressions are rejected rather than approximated. Queries are limited to 16,384 characters, 128 literals, 128 levels of nesting, 4,096 CNF clauses, and 65,536 conversion steps. Hydrus still validates accepted predicates on the server. This subset follows the [upstream text parser](https://github.com/hydrusnetwork/hydrus/blob/master/hydrus/external/SystemPredicateParser.py) and [search context](https://github.com/hydrusnetwork/hydrus/blob/master/hydrus/client/search/ClientSearchFileSearchContext.py); server versions may differ.

The API has no offset parameter: newest/oldest request up to `offset + limit` IDs sorted by Hydrus import time. Image/video filtering uses returned MIME prefixes, expanding those searches as necessary. Random order instead retrieves the complete matching ID set on every page and applies a deterministic permutation of the IDs before filtering and paging. The same unchanged result set therefore has the same order across independent requests and browsing sessions, regardless of Hydrus's returned order; it is not reshuffled on refresh. No results or credentials are cached. Changes to matching IDs or MIME types between requests can still move items between offsets, as can import-time ties for newest/oldest.

Feed searches support at most 100,000 matching IDs before media filtering. When a search needs to look beyond that boundary, it requests one extra ID: exactly 100,000 IDs can produce a partial or empty final page, while more than 100,000 produces an error instead of silently truncating. Random searches always probe the complete set; newest/oldest searches probe only when a requested page or media filtering requires looking beyond the boundary, so an oversized result set can yield earlier pages before the error. The cap applies to all query matches, including files excluded by the media filter. Metadata is fetched in batches of at most 100 IDs; each search and its metadata requests are serialized and share the existing deadline. Random paging repeatedly transfers and sorts the entire ID set, and sparse media matches can require extensive metadata requests, so either may be expensive or time out. Tag-restricted keys may reject searches without a positive top-level tag.

## Safety and Ownership

The client owns and disposes its transport. Default production transport has redirects and cookies disabled; supplied `HttpClientHandler` and `SocketsHttpHandler` transports (including those inside delegating handlers) are configured likewise before use. Inject only fresh, trusted custom handlers: custom code must not forward credentials or implement redirects itself.

Access keys appear only in the `Hydrus-Client-API-Access-Key` request header. Endpoints must be HTTP(S), without user information, query, or fragment. Endpoint path prefixes are retained. The client never logs keys, copies remote error bodies into exceptions, or attaches raw transport/JSON exceptions as inner exceptions.

Each public network operation has one deadline covering headers and complete response bodies, including both requests in a passive search. The default and maximum deadline is eight seconds; a shorter timeout may be supplied. Each complete response is limited to 256 KiB, even without Content-Length. JSON nesting is limited to 32, metadata to five rows, MIME strings to 128 characters, and page keys to 128 hexadecimal characters. Caller cancellation propagates; observation converts network/response failures into an unreachable observation.

The store follows the Stash store's serialized, protected-key, temporary-file-and-atomic-replacement pattern. Blank keys on edits preserve the previous key. Supply a Hydrus-specific Data Protection purpose and retain its key ring; use the host's normal DPAPI protection configuration. The store does not register services or choose an application data path.

The host registers this client and its protected store, and the dashboard exposes instance configuration, unified previews, and an explicit desktop-open command. Process control is not included. Live discovery reached Hydrus API version 96; authenticated real-server operations require an owner-configured access key and have not yet been verified.