# Calibre Hosting

## Managed Setup

Open **Server Management** (`/backends`), where Stash, Hydrus, Calibre, and Notes
have equal-priority sections. Stash, Hydrus, and Calibre offer external and managed
modes; Notes use local folders with optional Git sync rather than a server process.
Under **Calibre**, add a library in **Managed** mode with an absolute folder path
containing `metadata.db`, its own loopback port (default `8080`, range
`1024`-`65535`), and an optional Calibre installation folder. If omitted, the
installation folder defaults to `%ProgramFiles%\Calibre2`; it must contain
`calibre-server.exe`.

For example, add `M:\CALIBRE\Calibre Text` on port `8080` and
`M:\CALIBRE\SFW Comics` on port `8081`. Each library gets its own read-only
Content Server process, not one global process for all folders. The manager
discovers each server's library ID for reader links. Select **Start Calibre**
on the library row; opt in to **Start with Personal Server Manager** per library
for subsequent app launches. Stop the library's server before changing its settings.

Each process has a stable configuration directory under the manager's data directory
(`managed-calibre/<instance-id>`). Calibre's Windows per-user database mutex would
otherwise prevent the second server from starting. The launcher sets
`CALIBRE_NO_SI_DANGER_DANGER=1` only in that child process, together with the
read-only server flags. Duplicate managed library roots are rejected. This bypass
must not be reused to run concurrent writers against a library; keep libraries
stable while serving them.

The manager stops only its own launched processes on normal shutdown, never
adopts existing processes, and refuses an occupied Calibre port. Unlike the
Stash/Hydrus graceful GUI-close attempt followed by a bounded kill, headless
Calibre is killed directly as a process tree with a bounded 5-second exit wait.
Existing JSON IDs, legacy ebook/comic kinds, and explicitly configured reader
links remain supported. A legacy library with a folder and explicit Content
Server URL keeps that reader link instead of getting a managed reader connection.
The old global Calibre autostart setting is not automatically migrated and
currently remains disabled to avoid unexpected launches. Enable startup explicitly
for each managed library.

Ebooks and comics are formats within Calibre, not distinct backend choices.
Supported text formats such as EPUB/PDF produce Text/Ebook results; supported
comic archives such as CBZ/CBR produce Comic results. A mixed-format book appears
in both categories, without conversion.

Managed Calibre binds to `127.0.0.1` with no Calibre login. Other processes on
the same PC can reach it. Reader URLs use loopback, so in a remote browser they
refer to that browser's device, not the manager PC. Do not forward
or reverse-proxy the managed port. Use the external setup below for that.
Personal Server Manager does not synchronize reading progress, convert ebooks,
or proxy Calibre credentials. File downloads, unlike reader links, are proxied
through Personal Server Manager and require its sign-in.

## External Setup

In **Server Management**, add a Calibre library in **External** mode using a
**Content Server URL** and **Content Server library ID**, without a local folder.
The manager reads metadata, covers, and files over HTTP. The URL must be reachable
from the manager for API access and from the reading device for browser links.

The current HTTP adapter has no stored Calibre authentication credentials and
does not log in to Calibre. An authenticated server requiring login cannot be
API-searched unless an owner-managed reverse proxy grants read-only access to
Personal Server Manager. Cover and file requests need that access too. A browser
reader link can still prompt for Calibre login; a browser login does not
authenticate the manager's HTTP requests. The account setup below secures the
external server, but does not configure authentication support in the adapter.

Calibre is installed under `C:\Program Files\Calibre2` on the test machine.
Create an account database outside the book folders, then use Calibre's interactive
user manager to add read-only users. Enter passwords directly into that tool.

```powershell
New-Item -ItemType Directory -Force "$env:LOCALAPPDATA\CalibreServer"
& "C:\Program Files\Calibre2\calibre-server.exe" --userdb "$env:LOCALAPPDATA\CalibreServer\users.sqlite" --manage-users
```

After configuring accounts, a loopback-only server can serve both libraries:

```powershell
& "C:\Program Files\Calibre2\calibre-server.exe" --listen-on 127.0.0.1 --port 8080 --disable-fallback-to-detected-interface --enable-auth --disable-local-write --userdb "$env:LOCALAPPDATA\CalibreServer\users.sqlite" "M:\CALIBRE\Calibre Text" "M:\CALIBRE\SFW Comics"
```

The browser UI is at `http://127.0.0.1:8080/`; OPDS clients use `/opds`.
The `--disable-local-write` switch blocks unauthenticated local edits; authenticated
users still need read-only permissions in the account database.

In Personal Server Manager, edit each external Calibre library under **Server
Management**. Set
**Content Server URL** to the address reachable from the reading device and
**Content Server library ID** to the matching ID from `/ajax/library-info` on
that server. The example folders use `Calibre_Text` and `SFW_Comics`. Both
libraries can share one server URL. When using a separate device, do not enter
`localhost` or `127.0.0.1`; those refer to the device running the browser.

## LAN Access

Put an owner-managed HTTPS reverse proxy in front of the external loopback server
and keep authentication enabled for browser users. If manager API search is needed,
grant only narrowly scoped read-only access to the manager at the proxy, covering
search/metadata, covers, and file downloads; do not create a public authentication
bypass. This is owner-managed proxy configuration, not stored Calibre credentials
in Personal Server Manager. A dedicated hostname is simpler than a path prefix.
When TLS terminates at the proxy, Calibre documents `--auth-mode=basic` for the
loopback upstream. Do not expose a plaintext Basic-auth endpoint directly to the LAN
or internet, and do not forward port 8080 publicly.

Run the server under an account that can access both libraries. Windows mapped
drives are logon-session-specific: `M:` might not exist for a scheduled task or
service even when it works interactively. Prefer local storage on the hosting
machine or deliberately configure storage access for the service account. The
current app's local-folder adapter requires a fully qualified Windows drive path
and deliberately rejects symbolic-link/junction paths. Folderless external
libraries do not require those paths to be visible to the manager.

For always-on hosting, configure the verified command as a Windows scheduled task
or a service wrapper only after testing account permissions and storage visibility.
This repository does not install that service or change firewall/proxy settings.

## Read-Only Adapter Limits

Local Calibre search uses immutable, read-only SQLite and never edits metadata or
book files. The immutable reader ignores uncheckpointed WAL changes. For an
actively edited library, search a stable read-only snapshot, or use Calibre's own
Content server for current browsing. Do not copy only `metadata.db` while a writer
is active and assume it is a consistent backup. Back up the entire library using
a consistent snapshot or while writers are stopped.

External HTTP searches inspect at most 10,000 candidate books. Metadata responses
are bounded to 8 MiB each and covers to 5 MiB each. Search covers metadata, not
full book contents; unsupported formats are not converted. These bounds can omit
matches or reject oversized responses, and backend errors are reported separately
from healthy sources.

## Official References

- [Calibre Content server](https://manual.calibre-ebook.com/server.html)
- [Command-line options](https://manual.calibre-ebook.com/generated/en/calibre-server.html)