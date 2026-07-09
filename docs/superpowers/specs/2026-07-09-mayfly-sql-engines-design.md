# MayFly — Sous-projet 2 : Engines SQL (MySQL, MariaDB, SQL Server) — Design

- **Date** : 2026-07-09
- **Statut** : Validé en brainstorming, en attente de relecture utilisateur
- **Sous-projet** : 2 — Engines SQL supplémentaires (MongoDB déféré à son propre sous-projet)
- **Pré-requis** : slice-1 + sous-projet 6 (durcissement) mergés sur `master`

---

## 1. Contexte & objectif

MayFly ne provisionne aujourd'hui que **PostgreSQL** ; tout le code est pg-spécifique (`DockerProvisioner` hardcode `postgres:16-alpine`, `POSTGRES_*`, init-scripts pg, `pg_isready`, `pg_database_size`, rôle `NOSUPERUSER` ; `QueryExecutor` = Npgsql ; connection string `postgresql://`). Ce sous-projet ajoute **MySQL, MariaDB et SQL Server**, en réappliquant tout le durcissement du sous-projet 6 (non-superuser, egress, read-only rootfs, quota) par engine.

### Périmètre (décisions verrouillées)
- **Engines** : MySQL + MariaDB + SQL Server. **MongoDB est hors périmètre** (NoSQL : casse le modèle console SQL, le rôle non-superuser et le soft-enforce → sous-projet séparé).
- **Données initiales** : **Blank seulement** pour les 3 nouveaux engines. Le seed Northwind (par dialecte) et les autres templates arrivent au sous-projet 3 ; le wizard n'active Northwind que pour les engines qui l'ont (Postgres).
- **Abstraction** : `IEngineProvider` (Provisioner) + `QueryExecutor` générique ADO.NET + `IEngineClient` (Api). Refactor Postgres d'abord, sans changement de comportement.
- **Cible & validation** : inchangées (VPS Linux ; validation via la VM Linux de Docker Desktop).

---

## 2. Architecture — deux abstractions

### 2.1 Côté Provisioner : `IEngineProvider` (provisioning)
```
string EngineId;                 // "postgres" | "mysql" | "mariadb" | "mssql"
string Image;
int    Port;
IList<string> BuildEnv(string adminUser, string adminPassword, string db);
EngineSetup   BuildSetup(string appUser, string appPassword, string initialData);
                                 // init-scripts (files) OR post-ready docker-exec plan
IList<string> ReadinessExec();   // docker-exec command that returns 0 when TCP-ready
void          ApplyHardening(HostConfig hc);   // engine-specific mem/rootfs/tmpfs/caps
Credentials   GenerateCredentials();           // engine-compliant admin+app passwords
```
`DockerProvisioner` devient **agnostique** : crée volume + conteneur (image/env/hardening du provider) → applique le setup (init-volume+writer pour les engines à `initdb.d` ; docker-exec pour SQL Server) → attend la readiness (sonde du provider) → crée le sidecar → renvoie le résultat. **Toute la topologie SP6 est réutilisée telle quelle** : réseau `internal` + sidecar socat, read-only rootfs (sauf mssql, cf. §4), init-volume via writer, reconcile + sweep orphelins, quota volume, labels `mayfly.instance`/`mayfly.role`.

