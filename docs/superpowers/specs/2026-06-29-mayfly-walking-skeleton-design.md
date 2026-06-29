# MayFly — Walking Skeleton (Sous-projet 1) — Design

- **Date** : 2026-06-29
- **Statut** : Validé en brainstorming, en attente de relecture utilisateur
- **Sous-projet** : 1/6 — Walking skeleton, PostgreSQL only, bout-en-bout réel

---

## 1. Contexte & objectif global

MayFly est un **service web public** permettant à un visiteur anonyme de créer une **base de données temporaire** : choix du moteur, Time-To-Live, quota de storage, données initiales ; puis de la contrôler et l'afficher. Quota de **3 bases vivantes par IP**.

Le nom « MayFly » (éphémère) reflète le cœur du produit : des bases jetables à durée de vie courte.

### Cible de déploiement
**VPS public / Internet.** De vrais inconnus créent des bases. Conséquence directe : durcissement sécurité dès le slice 1, et lecture de la vraie IP cliente via `X-Forwarded-For` derrière Caddy.

### Produit complet (les 4 axes, vision cible)
- **new** — créer la base, saisir/sélectionner les infos.
- **instance** — contrôle : récap, connection string, code de connexion (bash/python/node/go/.net), permissions, stats (queries / storage / connections / IO throughput), schema, activity log.
- **console** — tables/requêtes, snippets, shortcuts, avec onglets results / messages / plan / history.
- **all** — rappel : DB alive (quota), queries today, storage used, prochaine base qui expire, + rappel des instances.

### Moteurs cibles
PostgreSQL, MySQL, MariaDB, MongoDB, SQL Server.

### Paramètres cibles
- **TTL** : 3h / 6h / 12h
- **Storage** : 256 MB / 512 MB / 1 GB / 2 GB
- **Initial data** : Blank / E-commerce / Blog / IoT Timeseries / Northwind / Import dump

---

## 2. Décomposition en sous-projets

« Full réel » est l'objectif ; il est ordonné en specs/plans distincts. Chaque ligne = un spec → plan → implémentation.

| # | Sous-projet | Contenu |
|---|---|---|
| **1** | **Walking skeleton (PostgreSQL, bout-en-bout réel)** — *ce document* | Squelette Vue + API ASP.NET ; 4 axes *fins* ; vrai provisioning Docker d'un seul engine ; connection string réelle + ports exposés ; console qui exécute de vraies requêtes ; TTL + destruction auto + quota 3/IP. |
| 2 | Les 4 autres engines | MySQL, MariaDB, MongoDB, SQL Server : images, drivers .NET, connection strings, spécificités console. |
| 3 | Initial data | Templates E-commerce / Blog / IoT Timeseries / Northwind + Import dump, par engine. |
| 4 | Axe instance — features riches | Permissions (Claude Design), stats live (queries / storage / connections / IO throughput), schema explorer, activity log, snippets connexion complets. |
| 5 | Axe console — features riches | Snippets, shortcuts, onglets results / messages / plan (EXPLAIN) / history persistée. |
| 6 | Durcissement sécurité & quotas | Isolation conteneurs avancée, enforcement quota disque robuste, anti-abus IP, rate-limiting fin. |

---

## 3. Périmètre du slice 1

Tout est réel, PostgreSQL uniquement. Les axes existent mais en version *fine*.

| Axe | Inclus (fin) | Reporté (sous-projets 2-6) |
|---|---|---|
| **new** | wizard : engine (Postgres seul, autres grisés), TTL 3/6/12h, storage 256MB→2GB, initial data (Blank + Northwind), bouton créer | E-commerce / Blog / IoT / Import dump ; 4 autres engines |
| **instance** | récap, **connection string réelle**, snippets connexion (bash / python / node / go / .net), bouton détruire | permissions, schema explorer, activity log riche |
| **console** | éditeur SQL, exécution réelle, onglet **results** + **messages** | snippets, shortcuts, plan (EXPLAIN), history persistée |
| **all** | quota DB alive, queries today, storage used, prochaine expiration, liste des instances (via cookie session) | — |
| **stats** | storage used + queries (monitor) | connections, IO throughput live |

---

## 4. Décisions d'architecture (verrouillées)

