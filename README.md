# Personal Server Manager

A Windows-hosted, owner-operated dashboard for Stash, Hydrus, Notes, and local
or external Calibre libraries. Search media, metadata, notes, ebooks, and comics
together, observe each endpoint independently, and start or stop managed servers.
GPU policy and MCP management are not implemented. See
[REQUIREMENTS.md](REQUIREMENTS.md) for the broader target.

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

The main page is a focused library search. Use the gear icon to open **Server
Management** (`/backends`), where Stash, Hydrus, Calibre, and Notes have equal-priority
sections for connections, health, and applicable lifecycle or sync controls. Stash,
Hydrus, and Calibre support **External** and **Managed** modes; Notes are local
folders with optional Git sync, not managed servers. Use the search link to return
to the main page.

Add each external Stash instance using one base URL (for example,
`http://127.0.0.1:9999/`) and optional API key. The GraphQL endpoint is derived
by appending `/graphql`; there are no separate GraphQL and web URL inputs.
Managed Stash requires an absolute installation folder containing `stash-win.exe`
and a port; its URLs use loopback. On launch, the manager updates the root `port`
value in that folder's `config.yml`, preserving other YAML values but not comments
or formatting. Invalid YAML is left untouched and prevents launch.
One query searches all categories
together. Results are combined across backends into Image, Video, Performer,
Studio, Tag, Marker, Collection, Note, Text/Ebook, and Comic grids. Stash searches
scenes, images, performers, studios, tags, scene markers, galleries, and groups;
galleries and groups share the Collection type. Empty types and sources are hidden.
Results are interleaved round-robin across backend instances, preserving each
source's ranking so one full source cannot crowd out the others. Each type
initially shows up to five results. Expand it to fetch up to 25 per
source and display up to 25 combined results, with an option to show everything
in each contributing source. Stash results open in Stash. Supported Hydrus files
open in the browser through the signed-in app; other formats open their preview.
Thumbnails preserve their aspect ratio, with a larger preview on hover or keyboard
focus. Backend failures are reported separately without hiding healthy results.
Scene markers use their own screenshot through the authenticated preview proxy.
Search and expansion show explicit loading indicators while data is being fetched.
Status refreshes every 15 seconds on the Server Management page.
Search and observation do not mutate Stash library contents.

## Managed Lifecycle

Managed Stash, Hydrus, and each Calibre library have start/stop controls and an
opt-in **Start with Personal Server Manager** setting, applied at app launch.
The manager only stops processes it launched; it does not adopt existing processes
or take over occupied ports. On normal app shutdown, Stash and Hydrus receive a
graceful GUI-close request, with up to 10 seconds to exit before a process-tree
kill and a further bounded 5-second wait. Calibre's headless server is killed
directly with a bounded 5-second exit wait. API health is separate from process
state. External servers remain under their owner's lifecycle control.

## Hydrus

Enable the Client API in Hydrus under `services -> edit`, and create an access
key under `services -> review`. Add an instance using its Client API base URL
(normally `http://127.0.0.1:45869/`) and key. The key needs **Search for and fetch
files** permission for previews, and **Manage pages** only for the explicit
desktop-open action. Keys are encrypted with the same Windows DPAPI-backed
mechanism as Stash keys, in a separate configuration file and protection scope.

Managed Hydrus additionally requires an absolute installation folder containing
`hydrus_client.exe`. Its Client API URL and access key are still required: configure
them in Hydrus yourself. Launching the executable does not enable or configure
the Client API.

Hydrus searches use comma-separated tags/predicates, preserving spaces within
each tag. For example, `blue sky, landscape` searches for both tags. Stash uses
the whole query as free text. Preview search is read-only and fetches at most
five files initially, or 25 on expansion, and their basic metadata. Hydrus files are labeled by file ID and
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

Hydrus tag autocomplete is searched independently using the read-only
`GET /add_tags/search_tags` endpoint. Tag results start a search for the selected
tag; no tag is created or edited. Endpoint permissions and failures are isolated
from file search. The configured access key must permit tag search as well as
the file operations you use.

## Notes And Calibre

On **Server Management**, add folders under **Notes** or **Calibre**. Notes and
managed Calibre require an absolute Windows drive path visible to the app's
account. Multiple libraries are supported; paths are configuration, not hardcoded
defaults. Example sources:

- Notes: `C:\ProgrammingProjects\StoryWriting`
- Calibre: `M:\CALIBRE\Calibre Text`
- Calibre: `M:\CALIBRE\SFW Comics`

Notes search `.md`, `.markdown`, and `.txt` filenames and UTF-8 text recursively,
using a literal case-insensitive query. Files larger than 1 MiB, non-UTF-8 files,
binary content, and symbolic links/junctions are skipped. A search scans at most
10,000 directory entries and 32 MiB of note content; matches beyond these bounds
are not returned. Notes open in a browser reader with Markdown formatting. Raw
HTML and remote resources are disabled, and the original note remains available.

