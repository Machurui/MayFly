# MayFly Security

MayFly is a public ephemeral-database service: any visitor receives a
short-lived, isolated PostgreSQL instance reachable over the public internet.
This document describes the security controls deployed, host prerequisites,
and residual risks.

---

## 1. Privilege boundary

### 1.1 API / Provisioner split

The application is split into two services with distinct privileges:

| Service | Docker socket | Internet-facing | Purpose |
|---|---|---|---|
| `MayFly.Provisioner` | yes (`/var/run/docker.sock`) | no | Creates, inspects, and destroys containers |
| `MayFly.Api` | no | yes (via Caddy) | Handles HTTP requests, talks to metadata DB and user DBs |

`MayFly.Api` cannot create or destroy Docker containers directly. All
provisioning calls from the API to the Provisioner carry a 256-bit shared
secret in the `X-Provisioner-Key` header. Requests without a valid key are
rejected with HTTP 401 before any handler is invoked (see `Program.cs` in
`MayFly.Provisioner`).

The `PROVISIONER_KEY` must be set as an environment variable before the
stack starts. The compose file fails fast with an error if it is absent.

### 1.2 No direct inbound access to the Provisioner

The Provisioner service has no published host port. Only the API (on the
shared `mayfly-internal` compose network) can reach it.

---

## 2. Database privilege model

### 2.1 User role: `appuser`

Each instance is initialised with two PostgreSQL roles via container
init-scripts (executed by Postgres before it accepts connections — no
external SQL connection is needed):

- `mayflyadmin` — PostgreSQL superuser. Created by the `POSTGRES_USER`
  environment variable. **Never returned to users.**
- `appuser` — Created by `00-roles.sql` with `NOSUPERUSER NOCREATEDB
  NOCREATEROLE`. This is the credential returned in `connectionString`.

Because `appuser` is not a superuser:
- `COPY ... FROM/TO PROGRAM` is blocked (`must be superuser to COPY to
  or from an external program`).
- Server-side file reads (`COPY ... FROM '/etc/passwd'`) are blocked.
- `pg_read_file()` and similar functions are unavailable.

The `appuser` password is generated with 128 bits of randomness per
instance and is stored encrypted in the metadata database
(`Instance.DbPasswordEnc`) using ASP.NET Core Data Protection (AES-256
via `SecretProtector`). The admin password (`AdminPasswordEnc`) is stored
with the same protection and is used only by the `QuotaEnforcer` when
flipping a user read-only after a disk-quota breach.

### 2.2 Init-script volume lifecycle

Init scripts (containing role passwords) are written into a named Docker
volume (`mayfly-init-<id>`) via a short-lived writer container, then
mounted read-only into the DB container at
`/docker-entrypoint-initdb.d`. After Postgres has initialised the
database, **the volume is removed by `DestroyAsync`** so passwords are
not retained at rest after teardown. The same label-based sweep removes
the volume in reconciliation and orphan-cleanup paths.

---

## 3. Network egress

### 3.1 Internal user network

User DB containers run exclusively on the `mayfly-users` Docker bridge
network, which is declared `internal: true` (no default gateway). A
container on this network cannot initiate outbound TCP/UDP connections to
the internet. This is enforced both in `docker-compose.yml` (for the
compose-owned network object) and by `DockerProvisioner.EnsureNetworksAsync`
(which creates the network with `Internal = true` if absent).

### 3.2 Socat sidecar as sole ingress

Each instance has exactly one socat sidecar container (`mayfly-sidecar-<id>`)
that is dual-homed:

- Connected to `mayfly-users` (to reach the DB by container name)
- Connected to `mayfly-ingress` (a normal bridge network, to publish the
  host port)

The DB container itself has **no published port**. The sidecar forwards
`<host-port> → <db-container>:5432`. This means:

- DB containers are never directly reachable from the host.
- Blocking the sidecar terminates external DB access without touching
  the DB container itself.

The sidecar is removed by `DestroyAsync` alongside the DB container.

### 3.3 API network membership

`MayFly.Api` is a member of both `mayfly-internal` (to reach the
Provisioner and metadata DB) and `mayfly-users` (to execute queries
against user DBs). It is not a member of `mayfly-ingress`.

---

