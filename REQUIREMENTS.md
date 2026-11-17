# Personal Server Manager - Requirements

Status: Draft for a new project; not an implementation specification.
Date: 2026-11-15

## Current goal

Build a private, owner-operated web application for selected services on one
Windows PC. The first usable release is a Stash-focused vertical slice: connect
multiple Stash instances, search scenes, images, and performers across all
configured instances through each instance's GraphQL API, and observe each
instance. Process lifecycle management is excluded from this release. Search
across other applications is not part of this first release.

Later candidates are Hydrus search, SwarmUI/ComfyUI observation, and llama.cpp
control and observation. MCP connection information and GPU conflict handling
remain broader goals, not first-release commitments.

## 1. Purpose and handoff

Build a custom web application for one owner's Windows PC used as a server.
Provide a unified view and safe control of explicitly selected native applications,
Windows Services where applicable, and Docker workloads. Make GPU workload conflicts,
existing application monitoring, and MCP connection information easy to understand.

This document is intended to be copied into the new project. It is self-contained;
the existing prototype is reference material, not a required architecture or migration
target. No programming language, UI framework, database, deployment topology, or
plugin mechanism has been selected for the new project.

Terminology:

- **Workload:** A configured application, Windows Service, or Docker workload.
- **Instance:** One configured connection or lifecycle target for an application
  integration. An application integration may have multiple independent instances.
- **Application:** The management software being specified here.
- **Owner:** The person configuring and operating the application.
- **Ready:** Available for its intended use, not merely having a live process.
- **Busy:** Performing work according to an application-specific signal, if available.
- **MCP:** Model Context Protocol; distinct from ordinary HTTP health endpoints.

## 2. Confirmed needs

The following requirements come directly from the owner:

| ID | Requirement |
| --- | --- |
| C-01 | Build a new, personally tailored project rather than adopting an existing product as the main interface. |
| C-02 | Manage software on a Windows PC through a web interface. |
| C-03 | Include selected Docker workloads in the same interface as native applications. |
| C-04 | Represent LAN-only services and services published through an existing nginx instance running in Docker. |
| C-05 | Show useful dashboard information and automatically identify GPU workload conflicts; suggest stopping an incompatible workload before starting another. |
| C-06 | Reuse monitoring information exposed by applications, including llama.cpp and the owner's Strata installation where supported. |
| C-07 | Make information about the selected applications' MCP services quickly accessible. |
| C-08 | Manual configuration is acceptable, as is extension through plugin-like integrations. |
| C-09 | Never hardcode credentials. Only the services and integrations the owner needs are required. |
| C-10 | Establish requirements first; evaluate architecture, software stack, and implementation practices in the new project. |
| C-11 | The first usable release centers on multiple Stash instances. Search covers scenes, images, and performers across all configured instances through each instance's GraphQL API, and results identify their source instance. The release includes observation but not process lifecycle management. |
| C-12 | The first release is for local-network access. Public internet access is not required. |
| C-13 | Hydrus search, SwarmUI/ComfyUI observation, and llama.cpp control/observation are later candidates. Strata is not required for the first usable release. |
| C-14 | Initial GPU conflict handling should warn or block; a confirmed stop/switch workflow is not required in the first release. |
| C-15 | The application should allow multiple configured instances of the same application/integration, with instance-specific connection details and supported management. |
| C-16 | Stash search uses its GraphQL API whether an instance runs on the manager's PC or another reachable host. Process lifecycle management is not required in the first release. |

## 3. Proposed scope and assumptions

Everything below is a proposed baseline unless marked confirmed above. Open decisions
are collected in section 11; they must not silently become implementation facts.

- One Windows host and one owner initially; no fleet or multi-tenant management.
- Desktop and mobile browsers on trusted networks are the initial clients.
- The owner explicitly registers workloads; automatic discovery is not required.
- The first release integrates with Stash only and may register multiple independent
  Stash instances. It connects to their GraphQL APIs for search and observation; it does
  not start, stop, or restart their processes or containers.
- The manager is LAN-only for the first release. Published workload URLs do not imply
  that the management interface should be published to the internet.
- Initial GPU conflict handling warns or blocks; it does not stop or unload workloads.
- MCP connection information is a later candidate. Capability browsing and retrieving
  information through MCP remain separately scoped enhancements.
- Tailored integration code is acceptable. A general-purpose plugin marketplace,
  universal service catalog, and support for unused applications are not goals.

### Initial release candidate

1. Register multiple Stash instances and search scenes, images, and performers through
  each instance's GraphQL API. Identify each result's source instance and isolate
  per-instance failures.