Notes offer a manual Git pull and opt-in **Auto Git pull** at app startup and
every 15 minutes. Git must be installed and the configured folder must itself be
the repository root, not a subfolder. Pulls require a clean working tree including
staged, unstaged, and untracked files, and use fast-forward-only updates without
rebase or autostash. Each attempt is limited to two minutes. Configure upstream
and credentials outside the app using a safe credential helper or SSH setup;
sync is noninteractive, disables hooks and prompts, and requires known SSH hosts.
Dirty trees, divergent history, unavailable credentials, and timeouts are reported
without discarding local changes.

Managed Calibre libraries must contain `metadata.db`. Search covers titles,
authors, tags, and comments, not full book contents. Supported formats determine
the result category, not separate ebook/comic backend kinds: EPUB/PDF and other
text formats appear in Text/Ebook, while CBZ/CBR and other comic archives appear
in Comic. A book with supported formats in both categories appears in both.
No conversion is performed. Covers are shown
when available. With a Content Server connection, results open Calibre's browser
reader (or the browser's PDF viewer) and offer a separate file download. SQLite is
opened read-only. Book descriptions from Calibre comments are shown as bounded,
plain-text excerpts with an expandable full description when available. SQLite uses
immutable reads; uncheckpointed WAL changes are not visible.
Keep the library stable while searching; use a read-only snapshot if another
process is actively editing it. The adapter never changes metadata or book files.

Expanded local groups link to an authenticated, paginated source view. File and
cover routes also require sign-in, reject path traversal/reparse points, and
expose opaque item IDs rather than physical paths. Local configuration is stored
alongside the other backend settings in `local-libraries.json`.

To enable reading on the manager PC, select **Managed** in each Calibre library,
set its own loopback port (default `8080`; use distinct ports for concurrent
libraries), and optionally select an installation folder. The default is
`%ProgramFiles%\Calibre2`; the folder must contain `calibre-server.exe`. Each
library has its own server process and discovered reader library ID. Without a
running managed reader, local search and downloads remain available.

**External** Calibre uses a Content Server URL and library ID without a local
folder. The HTTP adapter reads metadata, covers, and files, with at most 10,000
search candidates, 8 MiB per metadata response, and 5 MiB per cover. It does not
store or send Calibre login credentials: servers requiring login cannot be
API-searched unless an owner-managed reverse proxy grants read-only access to
Personal Server Manager. Browser reader links may still prompt for login.
Managed Calibre is loopback-only with no authentication; do not expose its port.
Its reader URL uses loopback, so a remote browser cannot use it to reach the
manager PC. File downloads are proxied through Personal Server Manager and
require sign-in. See [Calibre hosting](docs/calibre-hosting.md) for external
hosting and TLS guidance.

## Network Access

For local development, use the `http` launch profile, which binds only to
loopback. To serve HTTP directly on a trusted private LAN, start the opt-in
`lan-http` profile:

```powershell
dotnet run --project src/PersonalServerManager --launch-profile lan-http
```

On the manager PC, open `http://localhost:5289` once to complete owner-password
enrollment; enrollment is intentionally limited to loopback. Find the PC's
private IPv4 address with `ipconfig`, then open `http://<PC-IPv4>:5289` from a
LAN device. If Windows Firewall blocks access, add an inbound rule in an
Administrator PowerShell window, restricted to the private profile and local
subnet:

```powershell
New-NetFirewallRule -DisplayName "Personal Server Manager (LAN)" -Direction Inbound -Action Allow -Protocol TCP -LocalPort 5289 -Profile Private -RemoteAddress LocalSubnet
```

HTTP sends passwords, session cookies, and app traffic without transport
encryption. Use this only on a trusted private network, keep the firewall
restricted to the local subnet, and do not port-forward the app to the internet.
The normal `http` profile remains loopback-only. For access beyond a trusted
LAN, use TLS at an owner-managed reverse proxy and configure its exact IP in
`ForwardedHeaders:KnownProxies`; forwarded headers from other addresses are not
trusted. For example:

```powershell
$env:ForwardedHeaders__KnownProxies__0 = "10.0.0.10"
```

Bind the app only to an address reachable by that proxy, and firewall the app
port so clients cannot bypass the proxy.

## Local Data And Recovery

By default, configuration and Data Protection keys are stored under
`%LOCALAPPDATA%\PersonalServerManager`. Stash/Hydrus API keys and the owner password
hash are protected with Windows DPAPI for the current Windows account. The
instance JSON contains connection metadata and protected key material, not
plaintext API keys.

Existing JSON instance/library IDs and protected API keys are preserved. Legacy
Calibre ebook/comic entries and their configured reader links remain supported.
The old global Calibre autostart setting is not automatically migrated and
currently remains disabled to avoid unexpected launches; enable startup explicitly
on each managed library.

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