## 4. Container hardening

Every user DB container is started with:

| Control | Value |
|---|---|
| `CapDrop` | `ALL` |
| `CapAdd` | `CHOWN`, `SETUID`, `SETGID`, `FOWNER`, `DAC_OVERRIDE` (minimum for Postgres startup) |
| `SecurityOpt` | `no-new-privileges` |
| `ReadonlyRootfs` | `true` |
| `Tmpfs` mounts | `/tmp` (64 MiB), `/var/run/postgresql` (16 MiB), `/run` (16 MiB) |
| `Memory` limit | 256 MiB |
| `NanoCPUs` | 500,000,000 (0.5 CPU) |
| `PidsLimit` | 200 |
| `RestartPolicy` | `no` (containers do not restart automatically) |

The read-only rootfs means that even if an attacker achieves code
execution inside the container, they cannot modify the filesystem outside
the declared tmpfs mounts.

---

## 5. Disk quota

Disk usage is bounded at two levels:

### 5.1 XFS project quota (hard cap — host prerequisite)

When the host Docker data-root is on an XFS filesystem mounted with
`pquota`, setting `Provisioner:UseXfsQuota=true` enables `XfsVolumeProvisioner`.
It creates each data volume with a per-project size limit enforced by the
XFS kernel. The container cannot write beyond its quota regardless of
what the Postgres process attempts.

**This is a host-level control.** The XFS+pquota data-root must be
configured on the host before starting MayFly (see Section 8).

### 5.2 Portable soft-enforce

Independently of XFS, the `QuotaEnforcer` runs during each lifecycle
tick and queries `pg_database_size(current_database())` via the `appuser`
connection. When the returned size meets or exceeds `StorageQuotaMb`,
the enforcer connects as `mayflyadmin` and runs:

```sql
ALTER ROLE appuser SET default_transaction_read_only = on;
```

All subsequent `appuser` sessions see `default_transaction_read_only =
on`, preventing further writes. Because `ALTER ROLE ... SET` applies at
role level (not per-session), existing connections are not immediately
affected; new connections are read-only.

The bounded overshoot between two lifecycle ticks is the residual risk
(see Section 7).

---

## 6. Application-layer controls

### 6.1 Per-IP active-DB quota

`InstanceService.CreateAsync` applies a **transactional** check: the
number of `Running` or `Provisioning` instances with the same session-IP
must be below 3 before a new instance is created. The check and insert
are performed in a single database transaction to prevent races.

### 6.2 Rate limits

Three rate-limit policies (ASP.NET Core `RateLimiter`, fixed-window, per
remote IP):

| Policy | Endpoint | Limit |
|---|---|---|
| `perip` | All routes | 60 req / min |
| `create` | `POST /api/instances` | 6 req / min |
| `query` | `POST /api/instances/{token}/query` | 60 req / min |

### 6.3 Capability tokens

Instance access tokens are generated with `RandomNumberGenerator.GetBytes(32)`
(256 bits of entropy), URL-safe base64-encoded, and stored as their
plaintext in the metadata DB. Comparison uses `CryptographicOperations.FixedTimeEquals`
to prevent timing attacks.

### 6.4 Encrypted credentials

DB passwords (user and admin) are protected with ASP.NET Core Data
Protection (`AES-256-CBC` + HMACSHA256 by default), persisted to the
`/keys` volume, and scoped to the `MayFly.DbSecret` purpose string.
Credentials at rest in the metadata DB are ciphertext; a key compromise
is required to decrypt them.

### 6.5 Session cookie

`SessionCookieMiddleware` issues a `mayfly_sid` cookie (`HttpOnly`,
`Secure`, `SameSite=Lax`) scoped to the browser session. This cookie
is used for per-IP quota tracking (as a session identifier, not for
authentication).

### 6.6 ForwardedHeaders / KnownProxies

`ForwardedHeadersOptions` trusts `X-Forwarded-For` only from RFC 1918
address ranges (`10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`).
This is safe because `MayFly.Api` has no published host port — only
Caddy, which runs on the `mayfly-internal` private compose network,
can reach it. Spoofed `X-Forwarded-For` from external clients cannot
bypass the proxy trust boundary.

---

## 7. Lifecycle and cleanup