2. Observe each Stash instance and link to its existing interface. Process lifecycle
  operations are out of scope for the first release.
3. LAN-only authenticated access, durable configuration, and recovery guidance.
4. A Stash integration validated against the owner's installed version and configuration.

### Later, only when needed

- Hydrus search; SwarmUI/ComfyUI observation; llama.cpp control and observation.
- Additional lifecycle targets and monitoring integrations from the owner's inventory.
- Stash process lifecycle management, if still needed after the read-only integration.
- GPU conflict warnings/blocking across workloads, then optional telemetry and
  busy-aware switching if the owner still needs them.
- MCP connection information, capability browsing, and explicitly approved retrieval.
- Profiles grouping workloads, scheduled actions, or opt-in idle unloading.
- Notifications outside the dashboard, external plugins, or remote host agents.

### Not required initially

- Kubernetes, clustering, multi-host scheduling, multi-user roles, or billing.
- A replacement for nginx, Docker's deployment tooling, or each application's own UI.
- Public management access, automatic reverse-proxy editing, or certificate management.
- Automatic software/image updates, installation, or model downloads.
- An AI agent that decides which services to stop or executes arbitrary commands.
- A general MCP gateway, MCP hosting platform, or conversational AI interface.
- Migration of prototype code or configuration without an explicit decision.

## 4. Functional requirements

### 4.1 Workload inventory and configuration

- F-01: Register, inspect, edit, disable, and remove selected workload instances with
  stable identifiers. Multiple instances may use the same application integration.
  Removing configuration must not implicitly stop or delete an instance.
- F-02: Store a name, optional description, lifecycle target, local/public URLs,
  monitoring settings, resource claims, and optional MCP definitions for each instance.
  Keep instance configuration and state independent, including endpoint and secret
  references where applicable.
- F-03: Support lifecycle-specific settings rather than treating everything as a
  process: executable/arguments/working directory for native applications, service
  identity for Windows Services, and engine/container identity for Docker.
- F-04: Allow non-secret environment configuration and references to external secrets.
  Missing required secrets must produce an actionable error, not empty substitutions.
- F-05: Validate identifiers, required fields, references, dependency cycles, resource
  rules, and supported capabilities before accepting configuration.
- F-06: Support intentional start dependencies with readiness requirements and finite
  timeouts. Reject impossible dependency/resource combinations before side effects.
  Refuse stopping a required dependency unless an explicit dependent-stop plan is
  confirmed. Do not silently stop dependents or shared dependencies.
- F-07: Make configuration durable and exportable without resolved secret values.
  Report persistence failures; do not overwrite invalid persisted data with defaults.
  The configuration editing medium and backup format remain architecture decisions.

### 4.2 Dashboard and observation

- F-08: Show all enabled, configured workload instances, their available actions,
  local/public links, lifecycle state, readiness/health, and the most recent operation
  result. Distinguish instances that use the same application integration.
- F-09: Distinguish at least unknown, stopped, starting, running, stopping, and failed
  lifecycle states. Show health, readiness, busy state, and observation freshness
  separately where available; do not infer them solely from process existence.
- F-10: Refresh status automatically. Show when observations are stale or unavailable;
  a failed probe is not proof that the workload stopped or released its GPU resources.
- F-11: Provide useful workload details: identity, available logs/diagnostics,
  monitoring source, last observation time, and human-readable failure explanations.
- F-12: Keep failures isolated: an unreachable application or Docker engine must not
  prevent the rest of the dashboard from functioning. Network location labels must
  distinguish configured exposure from verified reachability.

### 4.3 Lifecycle control

- F-13: Start, stop, and restart workloads where their lifecycle target supports it.
  Clearly distinguish observation-only workloads from those the application controls.
- F-14: Report an accepted operation as pending until its completion conditions are
  observed. Starting a process/container alone must not imply readiness.
- F-15: Prefer graceful shutdown with a finite timeout. Force termination must be a
  distinct, explicit action against a verified identity, never an implicit fallback.
- F-16: Prevent duplicate/conflicting operations across tabs, clients, dependencies,
  instances, and resource claims. Starting an already-running instance must not
  duplicate it.
- F-17: Track native workloads using sufficient identity evidence, not process name
  or PID alone. Distinguish a launcher process from its actual managed application.
  Never stop a process whose identity cannot be verified.
- F-18: Reconcile actual instance state after manager restart and detect configured
  instances started externally. Do not duplicate them or claim ownership without
  verification; ambiguous matches must require owner resolution before control.
- F-19: The manager must not require restarting managed workloads to recover its
  dashboard. By default, manager shutdown/restart must leave workloads running.
