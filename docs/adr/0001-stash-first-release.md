# ADR 0001: Stash-First Local Web Application

Status: Accepted for the first implementation
Date: 2026-11-17

## Context

The confirmed first release is a single-owner, Windows-hosted web application
for multiple independently configured Stash instances. It searches scenes,
images, and performers through each instance's GraphQL API and observes each
instance without lifecycle control. The repository already uses .NET and
Blazor, but that scaffold is experience rather than a requirement.

## Decision

- Keep one ASP.NET Core / Blazor Server process and use a typed Stash GraphQL
  client with bounded requests and per-instance failure isolation.
- Persist instance metadata as atomic JSON under the current Windows user's
  local application data directory. Protect API keys and the owner credential
  with ASP.NET Core Data Protection keys protected by Windows DPAPI.
- Require a password established through a one-time loopback-only enrollment
  flow. Protect browser state with antiforgery tokens, secure cookie defaults,
  and rate limits on enrollment and login.
- Bind locally by default. Permit reverse-proxy HTTPS forwarding only when the
  proxy source IP is explicitly configured, and reject non-loopback plaintext
  requests.
- Keep the integration read-only. Do not add process control, other application
  integrations, GPU coordination, or MCP behavior in this increment.

## Consequences

- Configuration and API credentials are durable across app restarts, but
  DPAPI-protected backups are recoverable only under the same Windows account
  on the same host. Back up the entire data directory, including the key ring.
- LAN access requires an independently managed HTTPS reverse proxy, a trusted
  proxy address, and network restrictions preventing proxy bypass.
- No real Stash installation or version was specified. GraphQL query and UI
  link compatibility must be confirmed against the owner's installed version
  before release.
- The JSON store and single process are intentionally simple for one owner and
  a Stash-focused inventory; broader lifecycle coordination may require a new
  decision if that scope is approved later.