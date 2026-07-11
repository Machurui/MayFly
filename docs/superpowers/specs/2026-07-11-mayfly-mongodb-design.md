# MayFly — Sous-projet MongoDB — Design

- **Date** : 2026-07-11
- **Statut** : Validé en brainstorming, en attente de relecture utilisateur
- **Sous-projet** : MongoDB (le 5ᵉ et dernier moteur du périmètre initial ; NoSQL, déféré depuis le sous-projet 2)
- **Pré-requis** : slice-1 + sous-projet 6 (durcissement) + sous-projet 2 (moteurs SQL) mergés sur `master`

---

## 1. Contexte & objectif

MayFly provisionne PostgreSQL, MySQL, MariaDB et SQL Server (SP2) derrière l'abstraction `IEngineProvider`/`IEngineClient`. **MongoDB était déféré** parce qu'il casse le modèle SQL : pas d'ADO.NET, pas de SQL, résultats = **documents** (JSON imbriqué) et non des lignes, auth/rôles SCRAM, pas de `transaction read-only`/`ALTER DATABASE` pour le soft-enforce. Ce sous-projet ajoute MongoDB avec une console **mongosh complète**, sans polluer l'abstraction SQL.

### Décisions verrouillées (brainstorming)
- **Moteur** : MongoDB `mongo:7.0`, port 27017, id canonique **`mongo`** (réconcilier `mongodb`→`mongo` dans le frontend, comme le fix `sqlserver`→`mssql` de SP2). Données initiales : **Blank seul**.
- **Console** : **mongosh complète** — exécute du JS mongosh arbitraire (variables, boucles, chaining, aggregate).
- **Exécution** : `docker exec mongosh --eval` **dans le conteneur éphémère de l'utilisateur** (déjà durci). Jamais d'eval JS sur l'API (ce serait une RCE).
- **Routing** : **toutes** les ops conteneur Mongo (console + taille + soft-enforce) passent par un **nouvel endpoint exec du Provisioner**. L'API **ne parle jamais** le protocole Mongo → **aucune dépendance MongoDB.Driver côté API**.

---

## 2. Architecture & périmètre

MongoDB ne rentre **pas** dans l'abstraction SQL `IEngineClient` (ADO.NET + chaînes SQL). Deux moitiés distinctes :

- **Provisioning → réutilise `IEngineProvider`** (provisioning conteneur, quasi engine-agnostique). `MongoEngineProvider` fournit image/env/init-script/readiness/hardening/DataDirectory/Port comme les autres moteurs. Tout le durcissement SP6 est réappliqué (réseau `internal` + sidecar socat sur 27017, rootfs read-only + tmpfs, user non-root, cap-drop, no-new-privileges, limites, egress bloqué).
- **Query / taille / soft-enforce → chemin Mongo dédié, PAS `IEngineClient`.** Tout passe par un nouvel endpoint exec du Provisioner (qui détient le socket Docker) faisant `docker exec mongosh --eval` dans le conteneur de l'utilisateur :
  - **Console** : mongosh en tant qu'**appuser** (JS non-fiable confiné à la DB jetable — non-privilégié sur appdb, egress bloqué, rootfs RO, cpu/pids limités).
  - **Taille** (dbStats) + **soft-enforce** (révoquer `readWrite`→`read`) en tant qu'**admin**.

Ça garde l'abstraction SQL propre (Mongo n'y touche pas) et concentre toute interaction conteneur Mongo derrière le socket Docker du Provisioner.

---

## 3. Provisioning & durcissement (`MongoEngineProvider : IEngineProvider`)

| Champ | Valeur |
|---|---|
| Image | `mongo:7.0` |
| Port | 27017 |
| UsesInitVolume | `true` |
| DataDirectory | `/data/db` |
| BuildEnv | `MONGO_INITDB_ROOT_USERNAME=mayflyadmin`, `MONGO_INITDB_ROOT_PASSWORD=<hex>`, `MONGO_INITDB_DATABASE=appdb` (crée l'admin root + active l'auth) |
| GenerateCredentials | admin `mayflyadmin`, app `appuser`, db `appdb`, mots de passe hex (SCRAM, pas de politique de complexité) |

**BuildSetup** → init-script **`00-roles.js`** (le mécanisme tar/init-volume existant est agnostique au contenu → `.js` fonctionne ; l'entrypoint mongo exécute `/docker-entrypoint-initdb.d/*.js` au premier init). Crée l'appuser **non-privilégié** :
```js
db.getSiblingDB("appdb").createUser({
  user: "appuser", pwd: "<hex>",
  roles: [{ role: "readWrite", db: "appdb" }]
});
```
→ `readWrite` sur **appdb seul** — pas de root/clusterAdmin, pas d'accès aux autres bases. Mot de passe injecté en littéral JS échappé (guard sur quotes/backslashes, même si le hex n'en contient pas). `PostReadyExec = null`.

**ReadinessExec** :
```
mongosh --quiet --host 127.0.0.1 -u mayflyadmin -p <pwd> \
  --authenticationDatabase admin --eval "db.adminCommand('ping')"
```
Sonde TCP authentifiée → le serveur temporaire d'init (socket/no-auth) ne la satisfait pas (leçon TCP-readiness du SQL).

