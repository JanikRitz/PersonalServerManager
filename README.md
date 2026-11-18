# Personal Server Manager

A Windows-hosted, owner-operated dashboard for multiple Stash and Hydrus
instances. Search Stash scenes, images, and performers alongside Hydrus files,
and observe each endpoint independently. Process control, GPU policy, and MCP
management are not implemented. See [REQUIREMENTS.md](REQUIREMENTS.md) for the
broader target.

## Run Locally

Install the .NET 10 SDK, then run:

```powershell
dotnet run --project src/PersonalServerManager --launch-profile http
```

The launch profile binds to `localhost:5289`. Open the local page and create the
owner password during one-time enrollment. Use a unique password of at least 14
characters. Enrollment is accepted only from a loopback connection; no password
is shipped or stored in the repository. Sign in uses an HTTP-only, same-site
cookie, antiforgery protection, and a per-IP request limit.

Add each Stash instance using its GraphQL endpoint (normally ending in
`/graphql`), web UI URL, and optional API key. One query searches all categories
together, showing at most five results per category and instance. Each Stash
group links to the full query in that instance's UI. Failures are isolated per
group and do not suppress other results. Status refreshes every 15 seconds.
The integration does not start, stop, or otherwise mutate Stash.

## Hydrus

Enable the Client API in Hydrus under `services -> edit`, and create an access
key under `services -> review`. Add an instance using its Client API base URL
(normally `http://127.0.0.1:45869/`) and key. The key needs **Search for and fetch
files** permission for previews, and **Manage pages** only for the explicit
desktop-open action. Keys are encrypted with the same Windows DPAPI-backed
mechanism as Stash keys, in a separate configuration file and protection scope.

Hydrus searches use comma-separated tags/predicates, preserving spaces within
each tag. For example, `blue sky, landscape` searches for both tags. Stash uses
the whole query as free text. Preview search is read-only and fetches at most
five files and their basic metadata. Hydrus files are labeled by file ID and
MIME type; the Client API is not a browser-based library UI.

**Open query in Hydrus** explicitly creates and focuses a search page in the
Hydrus desktop client, without the preview limit. This changes the desktop
session, not the library contents, and never happens automatically during
search or observation. The app serializes Hydrus search/metadata pairs because
tag-restricted keys may retrieve only their most recently searched IDs;
external clients reusing the same key can still interfere, so issue this app
its own key. Connection errors and missing permissions are surfaced rather
than silently falling back to another action.

See the [official Client API documentation](https://hydrusnetwork.github.io/hydrus/developer_api.html)
and [adapter contract notes](src/PersonalServerManager/Hydrus/README.md).

## Network Access

Keep the app on loopback for local use. Non-loopback plaintext HTTP is rejected.
For LAN access, terminate TLS at an owner-managed reverse proxy and configure
the exact proxy IP in `ForwardedHeaders:KnownProxies`; requests with forwarded
headers from other addresses are not trusted. For example:

```powershell
$env:ForwardedHeaders__KnownProxies__0 = "10.0.0.10"
```

Bind the app only to an address reachable by that proxy, and firewall the app
port so clients cannot bypass the proxy. The repository does not configure
nginx, certificates, firewall rules, or public access. Verify the proxy source
address seen by the Windows host before enabling this mode.

## Local Data And Recovery

By default, configuration and Data Protection keys are stored under
`%LOCALAPPDATA%\PersonalServerManager`. Stash/Hydrus API keys and the owner password
hash are protected with Windows DPAPI for the current Windows account. The
instance JSON contains connection metadata and protected key material, not
plaintext API keys.

To back up or restore the app, stop it and copy the complete
`PersonalServerManager` directory, including its `keys` subdirectory. Restore
it under the same Windows account on the same host before starting the app.
DPAPI-protected data is not portable to another Windows account or machine. If
the key ring is lost, protected API keys cannot be recovered; re-enter them in
the dashboard. If persisted JSON is invalid, the app reports the error and
leaves the file untouched rather than replacing it with defaults.

`PSM_DATA_DIRECTORY` can select an alternate data directory for isolated tests
or an explicitly managed deployment. Protect that directory and its backups as
owner data.

## Development

```powershell
dotnet build src/PersonalServerManager/PersonalServerManager.csproj
dotnet test tests/PersonalServerManager.Tests/PersonalServerManager.Tests.csproj
```

Verified against Stash `v0.31.1`: observation, all three search categories, and
full-query links. Playwright checks cover combined results, five-result limits,
failure isolation, and desktop/mobile layout. Hydrus API version `96` was
reachable, but authenticated real-server search remains unverified until a key
is configured. Its header, search/metadata, encryption, cancellation, and
explicit desktop-page workflows are covered by deterministic tests and a
loopback browser fixture. No real credentials belong in source, examples, or
test data.