1. **Provisioning** : vrai, basé sur **Docker** (un conteneur Postgres par instance).
2. **Topologie (Approche B)** : un service interne **`MayFly.Provisioner`** tient le socket Docker et expose une API privée *étroite*. L'`MayFly.Api` exposée à Internet ne touche **jamais** Docker. Frontière de privilège posée dès le début.
3. **Contrôle d'accès** : **URL-capabilité** secrète (`/instance/{token}`) pour contrôler/partager une base ; **cookie de session anonyme** pour lister « tes » bases dans l'axe *all* ; **IP** uniquement pour le quota de création (3/IP). Pas de système de comptes.
4. **Storage** : **hard cap** réel (quota projet XFS) **+ monitor** de taille pour les stats d'affichage.
5. **Choix délégués justifiés** : Docker.DotNet (SDK), PostgreSQL + EF Core (base de métadonnées de l'app), réseau Docker interne pour l'exécution des requêtes, `BackgroundService` pour le TTL.

---

## 5. Composants & frontières

```
                    Internet (utilisateurs anonymes)
                              │
                          [ Caddy ]  reverse-proxy + TLS, lit X-Forwarded-For
                          /        \
              static SPA /          \ /api/*
                        /            \
         ┌─────────────┐            ┌──────────────────┐
         │ MayFly.Web  │            │   MayFly.Api     │  exposée Internet,
         │ (Vue 3 SPA) │── HTTP ───▶│  (ASP.NET Core)  │  ZÉRO accès Docker
         └─────────────┘            └──────────────────┘
                                       │            │
                          EF Core ▼    │            │ HTTP privé (réseau interne)
                          ┌──────────────┐          ▼
                          │ Metadata DB  │   ┌─────────────────────┐
                          │ (PostgreSQL) │   │ MayFly.Provisioner  │ tient le
                          └──────────────┘   │  (ASP.NET Core)     │ socket Docker
                                             └─────────────────────┘
                                                      │ Docker.DotNet
                                                      ▼
                                          ┌────────────────────────┐
                                          │  Docker Engine (hôte)  │
                                          │  réseau interne:       │
                                          │  [pg-inst-a][pg-inst-b]│ conteneurs users,
                                          │   :5432→port public     │ ports DB firewallés
                                          └────────────────────────┘
```

| Unité | Responsabilité | Dépend de | Exposition |
|---|---|---|---|
| **MayFly.Web** | SPA Vue 3, aucune logique métier, appelle l'API | MayFly.Api | Statique via Caddy |
| **MayFly.Api** | Validation, quota IP, capability tokens, persistance métadonnées, **exécution des requêtes users** (réseau interne), appels Provisioner pour le cycle de vie | Metadata DB, Provisioner, conteneurs users (réseau interne) | Internet (`/api/*`) |
| **MayFly.Provisioner** | Seul composant privilégié : create / destroy / inspect des conteneurs ; valide/whiteliste ses entrées | Socket Docker | **Interne uniquement** |
| **Metadata DB (PostgreSQL)** | État de l'app (instances, quotas, sessions, query log). Séparée des bases users | — | Interne |
| **Conteneurs users** | Un Postgres par instance, réseau interne, port DB mappé sur un port public firewallé | — | Port DB firewallé |

**Contrat étroit du Provisioner :**
- `Create(engine, ttlHours, storageMB, initData) → { containerId, internalHost, publicPort, dbName, dbUser, dbPassword }`
- `Destroy(instanceId)` — conteneur + volume + libération du port ; idempotent
- `Inspect(instanceId) → { state, sizeBytes }`

---

## 6. Flux de données

### Créer une base
1. Web → `POST /api/instances { engine, ttl, storageMB, initData }`
2. Api lit l'IP (`X-Forwarded-For` de confiance) → vérifie quota : instances non-terminales de cette IP `< 3`, sinon `429`. Vérif en transaction (anti-course).
3. Api appelle `Provisioner.Create(...)`.
4. Provisioner : crée volume taille-fixe (quota XFS) → crée conteneur Postgres durci → alloue un port public dans la plage → applique l'init data → renvoie les infos.
5. Api persiste l'`Instance` (`expiresAt`, `capabilityToken`, `sessionId` cookie, `creatorIp`) → renvoie `{ instanceToken, connectionString }`.

### Exécuter une requête (console)
1. Web → `POST /api/instances/{token}/query { sql }` (token dans l'URL).
2. Api valide le token (comparaison temps constant).
3. Api se connecte au conteneur **via le réseau Docker interne** (`internalHost`, pas le port public) avec le compte admin de l'instance.
4. Exécution avec `CommandTimeout` court + **cap de lignes** + limite de payload.
5. Renvoie `results` (colonnes + lignes) / `messages` ; enregistre un `QueryLog`.

> Point clé : l'exécution passe par le **réseau interne** ; le port public ne sert qu'aux clients externes de l'utilisateur (psql, drivers…).

---

## 7. Modèle de données (EF Core / PostgreSQL)

```
Instance
  Id              Guid (PK)
  CapabilityToken string  (≥128 bits CSPRNG, indexé) — URL /instance/{token}
  SessionId       string  (cookie session anonyme) — axe "all"
  CreatorIp       string  (X-Forwarded-For) — quota 3/IP
  Engine          enum    (Postgres) — extensible
  TtlHours        int     (3 | 6 | 12)
  StorageQuotaMB  int     (256 | 512 | 1024 | 2048)
  InitialData     enum    (Blank | Northwind)
  ContainerId     string
  InternalHost    string  (DNS Docker interne — exécution requêtes)
  PublicPort      int     (port firewallé — connection string)
  DbName          string
  DbUser          string
  DbPasswordEnc   string  (chiffré au repos — ASP.NET Data Protection)
  State           enum    (Provisioning | Running | Expired | Destroyed | Failed)
  CreatedAt       DateTime (UTC)
  ExpiresAt       DateTime (UTC, absolu)
  LastSizeBytes   long     (← monitor — affichage storage used)

QueryLog
  Id              Guid (PK)
  InstanceId      Guid (FK)
  ExecutedAt      DateTime (UTC)
  DurationMs      int
  RowCount        int
  Success         bool
  ErrorMessage    string?
  (SQL non stocké en slice 1 — vie privée + volume ; alimente "queries today")
```

Pas d'entité `Session` séparée en slice 1 : le `SessionId` est une simple colonne.

---

## 8. Cycle de vie — `LifecycleService : BackgroundService`

Tourne dans MayFly.Api, intervalle ~30s.

1. **Reaper** — `ExpiresAt <= now` & non détruites → `Provisioner.Destroy` (conteneur + volume + port) → état `Destroyed`. Idempotent (rejoue si crash).
2. **Size monitor** — pour chaque `Running`, `Provisioner.Inspect` (ou `pg_database_size` via réseau interne) → met à jour `LastSizeBytes`.
3. **Reconcile** — au démarrage, resynchronise métadonnées ↔ conteneurs réels (détruit les orphelins, marque `Failed` les manquants).

---

## 9. Quotas

### Storage — hard cap + monitor
- **Hard cap** : volume par instance à **taille fixe** = quota, via **quota projet XFS** (`prjquota`) sur le data-root Docker, un project-id par instance. Au dépassement : écritures `disk full`, Postgres gère proprement.
  - Choix vs images loopback : pas de `losetup` privilégié par instance, pas de gaspillage d'espace, agnostique du moteur.
  - **Pré-requis hôte** : data-root Docker sur filesystem **XFS monté `pquota`**. À documenter dans le déploiement.
- **Monitor** : le size monitor remplit `LastSizeBytes` → barre de progression vers le quota dans les axes instance/all.

### Création — 3/IP
À `POST /api/instances`, `COUNT` des instances de `CreatorIp` en état non-terminal `< 3`, sinon `429`. Vérifié en transaction (anti-course).

---

## 10. Stack frontend & choix justifiés

Imposé : Vue.js + Tailwind. Choix délégués :

| Besoin | Choix | Justification |
|---|---|---|
| Framework | **Vue 3 `<script setup>` + TypeScript** | Composition API testable hors composant ; TS attrape les erreurs de contrat API à la compilation (backend .NET typé). |
| Build | **Vite** | Standard Vue, HMR instantané, build optimisé. |
| Routing | **Vue Router** | 4 axes = 4 routes (`/new`, `/instance/:token`, `/console/:token`, `/`) ; garde la capability token dans l'URL. |
| State | **Pinia** | Store officiel Vue 3, typé, léger. |
| Données serveur | **TanStack Query (vue-query)** | Polling stats/instances (cache, refetch, invalidation, retry) = son métier ; évite un state custom bugué. |
| Éditeur SQL | **CodeMirror 6** | ~10× plus léger que Monaco, tree-shakable, `@codemirror/lang-sql` (coloration + autocomplétion). |
| HTTP | **fetch natif + wrapper** | Suffit sous TanStack Query ; une dépendance de moins. |
| UI | **Tailwind + composants maison** | Fidèle au visuel Claude Design importé ; pas de lib de composants concurrente. |

- **Servage** : SPA statique derrière Caddy ; API .NET sous `/api/*`.
- **Stats live (slice 1)** : **polling** TanStack Query (5–10s). SignalR reporté au sous-projet 4 (YAGNI).
- **Import Claude Design** : 1ʳᵉ étape d'implémentation = importer `index.html` depuis le lien Claude Design via le MCP `claude_design`, puis le découper en composants Vue par axe. Le HTML importé pilote le visuel ; branché ensuite sur l'API réelle.

Lien Claude Design : `https://claude.ai/design/p/0ff7f810-464e-4010-95ac-5f49cecf4ae6?file=index.html`

---

## 11. Sécurité (service public)

### Durcissement des conteneurs users (non négociable)
- Limites : `--memory`, `--cpus`, `--pids-limit` par instance.
- `--cap-drop=ALL` + caps minimales, `--security-opt=no-new-privileges`, `--read-only` rootfs (data sur le volume quota), aucun bind-mount hôte.
- Réseau Docker **interne** dédié, `icc=false` → conteneurs users invisibles entre eux ; seuls Provisioner/Api les atteignent.
- Port DB sur **plage firewallée** (ex. 20000–21000) ouverte explicitement ; le reste fermé.

### Frontière de privilège
Seul **MayFly.Provisioner** (interne, non exposé) tient le socket Docker. `MayFly.Api` exposée n'a aucune capacité Docker. Le Provisioner whiteliste ses entrées (engine ∈ liste, storage ∈ {256…2048}, ttl ∈ {3,6,12}).

### API exposée
- **Capability tokens** : ≥128 bits CSPRNG, comparaison temps constant, jamais loggés.
- **Quota IP** : vraie IP via `X-Forwarded-For` de confiance — Caddy uniquement, `KnownProxies`/`KnownNetworks` configurés (sinon spoofable).
- **Rate-limiting** : `RateLimiter` ASP.NET sur création + exécution requêtes, par IP.
- **Exécution requêtes** : `CommandTimeout` court, cap de lignes retournées, taille de payload limitée. Le sandbox est le conteneur lui-même. Erreurs propres, jamais de stacktrace .NET au client.
- **Secrets** : mots de passe DB chiffrés au repos (Data Protection) ; connection string montrée à l'utilisateur seulement.

---

## 12. Tests

- **Unitaires (xUnit)** : calcul quota IP (course/limite), génération+validation token, calcul `ExpiresAt`/reaper, validation des entrées Provisioner.
- **Intégration** : cycle create→inspect→destroy contre un vrai Docker (Testcontainers), enforcement du quota XFS, exécution requête (timeout / cap de lignes).
- **Frontend (Vitest)** : composants wizard (validation) et console (rendu results/messages).

---

## 13. Pré-requis hôte / déploiement

- VPS Linux avec Docker, **data-root Docker sur XFS monté `pquota`** (pour le hard cap storage).
- Caddy en reverse-proxy + TLS, configuré comme proxy de confiance pour `X-Forwarded-For`.
- Firewall ouvrant la plage de ports DB (ex. 20000–21000) + 80/443.
- Réseaux Docker : un réseau interne pour app↔conteneurs users (`icc=false`), Provisioner non exposé.
- Compose : `caddy`, `mayfly-web` (statique), `mayfly-api`, `mayfly-provisioner`, `metadata-db`.

---

## 14. Hors périmètre du slice 1

- 4 autres engines (MySQL, MariaDB, MongoDB, SQL Server).
- Initial data autres que Blank/Northwind ; import dump.
- Permissions, schema explorer, activity log riche, stats live (connections/IO), SignalR.
- Snippets/shortcuts console, onglets plan (EXPLAIN) / history persistée.
- Comptes utilisateurs.

---

## 15. Risques ouverts

- **Pré-requis XFS `pquota`** : si l'hôte ne le supporte pas, repli sur images loopback (à acter au déploiement) — mécanisme alternatif déjà identifié.
- **Confiance `X-Forwarded-For`** : mal configuré = quota IP spoofable. La config `KnownProxies` est un point de vérification critique.
- **Modèle capability-token** : une fuite de lien expose la base (atténué par TTL court + données jetables) — assumé.

---

## 16. Critères de succès (slice 1)

Un visiteur peut, depuis l'UI réelle : créer une base PostgreSQL (TTL + storage + Blank/Northwind), obtenir une **connection string fonctionnelle** + des snippets, **s'y connecter réellement** depuis l'extérieur, **exécuter de vraies requêtes** dans la console (results + messages), voir ses bases et son quota dans *all*, et constater que la base est **détruite automatiquement** à expiration. Le quota 3/IP et le hard cap storage sont effectifs.