- F-20: When a multi-step operation fails, report completed steps, remaining state,
  and recovery options. Do not silently restart, force-kill, or roll back workloads.

### 4.4 GPU conflicts and workload switching

- F-21: Allow manually configured shared/exclusive resource claims, initially an
  exclusive claim for the physical GPU shared by LLM and image-generation workloads.
  Native and Docker users of that GPU must belong to the same conflict domain.
- F-22: Before starting a workload or its dependencies, evaluate conflicts using
  current observations plus in-progress operations. Explain the blocker and proposed
  resolution. An unknown resource state must not be presented as safe availability.
- F-23: Offer a confirmed switch plan: stop/unload the conflicting workload, verify
  release, start the requested workload, and verify readiness. Reserve the resource
  through this transition so another request cannot race the operation.
- F-24: Distinguish stopping an application from unloading its model where the
  integration supports both. Do not assume an idle application has released VRAM.
- F-25: Where supported, display active jobs and offer bounded waiting before a
  switch. Unknown busy state must be explicit; interrupting work requires confirmation.
- F-26: Optionally enrich the policy with measured GPU usage and VRAM availability.
  Unsupported readings must display as unavailable. Declared claims and measurements
  are different evidence; neither guarantees that every external GPU user is known.
- F-27: Document enforcement boundaries. Out-of-band starts cannot be prevented by
  dashboard policy alone. Detect known conflicts, notify the owner, and do not
  automatically terminate externally started software.

### 4.5 Existing monitoring and links

- F-28: Consume each configured instance's existing health/metrics API when available. Start with
  a small useful subset, such as readiness, loaded model, active requests, or throughput,
  depending on what the installed application/version actually exposes.
- F-29: Keep a direct link to each application's own monitoring UI. Embedded pages
  are optional and must not be assumed to work across authentication/frame policies.
- F-30: Bound and time out integration requests; handle unsupported versions and
  malformed responses explicitly. Monitoring must not accidentally trigger model
  loading or otherwise mutate the workload.
- F-31: Keep one authoritative lifecycle controller per configured instance. An external
  controller may be integrated, but two independent supervisors must not compete.

### 4.6 MCP information

- F-32: Associate zero or more MCP definitions with an instance: display name,
  transport, endpoint or launch command, secret references, and usage notes.
  Do not assume every MCP server exposes an HTTP URL.
- F-33: Provide quick access to connection details and copyable client templates
  without revealing credentials. Label configured information separately from
  protocol-verified connectivity and supported capabilities.
- F-34 (later): Browse advertised tools/resources/prompts where supported by the
  configured server's protocol version and transport. Launching a stdio server is
  an operation, not a passive network probe; it must respect lifecycle/resource rules.
- F-35 (later): Retrieve selected information via explicitly configured MCP calls.
  Show provenance, timestamp, failures, and required inputs. Do not assume that a
  tool name or read-only annotation proves it has no side effects; use an owner-approved
  allowlist and confirmation policy. No autonomous tool execution is required.

## 5. Security and secrets requirements

- S-01: No hardcoded credentials, default passwords, committed tokens, or real secrets
  in examples, exports, URLs, telemetry, tests, or documentation.
- S-02: Keep secret references separate from resolved values. Choose an appropriate
  protected storage/provisioning mechanism during architecture design; define access,
  replacement, and backup/recovery behavior. Git ignore rules alone are not protection.
- S-03: Require authenticated access to management operations and sensitive runtime
  information. Define a safe first-run enrollment process without a shipped password.
- S-04: Use restricted network binding by default. Require explicit configuration
  for LAN exposure and encrypted non-loopback access or an approved protected transport.
- S-05: Run with the least privileges needed. Treat Docker access, Windows Service
  control, configuration editing, and command execution as privileged capabilities.
- S-06: Only owner-defined actions/targets are executable. Preserve argument boundaries;
  do not interpolate untrusted values into a shell. Intentional scripts are trusted
  configuration, not an unrestricted web terminal.
- S-07: Protect browser sessions and state-changing requests, including relevant
  CSRF, origin, and WebSocket protections for the selected stack.
- S-08: Redact secrets from application-controlled output. Child-process logs may
  themselves contain secrets: restrict access, bound retention, and document that
  arbitrary application output cannot be guaranteed secret-free by pattern redaction.

## 6. Reliability and operational quality

- Q-01: Configuration and last operation outcomes survive restart. Re-observe current
  state; do not treat persisted observations as current fact. Interrupted operations
  must be reconciled, not reported successful or blindly replayed.
- Q-02: Bound external calls, operation waits, log buffers, and retained history.
  Propagate useful errors and cancellation where supported; no silent success defaults.
