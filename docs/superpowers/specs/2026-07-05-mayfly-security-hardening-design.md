# MayFly — Sous-projet 6 : Durcissement sécurité & quotas — Design

- **Date** : 2026-07-05
- **Statut** : Validé en brainstorming, en attente de relecture utilisateur
- **Sous-projet** : 6/6 — Durcissement sécurité (passe complète A+B+C)
- **Pré-requis** : slice-1 (walking skeleton) mergé sur `master`

---

## 1. Contexte & objectif

MayFly est un service **public** où des inconnus créent des bases PostgreSQL éphémères. Le slice-1 est fonctionnel et mergé, mais la review finale (opus) a désigné plusieurs risques comme **prérequis avant exposition publique réelle**. Ce sous-projet durcit la sécurité de bout en bout : isolation réseau (egress), privilèges DB, isolation conteneur, enforcement réel du quota disque, robustesse du cycle de vie, et défense en profondeur.

### Cible & validation
- Déploiement : VPS public Linux, un hôte Docker (inchangé).
- Validation : **VM Linux de Docker Desktop** (macOS) — egress, réseaux internes, read-only rootfs, non-superuser, quota soft-enforce, reconcile sont **testables via l'API Docker** dans la boucle dev/CI. Seul le **hard-cap XFS pquota** reste incertain dans la VM → conservé derrière l'abstraction dev/prod et validé sur le VPS au déploiement.

### Périmètre : passe complète (A + B + C), en 4 phases ordonnées
| Phase | Tier | Contenu |
|---|---|---|
| **1** | B (robustesse) | ILogger dans le Provisioner, idempotence `DestroyAsync` (état `Destroying`), reconcile-au-démarrage |
| **2** | A (isolation) | `appuser` non-superuser, egress (réseau `internal` + sidecar socat par instance), read-only rootfs + tmpfs |
| **3** | A (quota) | hard-cap disque réel (XFS prod corrigé + soft-enforce portable) |
| **4** | C (polish) | auth endpoints Provisioner (secret partagé), affinement rate-limit/anti-abus, `SECURITY.md` |

**Composants touchés** : `MayFly.Provisioner` (topologie réseau, sidecar, rôle DB, rootfs, quota, reconcile, auth), `MayFly.Api` (état `Destroying`, secret partagé, rate-limit), `docker-compose.yml`/`Caddyfile` (réseaux), `MayFly.Tests`.

---

## 2. Phase 1 — Robustesse (fondation)

### 2.1 ILogger dans le Provisioner
Injecter `ILogger<DockerProvisioner>` (et dans `PostgresSeeder` / volume provisioners). Tous les `catch { }` de cleanup silencieux logguent en Warning/Error. Le ctor change → les tests construisant `new DockerProvisioner(docker, ports, volumes)` passent un `NullLogger<DockerProvisioner>.Instance`.

### 2.2 Idempotence `DestroyAsync`
- **Api (`InstanceService`)** : nouvel état `InstanceState.Destroying`. Destroy = update conditionnel gardé :
  `UPDATE Instances SET State=Destroying WHERE Id=@id AND State IN (Running, Provisioning)`. Si 0 ligne affectée → un autre destroy est en cours ou déjà terminé → retour sans agir. Sinon appel Provisioner puis `State=Destroyed`. Idem pour le reaper (qui passe par le même chemin gardé). Tue le double-destroy concurrent et garde le quota cohérent.
- **Provisioner (`DockerProvisioner.DestroyAsync`)** : force-remove conteneur DB **+ sidecar** + volume, ignore-si-absent (idempotent), libère le port.
- Les états « actifs » comptant vers le quota 3/IP incluent `Provisioning`, `Running` (PAS `Destroying`/`Destroyed`).

### 2.3 Reconcile-au-démarrage (`LifecycleService`)
Au boot, avant la boucle : resynchronise métadonnées ↔ Docker réel.
- Détruit les orphelins Docker (`mayfly-pg-*`, `mayfly-sidecar-*`, `mayfly-vol-*`) sans ligne métadonnée en état actif (ou marquée `Destroyed`/`Failed`).
- Marque `Failed` les lignes `Running`/`Provisioning` dont le conteneur DB a disparu ; libère leurs ports.
- Idempotent et sûr à rejouer.

---

## 3. Phase 2 — Isolation (cœur go-live)