**ApplyHardening** : `ReadonlyRootfs=true` + tmpfs (`/tmp` et `/data/configdb` que l'image déclare en VOLUME ; itération possible comme mysql pour trouver le jeu de tmpfs qui boote) ; **mémoire ~512 Mo–1 Go** (WiredTiger est plus gourmand que pg — l'implémenteur confirme le plancher qui boote) ; `CapDrop=ALL` + caps minimaux (CHOWN/SETUID/SETGID pour le chown de `/data/db` par l'entrypoint) ; `no-new-privileges` ; user non-root `mongodb` (image).

**Preuve non-privilégié** (équivalent no-FILE / non-sysadmin) : l'appuser tente une op admin (`db.adminCommand({listDatabases:1})` ou l'accès à une autre DB) → **refusé**.

`mongo:7.0` est **multi-arch (arm64 natif)** → pas d'émulation sur Apple Silicon (contrairement à mssql), tests rapides.

---

## 4. Le canal d'exécution mongosh

### 4.1 Endpoint Provisioner
`POST /instances/{containerId}/exec-mongosh` (protégé `X-Provisioner-Key`), body `{ command, credential }`. Exécute (schématiquement) :
```
docker exec <container> mongosh --quiet --host 127.0.0.1 \
  -u <user> [password fourni hors-argv] --authenticationDatabase <authdb> appdb \
  --eval "<command>"
```
et renvoie `{ output, error, exitCode, ms }` (stdout + stderr + code). `--eval` reçoit le **programme JS complet** de l'utilisateur → vraie fidélité mongosh. La `command` est passée en **argv** (pas de shell → pas d'injection shell).

**Mot de passe hors-argv** : pour éviter d'exposer le mot de passe dans la liste des process du conteneur, le préférer hors de l'argv de mongosh là où c'est praticable — variable d'environnement de l'exec (`docker exec -e MONGO_PWD=…`) lue par un mini-wrapper, ou le prompt stdin de mongosh. Si impraticable, l'argv `-p <pwd>` reste acceptable (transitoire, comme le pattern SQL `-P`/`mysqladmin -p` ; le conteneur est la DB jetable de l'utilisateur, egress bloqué). L'implémenteur retient l'option qui marche de façon fiable avec l'image `mongo:7.0` et la reporte.

**Garde-fous** :
- **Timeout serveur** sur l'exec (mongosh peut boucler → kill de l'exec ; les limites cpu/pids du conteneur bornent aussi le dégât).
- **Sortie plafonnée** (N Ko / N docs, troncature signalée dans la réponse).

### 4.2 Routing API
`POST /api/instances/{token}/query` branche sur `inst.Engine` :
- **SQL** → `QueryExecutor` (ADO.NET, inchangé).
- **Mongo** → `MongoQueryRunner` : appelle l'endpoint exec **en tant qu'appuser** avec la commande, enveloppe le résultat.

**Champ requête** : le `QueryRequestDto.Sql` actuel devient un champ neutre (ex. `query`) — SQL pour les moteurs SQL, JS mongosh pour Mongo. **Forme réponse** : SQL reste tabulaire (`columns`/`rows`) ; le DTO résultat gagne un champ optionnel **`output`** (texte/JSON) pour les moteurs non-tabulaires — pour Mongo, `output` = sortie mongosh, `columns`/`rows` vides.

**Credentials** : l'API détient les mots de passe chiffrés ; elle déchiffre et transmet celui qui convient (appuser pour la console, admin pour taille/enforce) au Provisioner. Le rate-limit par-IP `query` (SP6) s'applique toujours à l'API.

### 4.3 Sécurité
Le JS non-fiable tourne en **appuser** dans le conteneur **jetable et durci de l'utilisateur** (non-priv sur appdb, zéro egress, rootfs RO, cpu/pids bornés) — strictement plus sûr qu'un eval côté API. L'endpoint exec exige `X-Provisioner-Key`. Command en argv (pas d'injection shell) ; timeout + cap sortie + rate-limit bornent l'abus.

---

## 5. Taille & soft-enforce (via le canal exec, en admin)

Le size-monitor (`LifecycleService`) et le `QuotaEnforcer` gagnent une **branche Mongo** symétrique du routing query : `engine==mongo` → exec Provisioner **en admin** ; sinon → chemin SQL API-side existant (inchangé).

