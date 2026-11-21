# Calibre Hosting

## Managed Setup

Install Calibre in its standard Program Files location. In **Backends**, add
the Calibre ebook and comic folders under **Local libraries**. In the **Calibre
Content Server** section, save a loopback port (default `8080`) and select
**Start Calibre**. Enable **Start with Personal Server Manager** and save the
settings to have the app launch Calibre on subsequent starts.

The app starts one read-only server for the configured Calibre folders, obtains
their library IDs, and uses those IDs for reader links. Stop and start Calibre to
apply changes to the folder list. The manager stops its own Calibre process when
it shuts down normally and does not take over a port used by another process.
An explicit Content Server URL on a library overrides the managed reader link.

Managed Calibre binds to `127.0.0.1` with no Calibre login. Other processes on
the same PC can reach it. It is not for remote browser access; do not forward
or reverse-proxy the managed port. Use the external setup below for that.
Personal Server Manager does not synchronize reading progress, convert ebooks,
or proxy Calibre credentials.

## External Setup

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

In Personal Server Manager, edit each Calibre library under **Backends**. Set
**Content Server URL** to the address reachable from the reading device and
**Content Server library ID** to the matching ID from `/ajax/library-info` on
that server. The example folders use `Calibre_Text` and `SFW_Comics`. Both
libraries can share one server URL. When using a separate device, do not enter
`localhost` or `127.0.0.1`; those refer to the device running the browser.

## LAN Access

Put an owner-managed HTTPS reverse proxy in front of the loopback server and keep
authentication enabled. A dedicated hostname is simpler than a path prefix.
When TLS terminates at the proxy, Calibre documents `--auth-mode=basic` for the
loopback upstream. Do not expose a plaintext Basic-auth endpoint directly to the LAN
or internet, and do not forward port 8080 publicly.

Run the server under an account that can access both libraries. Windows mapped
drives are logon-session-specific: `M:` might not exist for a scheduled task or
service even when it works interactively. Prefer local storage on the hosting
machine or deliberately configure storage access for the service account. The
current app's local-library adapter requires a fully qualified Windows drive path
and deliberately rejects symbolic-link/junction paths.

For always-on hosting, configure the verified command as a Windows scheduled task
or a service wrapper only after testing account permissions and storage visibility.
This repository does not install that service or change firewall/proxy settings.

## Read-Only Adapter Limits

The app's immutable SQLite reader ignores uncheckpointed WAL changes. For an
actively edited library, search a stable read-only snapshot, or use Calibre's own
Content server for current browsing. Do not copy only `metadata.db` while a writer
is active and assume it is a consistent backup. Back up the entire library using
a consistent snapshot or while writers are stopped.

## Official References

- [Calibre Content server](https://manual.calibre-ebook.com/server.html)
- [Command-line options](https://manual.calibre-ebook.com/generated/en/calibre-server.html)