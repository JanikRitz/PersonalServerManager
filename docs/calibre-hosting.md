# Calibre Hosting

## Recommendation

Use the official Calibre Content server when you want browser reading and OPDS,
with one process serving both libraries. Keep the Calibre desktop application as
the editor and restrict server accounts to read-only access. Avoid adding another
library manager solely to expose these two existing folders.

Personal Server Manager currently searches these libraries directly and reads
existing files. It does not start a server, synchronize reading progress, convert
ebooks, or connect to a remote Calibre API. Starting the optional Content server
does not automatically change the app's local-backend configuration.

## Local Setup

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