### 3.1 Rôle DB non-superuser
L'image postgres crée `POSTGRES_USER` en **superuser** — vecteur d'exécution de commande (`COPY … FROM PROGRAM`), lecture de fichiers serveur, etc.
- L'image crée un superuser interne **`mayflyadmin`** (`POSTGRES_USER=mayflyadmin`), utilisé **uniquement** par le Provisioner (seeding) et jamais exposé à l'utilisateur.
- À l'init (après readiness), création d'un rôle **`appuser`** avec `LOGIN NOSUPERUSER`, mot de passe, `GRANT` scopés à `appdb` : `CONNECT`, usage/`CREATE` sur le schéma `public`, DML complet + DDL (CREATE/DROP TABLE/INDEX/VIEW) sur ses objets. **Sans** superuser, sans `pg_read_server_files`, sans droit d'exécuter des programmes serveur.
- La **connection string** remise à l'utilisateur ET l'exécution console (`QueryExecutor`) utilisent `appuser`. Le seeding utilise `mayflyadmin`.
- **Métadonnées** : l'entité `Instance` gagne un `AdminPasswordEnc` (chiffré) en plus de `DbPasswordEnc` (appuser). Le seeding l'utilise au create (dans le Provisioner) ; le **soft-enforce** (§4.2, plus tard, côté Api) en a besoin pour se connecter en `mayflyadmin` et exécuter l'`ALTER ROLE`. `mayflyadmin` n'apparaît jamais dans une connection string exposée.
- `CREATE EXTENSION` est superuser-only → les extensions courantes (pg_trgm, uuid-ossp) sont pré-installées par `mayflyadmin` à l'init ; une extension arbitraire par `appuser` est refusée (documenté ; élargissement = travail ultérieur).

### 3.2 Egress bloqué : réseau `internal` + sidecar socat
- Réseau Docker **`mayfly-users`** créé avec **`internal: true`** (pas de gateway → aucune sortie Internet au niveau réseau). Les conteneurs DB user y sont **seuls** (aucun port publié sur la DB).
- L'**Api** est rattachée à `mayfly-users` (multi-réseaux) → atteint chaque DB par nom (`pg-inst-x:5432`) pour l'exécution des requêtes (chemin direct, inchangé).
- **Sidecar par instance** : un conteneur `alpine/socat` (ou équivalent) sur `mayfly-users` **et** un bridge normal **`mayfly-ingress`**, publiant `publicPort:5432` sur l'hôte. Commande : `socat TCP-LISTEN:5432,fork,reuseaddr TCP:pg-inst-x:5432`. (Un réseau `internal` rejette le trafic host-publié → d'où le double réseau du sidecar. Le sidecar n'exécute aucun code user → son egress est sans risque.)
- Isolation DB-à-DB : **réseau partagé + non-superuser comme contrôle primaire** (option a) — le lateral DB-à-DB exige un shell/exec que le rôle non-superuser empêche. (Réseau par-instance = défense en profondeur différée.)
- Le **Provisioner** reste hors de `mayfly-users`.

### 3.3 Read-only rootfs + tmpfs
Conteneur DB : `HostConfig.ReadonlyRootfs = true`, avec tmpfs sur les chemins où postgres écrit hors du volume data : `/tmp`, `/var/run/postgresql`, `/run` (taille bornée). Le volume data (`/var/lib/postgresql/data`) reste inscriptible (c'est le volume quota). Les caps d'init (CHOWN/SETUID/SETGID/FOWNER/DAC_OVERRIDE) restent nécessaires à l'entrypoint. Empêche l'écriture de binaires dans le conteneur.

### 3.4 Contrat Provisioner
`CreateInstanceResult` inchangé côté Api (`InternalHost` = nom du conteneur DB pour le chemin Api ; `PublicPort` = port publié par le sidecar). Le sidecar est un détail interne du Provisioner (créé/détruit avec la DB). `DestroyAsync` reçoit déjà containerId/volume/port ; il déduit/retrouve le sidecar par label (`mayfly.instance`).

---

## 4. Phase 3 — Hard-cap disque réel

### 4.1 Correction du hard-cap prod (XFS)
`XfsVolumeProvisioner` : remplacer `DriverOpts { type=xfs, o=pquota, size }` (incorrect) par **`DriverOpts { size: "<mb>m" }`** seul sur le driver `local`. Cela exploite le quota projet XFS **si** le data-root Docker est sur un FS **XFS monté `pquota`** → **pré-requis hôte documenté** (activable sur le VPS). Au dépassement : écritures `disk full`, Postgres gère.

### 4.2 Soft-enforce portable (couche universelle)
Le size monitor du `LifecycleService` (déjà via `pg_database_size`) devient **actif** : quand `LastSizeBytes >= StorageQuotaMb`, il bascule `appuser` en lecture seule via `ALTER ROLE appuser SET default_transaction_read_only = on` (exécuté en tant que `mayflyadmin`) et logue l'événement. Fonctionne partout (y compris dev/Docker Desktop sans XFS). Overshoot borné (s'applique aux nouvelles sessions ; monitor ~30s ; DB jetables).
- Sélection dev/prod du volume conservée via `IVolumeProvisioner` (size-opt prod / plain dev) ; la couche soft-enforce s'applique dans les deux.