- **Taille** : au lieu de `SizeQuerySql`, l'admin exécute `db.getSiblingDB("appdb").stats()` et parse l'empreinte on-disk (**storageSize + indexSize** ; l'implémenteur confirme storageSize vs dataSize — storageSize = compressé on-disk, le plus proche de l'usage du volume quota).
- **Soft-enforce** (au dépassement) : l'admin bascule l'appuser en lecture seule :
  ```js
  db.getSiblingDB("appdb").updateUser("appuser", { roles: [{ role: "read", db: "appdb" }] });
  ```
  Effectif sur les **nouvelles connexions** — et comme chaque requête console ouvre un mongosh frais (nouvelle connexion par exec), une écriture après enforce est **refusée** (même sémantique d'overshoot que le read-only par-session du SQL).

L'admin (`mayflyadmin`, déjà persisté via `Instance.AdminUser` de SP2) s'authentifie via `--authenticationDatabase admin`.

---

## 6. Frontend

- **EnginePicker** : activer Mongo — réconcilier l'id `mongodb`→`mongo` (canonique), `enabled:true`. Les 5 moteurs deviennent sélectionnables.
- **engineLabels** : ajouter `mongo: 'MongoDB'` (glyph/couleur déjà dans le design).
- **InitialDataPicker** : Mongo = **blank seul** (le gating northwind→postgres existant suffit).
- **Console (`ConsoleView`)** — branche sur l'engine :
  - SQL → CodeMirror `lang-sql` + `QueryResults` **tabulaire** (inchangé).
  - Mongo → **éditeur JavaScript** (`@codemirror/lang-javascript`), doc par défaut mongosh-ish (`db.getCollection("items").find()`), et **vue de sortie document/JSON** (le champ `output`) au lieu de la grille — bloc monospace formaté (mongosh formate déjà joliment ; pretty-print EJSON optionnel).
- **Connection string & snippets** (extension du `buildSnippets` engine-aware de SP2-T10) :
  - Affichage : `mongodb://appuser:pass@host:27017/appdb` (URI, parsé comme `mysql://`).
  - 5 langs : bash **mongosh**, python **pymongo**, node **mongodb**, go **go.mongodb.org/mongo-driver**, dotnet **MongoDB.Driver**.
- **Vues instance/dashboard** : label + glyph déjà génériques (engineLabels/glyph map) → Mongo automatique ; port 27017 depuis le DTO.
- **Validateurs** : ajouter `mongo` à l'ensemble des moteurs dans **`ApiSpecValidator` ET `InstanceSpecValidator`** (les DEUX — leçon des deux validateurs de SP2-T11).

---

## 7. Phasage & tests

Via l'API Docker dans la VM Docker Desktop, collection `docker-sequential`. `mongo:7.0` est arm64-natif → tests rapides.

| Phase | Contenu | Test |
|---|---|---|
| **1 — Provisioning** | `MongoEngineProvider` + durcissement + init-role-js + readiness ; register + `mongo` dans les 2 validateurs | provision mongo → appuser non-privilégié (readWrite sur appdb seul ; `db.adminCommand({listDatabases:1})` refusé) → egress bloqué → destroy. Le projet de test se connecte via **MongoDB.Driver** pour prouver. |
| **2 — Canal exec** | endpoint Provisioner `POST /instances/{id}/exec-mongosh` (X-Provisioner-Key, docker-exec mongosh, timeout, cap sortie, cred via env) + routing API (`MongoQueryRunner`) + champ requête neutre + champ `output` | insert+find via `/query` → output ok ; commande invalide → error ; **runaway `while(true){}` tué par le timeout** ; sortie volumineuse tronquée. |
| **3 — Taille + soft-enforce** | branches Mongo (dbStats / updateUser→read via exec admin) | remplir au-delà du quota → tick → écriture appuser **refusée** (read-only). |
| **4 — Frontend** | enable + id reconciliation ; console éditeur-JS + sortie-JSON ; snippets mongo ; validateurs | Vitest : console rend éditeur JS + sortie JSON pour mongo ; snippets mongo ; picker mongo activé ; validateurs acceptent mongo. |
| **5 — e2e** | cas mongo dans `e2e-fullstack.sh` (create → requête mongosh via `/query` → output → destroy + egress) ; `SECURITY.md` mis à jour | e2e full-stack mongo via Caddy. |

---

## 8. Hors périmètre (différé)

- Rendu de documents riche/interactif (arbre pliable, édition inline) — la sortie mongosh texte/JSON formatée suffit pour ce sous-projet.
- Réplica-set / sharding / change streams — mongod standalone durci.
- Seeds/templates Mongo (équivalent Northwind) — cohérent avec « blank seul ».
- Autocomplétion mongosh / IntelliSense dans l'éditeur console.

---

## 9. Critères de succès

Un utilisateur peut créer une base **MongoDB** depuis le wizard, obtenir une **connection string `mongodb://` fonctionnelle** + des snippets corrects (mongosh/pymongo/node/go/.NET), s'y connecter de l'extérieur (via le sidecar), et **exécuter du JS mongosh complet dans la console web** (find/aggregate/insert/update, variables, boucles) avec un **rendu document/JSON**. La base tourne **durcie** : appuser non-privilégié (op admin refusée prouvée), **aucun egress**, quota appliqué (soft-enforce `readWrite`→`read` au dépassement, écriture refusée), isolation SP6. Le JS non-fiable est **confiné au conteneur jetable de l'utilisateur** (jamais d'eval sur l'API) ; l'exec est borné par timeout + cap sortie. L'abstraction SQL reste intacte (Mongo a son propre chemin). La suite complète (slice-1 + SP6 + SP2 + Mongo) reste verte ; l'e2e crée un instance Mongo avec succès.