- Q-03: Record who requested each action, its target, timestamps, outcome, and related
  steps. Prefer structured, correlated diagnostics without secret values.
- Q-04: Show state using text as well as color. Make primary actions usable on desktop
  and mobile, with clear pending states and confirmations describing their effects.
- Q-05: Support repeatable installation, configuration backup/restore, upgrades, and
  troubleshooting. Clarify startup without login and Docker availability after reboot.
- Q-06: Keep status and operation coordination independent of an individual browser
  session. Closing the page must not implicitly kill a workload or duplicate an action.
- Q-07: Use supported dependencies and runtime versions. Document tested Windows,
  Docker, GPU/driver, application API, and MCP compatibility boundaries.

Proposed acceptance targets for a broader release, to confirm against the real workload
inventory. They are not all gates for the Stash-focused first release:

- At 20 configured workloads and two browser sessions, reflect a completed local
  status probe in the dashboard within 5 seconds under normal operating conditions.
- By 15 seconds after the last successful observation, label it stale or show an
  explicit unavailable state; long-running operation progress is separate.
- Acknowledge a submitted local action as pending or rejected within 1 second under
  the agreed test conditions. Backend/model load time is not included in that target.
- Resource overhead and history-retention budgets must be agreed before release;
  do not invent hardware-independent CPU/RAM guarantees.

## 7. Acceptance scenarios for the broader target

The first-release scenarios are A-01, A-08, A-09, and A-13, adapted to the Stash
integration, including multiple instances. Scenarios that require process lifecycle
control, Docker, GPU switching, dependencies, MCP, or a 20-workload inventory are
later-release checks.

| ID | Scenario and expected result |
| --- | --- |
| A-01 | Configure at least two Stash instances; search scenes, images, and performers through each instance's GraphQL API, identify each result's source instance, and expose independent observation and links. |
| A-02 | Start Stash through the manager; show the operation as pending until Stash's supported readiness observation confirms ready, or show an actionable timeout. |
| A-03 | Restart the manager while Stash runs; Stash remains running and is correctly reconciled without a duplicate start or false ownership claim. |
| A-04 | A tracked PID is reused by an unrelated process; the manager does not report or terminate that process as the configured workload. |
| A-05 | With the LLM holding the exclusive GPU claim, request image generation; show the conflict and proposed switch. Cancel leaves both workloads unchanged. |
| A-06 | Confirm the switch; verify release before starting image generation. If shutdown/unload fails or times out, do not start the replacement; show actual observed state. |
| A-07 | Submit conflicting starts concurrently from two sessions; at most one acquires the exclusive resource, including during pending startup. |
| A-08 | Make one configured Stash GraphQL endpoint unavailable; show its search/observation failure while other instances continue to return results and status. |
| A-09 | Make configuration persistence fail or remove a required secret; show the error, preserve existing valid data, and do not report the failed operation as successful. |
| A-10 | Use a missing/cyclic dependency or a dependency that cannot become ready; reject invalid plans before side effects and never start a dependent before readiness. |
| A-11 | Stop a dependency used by another running workload; reject or present an explicit confirmed dependent-stop plan, never silently break that workload. |
| A-12 | Open a workload's MCP details and export a template; correct transport/connection instructions appear and no resolved secret is included. |
| A-13 | From an unauthenticated client, attempt a management action; deny it. Verify examples, exports, operation diagnostics, and errors do not reveal supplied test credentials. |
| A-14 | Close the browser during a Stash operation or restart the manager mid-operation; Stash is not implicitly killed, interrupted work is reconciled, and no unsafe step is blindly replayed. |
| A-15 | Exercise 20 representative configured workloads with two sessions; measure the proposed refresh, stale-state, and action-acknowledgment targets using controlled probe delays. |
| A-16 | On the actual Windows/Docker installation, reboot and follow the documented startup procedure; the dashboard accurately reports available, unavailable, and externally running workloads. |

## 8. Inventory to supply before implementation

Do not populate this with credentials. Use secret-reference names only.

| Field | Information needed per selected workload |
| --- | --- |
| Identity | Name, purpose, exact product/repository, installed version. |
| Runtime | Native application, Windows Service, Docker container, or Compose project; any WSL boundary. |
| Lifecycle | How it starts today, working directory, arguments, graceful stop/unload options, external supervisor. |
| Execution context | User/service account, privilege needs, interactive desktop or login requirements. |
| Observation | Identity evidence, readiness URL, health/metrics API, busy/queue information, own monitoring UI. |
| Access | Local URL, optional public URL, authentication method, secret-reference names. |
| Resources | Physical GPU identity, exclusivity/coexistence rules, optional VRAM estimate, release evidence. |
| Relationships | Required dependencies, preferred startup order, consequences of interruption. |
| MCP | Transport, endpoint or launch configuration, protocol/client needs, information to access quickly. |

