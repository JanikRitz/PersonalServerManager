# Personal Server Manager

A Windows-hosted, owner-operated dashboard. The first implementation is a
read-only Stash integration: configure multiple Stash instances, search scenes,
images, and performers across them, and observe each endpoint independently.
Process control, GPU policy, other integrations, and MCP management are not
implemented. See [REQUIREMENTS.md](REQUIREMENTS.md) for the broader target.

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
`/graphql`), web UI URL, and optional API key. Searches request at most 40
matches per instance and run concurrently. An unavailable instance reports its
own search and observation error without suppressing results from other
instances. Status refreshes every 15 seconds. The integration does not start,
stop, or otherwise mutate Stash.

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
`%LOCALAPPDATA%\PersonalServerManager`. Stash API keys and the owner password
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

The tested Stash version and real instance configuration have not yet been
provided, so validate the GraphQL fields and links against the installed Stash
version before relying on the integration. No credentials belong in source,
examples, or test data.