### 2.2 Côté Api : `IEngineClient` (query + enforce)
```
DbConnection CreateConnection(string connectionString);   // Npgsql / MySqlConnector / Microsoft.Data.SqlClient
string BuildAdoConnectionString(host, port, db, user, pwd);
string BuildDisplayConnectionString(host, port, db, user, pwd);  // user-facing (URI vs Server=…)
string SizeQuerySql();                 // bytes used by the db
string SoftEnforceReadOnlySql(string appUser);
```
Les 3 engines SQL exposent tous `DbConnection`/`DbCommand` (ADO.NET) → le `QueryExecutor` reste **générique ADO.NET** (timeout, cap 500 lignes, capture d'erreur, re-throw cancellation — inchangés) et ne fait que résoudre `IEngineClient` par `inst.Engine` pour la fabrique de connexion + la connection string. Le size monitor (`LifecycleService`) et le `QuotaEnforcer` prennent leur SQL du `IEngineClient`. `InstanceDto` construit la connection string d'affichage via le client. `ApiSpecValidator` accepte `postgres|mysql|mariadb|mssql`.

**Admin engine-spécifique** : l'utilisateur admin diffère par engine (`mayflyadmin` / `root` / `sa`). L'entité `Instance` persiste `AdminUser` (en plus de `AdminPasswordEnc`) — le `QuotaEnforcer` et le size-monitor s'y connectent en admin avec `inst.AdminUser`/`inst.AdminPasswordEnc` (plus de constante « mayflyadmin »). `CreateInstanceResult` le porte déjà.

### 2.3 Refactor préalable (behavior-preserving)
Extraire `PostgresEngineProvider` (Provisioner) et `PostgresEngineClient` (Api) du code pg-spécifique existant, sans changer le comportement. Les **40 tests SP1+SP6 restent verts** — garde-fou de régression. Registration des providers/clients par `EngineId`.

---

## 3. Spécificités par engine

Postgres = baseline existante. Les 3 nouveaux :

| | **MySQL** | **MariaDB** | **SQL Server** |
|---|---|---|---|
| Image | `mysql:8.4` | `mariadb:11.4` | `mcr.microsoft.com/mssql/server:2022-latest` (Developer) |
| Port | 3306 | 3306 | 1433 |
| Env admin | `MYSQL_ROOT_PASSWORD`, `MYSQL_DATABASE=appdb` (admin=root) | `MARIADB_ROOT_PASSWORD`, `MARIADB_DATABASE=appdb` | `ACCEPT_EULA=Y`, `MSSQL_SA_PASSWORD` (admin=sa) |
| Setup rôle | init-script : `CREATE USER appuser@'%'` + `GRANT SELECT,INSERT,UPDATE,DELETE,CREATE,DROP,INDEX,ALTER,REFERENCES ON appdb.*` — **pas de FILE/SUPER** | idem | docker-exec `sqlcmd` après readiness : `CREATE LOGIN` + `CREATE USER FOR LOGIN` + db-roles scopés appdb — **pas sysadmin, pas xp_cmdshell** |
| Readiness (exec) | `mysqladmin ping -h127.0.0.1 -uroot -p…` | `mariadb-admin ping -h127.0.0.1` | `sqlcmd -C -S127.0.0.1 -Q "SELECT 1"` |
| Size (bytes) | `SUM(data_length+index_length)` de `information_schema.tables WHERE table_schema='appdb'` | idem | `SUM(size)*8192` de `sys.master_files WHERE database_id=DB_ID('appdb')` |
| Soft-enforce | `REVOKE INSERT,UPDATE,DELETE,CREATE,DROP,ALTER,INDEX ON appdb.* FROM appuser@'%'; FLUSH PRIVILEGES;` | idem | `ALTER DATABASE [appdb] SET READ_ONLY WITH ROLLBACK IMMEDIATE;` |
| Driver | `MySqlConnector` | `MySqlConnector` | `Microsoft.Data.SqlClient` |
| Display conn. | `Server=host;Port=3306;Database=appdb;User Id=appuser;Password=…` | idem | `Server=host,1433;Database=appdb;User Id=appuser;Password=…;TrustServerCertificate=True` |
| Non-superuser proof | appuser sans `FILE` (bloque `LOAD DATA INFILE`/`INTO OUTFILE`) | idem | appuser non-`sysadmin` + `xp_cmdshell` refusé |

**MariaDB ≈ MySQL** : même protocole/driver/modèle de rôle → `MariaDbEngineProvider` réutilise/sous-classe MySQL (diffère par image + préfixe env + commande ping) ; `MySqlEngineClient` sert les deux. Peu cher.

---

## 4. SQL Server — déviations (engine-spécifiques)

1. **Complexité du mot de passe** : politique SA (≥8 car., 3/4 des catégories maj/min/chiffre/symbole). Les mots de passe hex échouent → `GenerateCredentials()` de mssql produit des mots de passe conformes (hex + suffixe fort déterministe, ex. `Aa1!`, ou générateur multi-classes), pour admin (sa) **et** appuser.
2. **Setup via docker-exec** (pas d'`initdb.d`) : après readiness, `docker exec` de `sqlcmd` (`/opt/mssql-tools18/bin/sqlcmd -C -S127.0.0.1`) exécute le setup **localement dans le conteneur** (login + user + db-roles scopés). Via l'API Docker (socket), **pas le réseau** → Provisioner reste hors de `mayfly-users`. mssql **n'utilise pas** l'init-volume/writer.
3. **Plancher mémoire** : `ApplyHardening` relève la limite mémoire conteneur à **~2 GB pour mssql** (256 MB par défaut ne démarre pas). Conséquence assumée : un instance mssql coûte ~2 GB (moins d'instances concurrentes). Distinct du quota **disque**.
4. **Read-only rootfs relâché** : `ReadonlyRootfs=false` **pour mssql uniquement** (chemins runtime peu prévisibles). **Tout le reste du durcissement s'applique** : egress bloqué + sidecar, appuser non-sysadmin, cap-drop, no-new-privileges, limites, user `mssql` non-root de l'image.

---

## 5. Frontend

- **EnginePicker** : activer MySQL / MariaDB / SQL Server (glyphes/couleurs déjà dans le design importé). MongoDB reste grisé (« soon »).
- **InitialDataPicker** : **Northwind gaté par engine** — activé seulement si l'engine sélectionné le supporte (Postgres) ; sinon Blank seul.
- **`buildSnippets` engine-aware** (les 5 langs bash/python/node/go/.net) :
  - MySQL/MariaDB : `mysql` CLI, PyMySQL, `mysql2` (node), `go-sql-driver/mysql`, MySqlConnector (.net)
  - SQL Server : `sqlcmd`, `pyodbc`, `mssql`/tedious (node), `denisenkom/go-mssqldb`, `Microsoft.Data.SqlClient` (.net)
  - Le composant parse la connection string selon l'engine (format par-engine).
- **Label engine** : map `engine → label` (PostgreSQL/MySQL/MariaDB/SQL Server) — **résout le Minor SP1** (« PostgreSQL » codé en dur dans InstanceView).
- **Console** : marche pour tous les SQL (`@codemirror/lang-sql` avec le dialecte selon l'engine, sinon SQL générique) ; `QueryResults` déjà générique.
- **Dashboard/instance** : colonne/entête engine affiche le bon label + glyphe (port issu de l'InstanceDto).

---

## 6. Phasage

| Phase | Contenu |
|---|---|
| **1 — Abstraction (fondation)** | `IEngineProvider` + `IEngineClient` ; refactor Postgres → providers/clients, **sans changement de comportement** ; les 40 tests SP1+SP6 restent verts |
| **2 — MySQL** | `MySqlEngineProvider` + `MySqlEngineClient` ; valide l'abstraction bout-en-bout |
| **3 — MariaDB** | réutilise/sous-classe MySQL (image + env + ping) |
| **4 — SQL Server** | setup docker-exec, credentials conformes, plancher mém 2 GB, rootfs relâché, soft-enforce `ALTER DATABASE READ_ONLY`, `Microsoft.Data.SqlClient` |
| **5 — Frontend + e2e** | activer engines, gater Northwind, snippets engine-aware, label-map, validator ; e2e full-stack par engine SQL |

---

## 7. Tests (via API Docker, VM Docker Desktop, collection `docker-sequential`)

- **Par engine** : provision → **preuve non-superuser** (FILE / xp_cmdshell refusé) → **egress bloqué** (probe sortant depuis la DB échoue) → connexion + query via le driver de l'engine → **soft-enforce** (écriture appuser refusée au dépassement de quota) → destroy nettoie DB + sidecar + volumes. Réutilise les patterns SP6 (probe egress non-ambigu, assertions HostConfig, reconcile).
- **Phase 1** : les 40 tests existants = garde-fou behavior-preserving.
- **Frontend** : tests `buildSnippets` par engine (driver/format corrects) + gating engine-picker/Northwind (Vitest).
- **e2e full-stack** : créer un instance de chaque engine SQL via Caddy → connecter (sidecar) → query → dashboard → destroy.

**Prérequis test (Phase 4)** : SQL Server ~2 GB → la VM Docker Desktop du Mac doit avoir **≥ 3-4 GB alloués** (sinon OOM/timeout mssql). Démarrage mssql lent (~30-60 s) → timeouts généreux + collection séquentielle.

---

## 8. Hors périmètre (différé)

- **MongoDB** (sous-projet propre : console NoSQL, auth/rôles Mongo, soft-enforce sans transaction-read-only).
- **Seeds Northwind / templates** par dialecte (sous-projet 3).
- Réglage fin des grants/db-roles par engine au-delà du nécessaire (DML+DDL scopés à appdb).

---

## 9. Critères de succès

Un utilisateur peut créer une base **MySQL**, **MariaDB** ou **SQL Server** depuis le wizard, obtenir une **connection string fonctionnelle** (format par-engine) + des snippets corrects (5 langs), **s'y connecter de l'extérieur** (via le sidecar), **exécuter des requêtes** dans la console, et voir la base détruite à expiration. Chaque engine tourne **durci** : appuser non-superuser (exec de commande bloqué prouvé), **aucun egress**, quota appliqué (soft-enforce), isolation SP6. Les 40 tests existants restent verts (refactor sans régression) ; la suite complète (SP1+SP6+SP2) est verte ; l'e2e crée chaque engine SQL avec succès.