Known candidates, not a final inventory:

- llama.cpp / llama-server: exact build, model settings, start method, and monitoring to confirm.
- Image generation server: product, version, API, and stop/unload behavior to identify.
- Strata: exact product/repository and available monitoring interface to identify.
- nginx in Docker: existing routing context; whether it should be controllable is unconfirmed.
- Other native and Docker services: owner to enumerate; none are included by default.

## 9. Engineering expectations for the new project

- Prefer the simplest design satisfying the selected scope. Keep lifecycle, observation,
  resource policy, and UI responsibilities distinct without requiring microservices.
- Keep integration-specific behavior behind explicit contracts and capability checks.
  Prefer configuration and built-in adapters before runtime-loaded external plugins.
- Keep configuration, observed state, desired actions, and operation history conceptually
  distinct. Make transitions, ownership, failure, and recovery semantics explicit.
- Test orchestration with deterministic simulated providers, then validate actual
  Windows/Docker behavior with opt-in integration tests. Never kill unrelated software
  or use real owner credentials in automated tests.
- Cover dependency ordering, concurrent starts, resource release, restarts, stale data,
  partial failures, secret handling, and authentication with repeatable tests.
- Establish formatting, static checks, build/test automation, dependency maintenance,
  and release documentation appropriate to the chosen stack.
- Record consequential architecture decisions with alternatives and trade-offs.
  Avoid abstractions whose only justification is hypothetical future integrations.

## 10. Architecture and stack evaluation agenda

Evaluate these after the inventory and initial scope are agreed. Existing .NET/Blazor
code is useful prior experience, not a predetermined decision.

1. Deployment: Windows-hosted application, optional agent separation, account model,
   startup without login, interactive-app constraints, and Docker/WSL connectivity.
2. Backend and frontend: Windows APIs, maintainability, browser update mechanism,
   operational footprint, testing support, and the owner's familiarity.
3. Coordination: state model, verified identities, operation serialization, dependency
   planning, resource reservations, timeouts, and restart recovery.
4. Persistence and secrets: atomic configuration changes, history, schema evolution,
   backups, recovery, and a protected credential mechanism.
5. Integrations: native process/service APIs, Docker API versus CLI, application
   monitoring, MCP SDK compatibility, and optional external-controller adapters.
6. Security: authentication/enrollment, network boundaries, TLS/reverse proxy, least
   privilege, trusted configuration, logging/redaction, and threat model.
7. Delivery: incremental vertical slices, test strategy, CI, packaging, and upgrades.

## 11. Open decisions

Resolve the first four before selecting an architecture; resolve the remainder before
their corresponding feature is implemented.

1. What are the exact Stash versions and instance URLs? Verify GraphQL authentication,
  search behavior for scenes/images/performers, pagination, and useful observation fields.
  Process lifecycle control is excluded from the first release.
2. The initial host is Windows 11 Pro with an RTX 5090 and Docker Desktop/WSL2. What
  lifecycle and observation boundaries apply to Stash on this host? Starting before
  user login is not required initially, but is a future priority.
3. Access is LAN-only initially. Which authentication/enrollment approach is acceptable?
  Internet exposure remains out of scope until its security requirements are resolved.
4. MCP connection details are the selected baseline, but which concrete MCP service and
  workload should expose them, and is that needed in the first release?
5. GPU warn/block is the initial preference. Should conflicts be in the multi-instance
  Stash first release, or wait until GPU-consuming workloads are integrated? Is waiting
   for active work required, and is unload preferable to stopping each application?
6. Which workloads are started outside the manager or supervised by another tool?
   What control, if any, should the manager have over them?
7. Are Windows Service and Compose-stack lifecycle actions needed initially? Are any
   target programs GUI applications requiring an interactive Windows session?
8. Is manual file configuration preferable, a configuration UI required, or both?
   What existing secret storage, if any, should be used?
9. Which existing monitoring fields and log views are genuinely useful per application?
   Are current status and short history enough, or are charts/long-term history required?
10. Are the proposed response/freshness targets suitable, and what resource/retention
    budgets are acceptable on the actual host?

## 12. Next-project starting point

Copy this document into the new project, confirm the Stash versions and their supported
search, readiness, and control interfaces, then choose the architecture/stack for a
multi-instance Stash first vertical slice. Validate cross-instance search, per-instance
observation and control, authentication, and LAN-only operation end to end before adding
other application integrations or GPU conflict policy.