---

## 5. Phase 4 — Polish (défense en profondeur)

### 5.1 Auth des endpoints Provisioner (secret partagé)
Header `X-Provisioner-Key` : middleware Provisioner rejetant (401) toute requête sans le secret ; l'`Api` (`ProvisionerClient`) l'ajoute. Secret via variable d'env, injecté dans les deux services (compose). Empêche un provisioning arbitraire si le réseau interne est compromis.

### 5.2 Affinement rate-limit / anti-abus IP
En plus de la politique générale par IP, deux politiques par IP dédiées :
- **création** (fenêtre stricte, p.ex. quelques/min) — borne le churn de provisioning ;
- **exécution de requêtes** (borne QPS) — empêche l'usage du service comme compute gratuit.
Appliquées via `RateLimiter` ASP.NET (policies nommées sur les endpoints concernés).

### 5.3 `SECURITY.md`
Document racine : posture de sécurité (frontière de privilège Provisioner, non-superuser, egress bloqué, read-only rootfs, quota hard+soft, rate-limit, capability tokens, secret partagé), pré-requis hôte (XFS pquota, KnownProxies Caddy), et risques résiduels assumés (fuite de capability-URL, isolation DB-à-DB reposant sur non-superuser, extensions limitées).

---

## 6. Tests (via API Docker dans la VM Docker Desktop + xUnit)

- **egress** : après create, un probe réseau depuis le conteneur DB (`docker exec … wget -T2 http://example.com` ou équivalent) **échoue/timeout** → assertion « pas d'egress ». Le chemin externe (client→sidecar→DB) et le chemin Api (direct) restent fonctionnels (SELECT 1).
- **non-superuser** : assertion que `appuser` n'est pas superuser (`rolsuper=false`) et qu'une opération superuser (`COPY … FROM PROGRAM`) est refusée ; le seeding par `mayflyadmin` fonctionne toujours (Northwind).
- **read-only rootfs** : assertion `HostConfig.ReadonlyRootfs==true` + une écriture dans `/etc` échoue ; postgres démarre et sert malgré le rootfs read-only.
- **quota soft-enforce** : instance à petit quota, remplissage jusqu'au dépassement, un tick de monitor, puis une écriture `appuser` est **refusée** (read-only).
- **reconcile** : créer un orphelin Docker manuel (conteneur/volume labellisé sans métadonnée) → après `RunReconcileAsync`, il est supprimé ; une ligne `Running` sans conteneur → `Failed`.
- **idempotence** : deux `DestroyAsync` concurrents/successifs → une seule transition vers `Destroyed`, pas d'exception, quota décrémenté une fois.
- **Provisioner auth** : requête sans `X-Provisioner-Key` → 401 ; avec la clé → OK.
- **XFS hard-cap** : validé manuellement sur le VPS (data-root XFS+pquota) au déploiement — hors boucle CI.

Les tests Docker rejoignent la collection xUnit séquentielle existante (`docker-sequential`) car ils lient des ports/réseaux réels.

---

## 7. Hors périmètre (différé)

- Réseau `internal` **par-instance** (isolation DB-à-DB au niveau réseau) — l'option (a) partagée + non-superuser est retenue ; le per-instance reste un durcissement ultérieur.
- `CREATE EXTENSION` arbitraire par `appuser` (au-delà des extensions pré-installées).
- Signatures/authenticité d'image, scanning de vulnérabilités, secrets manager externe.
- Autres engines (sous-projet 2) — ce durcissement cible PostgreSQL ; les mécanismes (non-superuser, egress, rootfs, quota) seront réappliqués par engine quand ils arriveront.

---

## 8. Critères de succès

Sur la VM Docker Desktop, tous les tests ci-dessus passent : une DB user n'a **aucune sortie Internet**, tourne sous un rôle **non-superuser** (COPY PROGRAM refusé), avec **rootfs read-only**, reste joignable de l'extérieur (via sidecar) et par l'Api (direct), voit ses **écritures refusées au dépassement de quota**, et le cycle de vie est **idempotent + auto-réconcilié**. Les endpoints Provisioner exigent le **secret partagé**. Le hard-cap XFS est prêt à être validé sur le VPS. `SECURITY.md` documente la posture. La suite complète (slice-1 + durcissement) reste verte.