| Mechanism | Behaviour |
|---|---|
| TTL reaper | `LifecycleService` destroys instances whose `ExpiresAt` has passed every 30 s |
| Idempotent destroy | `DestroyAsync` removes the DB container, sidecar, data volume, and init volume; repeated calls succeed |
| Reconcile on startup | On boot, `LifecycleService.RunReconcileAsync` identifies orphaned containers (live Docker container with no metadata record) and destroys them; and marks metadata records `Failed` if the corresponding container has vanished. Reconcile runs at startup, not periodically — a crash mid-reconcile is recovered on the next service restart. |
| Destroying-state re-drive | Metadata rows left in `Destroying` (process crashed between the state-claim and the provisioner call) are re-driven through `DestroyAsync` on startup reconcile and transitioned to `Destroyed`. |
| Orphan writer/init-volume sweep | `RunReconcileAsync` calls `SweepOrphansAsync` after each reconcile pass. This force-removes any surviving `mayfly.role=writer` containers (always transient) and any `mayfly-vol-*`, `mayfly-init-*`, or `mayfly.instance`-labelled volumes not belonging to an active instance. This reclaims writer containers and init volumes leaked by a crash in the pre-metadata-write window. |
| Orphan init-volume cleanup | `DestroyByInstanceAsync` performs a label-based volume sweep (`mayfly.instance=<id>`) to catch the credential-bearing init volume even if the container crashed mid-provision |

---

## 8. Host prerequisites

| Prerequisite | Required for |
|---|---|
| Docker data-root on XFS + `pquota` mount option | Hard disk quota (Section 5.1) |
| `PROVISIONER_KEY` env var | Provisioner auth (Section 1.1) |
| `MAYFLY_DB_PASSWORD` env var | Metadata DB password (compose) |
| `PUBLIC_HOST` env var | Connection-string hostname returned to users |
| Caddy `KnownProxies` configuration | Matching the RFC 1918 ranges in `ForwardedHeadersOptions` |

Without XFS+pquota, disk usage falls back to the soft-enforce only
(Section 5.2). All other controls are independent of XFS.

---

## 9. Residual risks

| Risk | Severity | Mitigation |
|---|---|---|
| **Capability-URL leak** — token in connection string is a bearer credential | Medium | Short TTL (default 3 h); token rotates per instance; constant-time comparison |
| **DB-to-DB isolation relies on non-superuser** — a PostgreSQL vulnerability granting superuser to `appuser` would let one tenant affect another's DB | Medium | Per-instance network isolation (separate container per instance, on `internal` network) is in place; per-instance network namespace (separate bridge per instance) is a future hardening item |
| **Quota soft-enforce overshoot** — between lifecycle ticks (≤ 30 s), a burst write can exceed quota | Low | XFS hard cap (host prerequisite) eliminates the window; without XFS, up to ~30 s of excess writes are possible |
| **Limited `CREATE EXTENSION`** — `appuser` can install extensions available in `shared_preload_libraries` | Low | Only `pg_trgm` and `uuid-ossp` are pre-installed by the init script; the server cannot load arbitrary shared libraries |
| **Socat sidecar trust** — the sidecar image (`alpine/socat:1.8.0.0`) is pinned by tag, not digest | Low | Monitor for tag mutation; future hardening: pin by digest |
| **Pre-metadata-write crash window** — a crash between init-volume creation and metadata row insert leaves an orphan `mayfly-init-*` volume holding the `appuser` password | Low | The orphan init volume guards a never-created DB (no running postgres, no accessible data). The startup orphan sweep (`SweepOrphansAsync`) reclaims it on next restart. The window is bounded by the time between volume creation and the first successful `ListManagedAsync` call in reconcile. |
| **Reconcile is startup-only** — a crash mid-reconcile is not retried until the next service restart | Low | Each individual direction (Destroying re-drive, Direction 1, Direction 2, orphan sweep) is wrapped in per-item try/catch and idempotent; a partial reconcile leaves the system in a safe intermediate state that is fully resolved on the subsequent restart. |

---

## 10. Reporting a vulnerability

To report a security issue, please email the maintainers directly rather
than opening a public issue. Include a description of the vulnerability,
steps to reproduce, and the potential impact.
