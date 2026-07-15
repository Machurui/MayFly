# MayFly — Sous-projet : Import dump — Design

- **Date** : 2026-07-15
- **Statut** : Validé en brainstorming, en attente de relecture utilisateur
- **Sous-projet** : Import dump (upload d'un dump utilisateur + restore) — déféré depuis SP3
- **Pré-requis** : slice-1 + SP6 (durcissement) + SP2 (moteurs SQL) + MongoDB + SP3 (templates) mergés sur `master`

---

## 1. Contexte & objectif

Le wizard MayFly affiche une option **« Import dump »** (« restore from your .sql file ») désactivée. Ce sous-projet la rend réelle : un utilisateur uploade un fichier dump et sa base est **restaurée** à partir de ce fichier, sur **les 5 moteurs**. C'est un mécanisme distinct des seeds statiques de SP3 (fichier fourni au runtime, non-fiable, taille variable) — d'où son propre sous-projet.

### Décisions verrouillées (brainstorming)
- **5 moteurs** : SQL via `.sql` (postgres/mysql/mariadb/mssql) ; Mongo via `.js`.
- **Flux** : create JSON inchangé ; **endpoint import dédié post-create** (`POST /api/instances/{token}/import`, multipart) ; le wizard orchestre create→import.
- **Application** : le dump non-fiable est rejoué **dans le conteneur jetable durci de l'utilisateur** via le **client natif du moteur** (`psql`/`mysql`/`sqlcmd`/`mongosh`), porté par un **canal exec-dump du Provisioner**. L'API ne parse jamais le SQL.
- **Rôle : admin** (fidélité complète d'un pg_dump/mysqldump), confiné au conteneur.

---

## 2. Architecture

- **Flux** : `POST /api/instances/{token}/import` applique le dump sur une instance existante. Le wizard, quand « Import dump » est choisi, crée l'instance avec `initialData="blank"` puis uploade le fichier — une seule étape UX avec progression + erreurs. `dump` reste un **marqueur frontend** ; le create backend reçoit `"blank"` (les validateurs n'ont pas besoin de `"dump"`).
- **Application dans le conteneur** : le dump est rejoué via le client natif du moteur, lancé **en admin** à l'intérieur du conteneur (comme les seeds), par un nouveau canal exec-dump du Provisioner (qui détient le socket Docker). L'API transmet le contenu au Provisioner avec `X-Provisioner-Key` ; elle ne parse jamais le dump.
- **Confinement** : exécution non-fiable **bornée** au conteneur jetable, egress-bloqué, rootfs durci, cpu/pids-limité, TTL-borné — même modèle que la console mongosh. Cap de taille, timeout, sortie plafonnée. Données restaurées bornées par le **quota volume** (soft-enforce SP6).

---

## 3. Le canal exec-dump (Provisioner)

Nouvelle méthode `Task<ExecDumpResult> ExecDumpAsync(string containerId, ExecDumpRequest req, CancellationToken ct)` sur `IDockerProvisioner`, + endpoint `POST /instances/{containerId}/exec-dump` (gardé par le middleware `X-Provisioner-Key` global).

```csharp
public record ExecDumpRequest(string Engine, string DumpContent, string AdminUser, string AdminPassword,
                              string Db, int TimeoutSeconds, int MaxOutputBytes);
public record ExecDumpResult(string Output, string Error, int ExitCode, bool Truncated, int Ms);
```

**Client natif par moteur, lancé en admin dans le conteneur** :
| Moteur | Commande (schématique) |
|---|---|
| postgres | `psql "postgresql://mayflyadmin:pwd@127.0.0.1/appdb" -f <dump>` (ON_ERROR_STOP off → restaure au max, remonte toutes les erreurs) |
| mysql / mariadb | `mysql -h127.0.0.1 -uroot -p… appdb < <dump>` |
| mssql | `/opt/mssql-tools18/bin/sqlcmd -C -S127.0.0.1 -U sa -P… -d appdb -i <dump>` |
| mongo | `mongosh -u mayflyadmin … --authenticationDatabase admin appdb --file <dump>` |

**Livraison du dump** : le contenu est écrit dans le conteneur à `/tmp/dump.<ext>` via le mécanisme `ExtractArchiveToContainer` existant (`/tmp` est tmpfs pour pg/mysql/mariadb/mongo, writable pour mssql), puis le client est exécuté dessus. **Cap de taille** : upload plafonné (défaut **16 Mo**, configurable) — sous la taille du tmpfs `/tmp` (64 Mo). *(Détail : livraison fichier-tmpfs vs pipe stdin tranché au plan si de plus gros dumps sont voulus — le pipe stdin lèverait la contrainte tmpfs.)* Le mot de passe admin est fourni hors-argv là où c'est praticable (env / URI), sinon argv (accepté, transitoire, comme le pattern SQL `-P`/mongosh).

**Garde-fous** (réutilise le plumbing exec-mongosh — `ExecCaptureAsync`, timeout container-side + CTS externe, sortie plafonnée) : renvoie `{ output, error, exitCode, truncated, ms }` (stdout+stderr du client → l'utilisateur voit les erreurs de restore). `ExecDumpAsync` ne subsume pas `ExecMongoshAsync` (console = appuser interactif ; dump = admin + fichier + client par-moteur) mais partage `ExecCaptureAsync` + la livraison tmpfs.

---

## 4. Endpoint API import + sécurité

**`POST /api/instances/{token}/import`** (multipart, le fichier dump) :
1. Résout l'instance par token — **vivante et appartenant à la session** (même contrôle d'ownership que l'endpoint query).
2. Lit le fichier avec `MultipartBodyLengthLimit`/`RequestSizeLimit` = le cap (~16 Mo) → **413** si dépassé.
3. Déchiffre le credential **admin** (`inst.AdminUser` / `secrets.Unprotect(inst.AdminPasswordEnc)`).
4. Appelle `IProvisionerClient.ExecDumpAsync(inst.ContainerId, new ExecDumpRequest(inst.Engine, dumpContent, adminUser, adminPassword, inst.DbName, timeout, maxOutput), ct)`.
5. Renvoie `{ success: exitCode==0, output, error, truncated, ms }`.

Un service scopé **`IDumpImporter`** (parallèle à `MongoOps`, résolution DI identique) porte les étapes 3-4 (testable). `IProvisionerClient` (+ `ProvisionerClient`) gagne `ExecDumpAsync` ; les records `ExecDumpRequest`/`ExecDumpResult` sont mirrorés côté Api (`ProvisionerDtos.cs`) et Provisioner (`Contracts`).

**Sécurité** :
- Dump non-fiable exécuté en admin **uniquement dans le conteneur jetable, durci, egress-bloqué, TTL-borné** — jamais sur l'API (qui ne parse pas le SQL ; aucune connexion DB pour l'import). Même confinement que la console mongosh.
- **Rate-limit par-IP dédié** sur `/import` (fenêtre stricte, quelques/min).
- `X-Provisioner-Key` sur l'appel exec-dump ; taille cappée ; timeout borné ; sortie plafonnée. Données restaurées bornées par le quota volume (soft-enforce SP6).

---

## 5. Frontend

- **`InitialDataPicker.vue`** : activer `dump`. Quand `dump` est sélectionné → afficher un **input fichier** (`accept=".sql,.js"`, hint taille max ~16 Mo, refus client-side si dépassé). Le bouton create reste désactivé tant qu'aucun fichier n'est choisi quand dump est sélectionné.
- **`NewView.vue` (orchestration)** quand « Import dump » + fichier + create :
  1. Créer l'instance avec `initialData="blank"`.
  2. Au 201, **POST multipart** le fichier vers `/api/instances/{token}/import`.
  3. Afficher progression (upload/restore) puis résultat — succès, ou la sortie client (bloc texte, comme la sortie mongosh) si erreurs.
  4. Naviguer vers l'instance.
- **`src/api/instances.ts`** : `importDump(token, file)` → POST multipart ; type de réponse `{ success, output, error, truncated, ms }`.

---

## 6. Phasage & tests

Via l'API Docker (VM Docker Desktop), collection `docker-sequential`. mongo/mysql/mariadb arm64-natifs (rapides) ; mssql émulé (lent).

| Phase | Contenu | Test |
|---|---|---|
| **1 — Canal exec-dump** | `ExecDumpAsync` + endpoint + client natif par moteur (admin) + livraison `/tmp` + timeout/cap ; records Provisioner | par moteur, exec-dump un petit dump (`CREATE TABLE`+`INSERT` SQL ; `insertMany` `.js` mongo) → donnée restaurée et interrogeable ; **preuve admin** : un statement admin-only (pg `CREATE EXTENSION`) réussit ; runaway → timeout ; sortie volumineuse tronquée. |
| **2 — Endpoint API import** | `POST /instances/{token}/import` (multipart, cap, ownership, rate-limit) + `IDumpImporter` + `IProvisionerClient.ExecDumpAsync` + records Api | upload dump → success + donnée présente ; **surtaille → 413** ; dump invalide → `success=false` + sortie d'erreur ; mauvais owner → refusé. |
| **3 — Frontend** | activer dump + input fichier + orchestration wizard create→import + affichage résultat | Vitest (dump activé, input fichier, orchestration mockée, résultat affiché). |
| **4 — e2e + docs** | cas import dans `e2e-fullstack.sh` (create blank + import dump sur postgres + mongo via Caddy → interroger la donnée restaurée → destroy) ; `SECURITY.md` (modèle sécurité import) | e2e full-stack import. |

---

## 7. Hors périmètre (différé)

- Dumps volumineux au-delà du cap (~16 Mo) — le pipe stdin (contrainte tmpfs levée) est le chemin d'évolution ; les très gros restores restent hors-scope.
- Formats binaires (pg_dump custom `-Fc`, mongodump BSON/archive) — ce sous-projet cible les dumps **texte** (`.sql` / `.js`). Le restore binaire (pg_restore/mongorestore) est un travail ultérieur.
- Export/download du dump d'une instance existante.

---

## 8. Critères de succès

Depuis le wizard, un utilisateur peut choisir **« Import dump »**, uploader un fichier `.sql` (moteurs SQL) ou `.js` (Mongo), et voir sa base **restaurée** à partir du fichier, puis **interroger les données restaurées** dans la console et de l'extérieur. Le dump s'exécute **en admin dans le conteneur jetable durci de l'utilisateur** (jamais sur l'API ; fidélité complète prouvée par un statement admin-only qui réussit), borné par cap de taille + timeout + sortie plafonnée + rate-limit, avec les données restaurées comptant dans le quota. La suite complète reste verte ; l'e2e crée-puis-importe un dump avec succès. Les dumps binaires et > 16 Mo restent hors-scope.
