# MayFly Security

MayFly is a public ephemeral-database service: any visitor receives a
short-lived, isolated database instance (PostgreSQL, MySQL, MariaDB, or SQL
Server) reachable over the public internet. This document describes the
security controls deployed, host prerequisites, and residual risks.

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

Each instance is initialised with two database roles. The admin credential
(`mayflyadmin` / `sa` / `root`) is **never returned to users**; only the
unprivileged `appuser` credential appears in `connectionString`.

**PostgreSQL** — roles are created by `00-roles.sql` placed in
`/docker-entrypoint-initdb.d` (executed before the server opens port 5432):
- `mayflyadmin` — `POSTGRES_USER` superuser. Never exposed.
- `appuser` — `NOSUPERUSER NOCREATEDB NOCREATEROLE`.

Because `appuser` is not a superuser:
- `COPY ... FROM/TO PROGRAM` is blocked.
- Server-side file reads (`COPY ... FROM '/etc/passwd'`) are blocked.
- `pg_read_file()` and similar functions are unavailable.

**MySQL / MariaDB** — roles are created by `00-roles.sql` in
`/docker-entrypoint-initdb.d`. `appuser` receives `SELECT, INSERT, UPDATE,
DELETE, CREATE, DROP, INDEX, ALTER, REFERENCES` on the instance database
only; no `FILE`, `SUPER`, or `PROCESS` privilege.

**SQL Server** — there is no init-script path; setup runs after readiness
via `docker exec sqlcmd` (see §4.3). `appuser` is a SQL Server Login mapped
to a database user with `db_datareader`, `db_datawriter`, and `db_ddladmin`
roles on `appdb`. It has no server-level (`sysadmin`, `securityadmin`)
privileges.

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
`<host-port> → <db-container>:<engine-port>` (5432 for postgres, 3306 for
mysql/mariadb, 1433 for mssql). This means:

- DB containers are never directly reachable from the host.
- Blocking the sidecar terminates external DB access without touching
  the DB container itself.

The sidecar is removed by `DestroyAsync` alongside the DB container.

### 3.3 API network membership

`MayFly.Api` is a member of both `mayfly-internal` (to reach the
Provisioner and metadata DB) and `mayfly-users` (to execute queries
against user DBs). It is not a member of `mayfly-ingress`.

Api↔DB traffic runs over the internal `mayfly-users` network and uses
plain TCP (Postgres), `SslMode=None` (MySQL/MariaDB), and
`Encrypt=Optional` + `TrustServerCertificate` (SQL Server) by design —
encryption is opportunistic on an isolated network with no PKI, consistent
across engines.

---

## 4. Container hardening

### 4.1 Default hardening (postgres, mysql, mariadb)

Every PostgreSQL, MySQL, and MariaDB user DB container is started with:

| Control | Value |
|---|---|
| `CapDrop` | `ALL` |
| `CapAdd` | `CHOWN`, `SETUID`, `SETGID`, `FOWNER`, `DAC_OVERRIDE` |
| `SecurityOpt` | `no-new-privileges` |
| `ReadonlyRootfs` | `true` |
| `Tmpfs` mounts | `/tmp` (64 MiB); postgres/mysql/mariadb also add `/var/run/<engine>` (16 MiB) and `/run` (16 MiB) |
| `Memory` limit | 256 MiB (postgres); 512 MiB (mysql, mariadb — InnoDB buffer pool floor) |
| `NanoCPUs` | 500,000,000 (0.5 CPU) |
| `PidsLimit` | 200 |
| `RestartPolicy` | `no` (containers do not restart automatically) |

The read-only rootfs means that even if an attacker achieves code
execution inside the container, they cannot modify the filesystem outside
the declared tmpfs mounts.

### 4.2 SQL Server deviations

SQL Server (`mssql`) requires several relaxations from the default hardening.
All controls not listed here are retained at the same level as §4.1.

| Control | Postgres/MySQL/MariaDB | SQL Server | Reason |
|---|---|---|---|
| `ReadonlyRootfs` | `true` | **`false`** | `sqlservr` writes to `/var/opt/mssql` (data, logs, tempdb, secrets) at runtime; the path layout is not amenable to tmpfs overlays |
| `Memory` | 256–512 MiB | **2 GiB** | SQL Server enforces a minimum 2 GiB working set; instances below this floor crash on startup |
| `NanoCPUs` | 500,000,000 | **1,000,000,000** (1 CPU) | sqlservr minimum viable throughput |
| `PidsLimit` | 200 | **500** | SQL Server spawns more system threads |
| `CapAdd` | `CHOWN SETUID SETGID FOWNER DAC_OVERRIDE` | same **+ `NET_BIND_SERVICE`** | The `sqlservr` binary carries a file capability `cap_net_bind_service=ep`. With `CapDrop=ALL` and `no-new-privileges`, the kernel verifies that any file capability on the exec target is satisfiable within the bounding set. `NET_BIND_SERVICE` absent from the bounding set causes exec to fail with `EPERM`. |

### 4.3 SQL Server setup path (docker-exec, not init-script)

SQL Server does not support `/docker-entrypoint-initdb.d`. Instead, the
`DockerProvisioner` waits for SQL Server to accept connections (via the
readiness poll), then runs a single `docker exec sqlcmd` command that creates
the database, server-level login, and database-level user in one batch.

The Provisioner itself is **not** on the `mayfly-users` network; it reaches
the container via `docker exec` (a control-plane call through the Docker
socket), not via a direct TCP connection. This preserves the property that
only `MayFly.Api` can reach user DB containers over the network.

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
tick and queries the engine-appropriate size function via the `appuser`
connection (`pg_database_size` for postgres; `information_schema` for
MySQL/MariaDB; `sys.master_files` for SQL Server). When the
returned size meets or exceeds `StorageQuotaMb`, the enforcer connects
as the admin credential and flips the app user — or, for SQL Server, the
database itself — to read-only.

For **PostgreSQL**:
```sql
ALTER ROLE appuser SET default_transaction_read_only = on;
```

For **MySQL / MariaDB**:
```sql
REVOKE INSERT, UPDATE, DELETE, CREATE, DROP, ALTER, INDEX
  ON `appdb`.* FROM 'appuser'@'%';
FLUSH PRIVILEGES;
```

For **SQL Server**:
```sql
USE [master];
ALTER DATABASE [appdb] SET READ_ONLY WITH ROLLBACK IMMEDIATE;
```

In all cases, the effect applies to new connections; existing sessions
may complete in-flight transactions before the read-only flag takes hold.
The bounded overshoot between two lifecycle ticks (≤ 30 s) is the
residual risk.

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
