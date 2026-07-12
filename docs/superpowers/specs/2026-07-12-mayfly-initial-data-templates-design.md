# MayFly — Sous-projet SP3 : Templates de données initiales — Design

- **Date** : 2026-07-12
- **Statut** : Validé en brainstorming, en attente de relecture utilisateur
- **Sous-projet** : SP3 — Templates de seed (Northwind portable + E-commerce + Blog + IoT)
- **Pré-requis** : slice-1 + SP6 (durcissement) + SP2 (moteurs SQL) + MongoDB mergés sur `master`

---

## 1. Contexte & objectif

Le wizard MayFly **affiche déjà** E-commerce / Blog / IoT (placeholders désactivés) et une option Import dump, mais seuls **Blank** (tous moteurs) et **Northwind** (postgres seul) sont réellement implémentés. SP3 rend l'UI honnête : les 4 templates de seed deviennent réels et disponibles sur **les 5 moteurs** (PostgreSQL, MySQL, MariaDB, SQL Server, MongoDB).

### Décisions verrouillées (brainstorming)
- **4 templates** : `northwind`, `ecommerce`, `blog`, `iot`. Northwind (aujourd'hui pg-only) est **porté** pour couvrir tous les moteurs.
- **Authoring** : chaque template = **UN `.sql` en SQL ANSI portable** (postgres+mysql+mariadb+mssql) **+ UN variant JS Mongo** → 2 fichiers/template couvrent les 5 moteurs.
- **Import dump** : mécanisme distinct (upload + restore) qui ne partage pas de code avec le catalogue statique → **son propre sous-projet, juste après SP3** (hors périmètre ici).

---

## 2. Architecture

Réutilise le mécanisme de seeding existant (`IEngineProvider.BuildSetup(creds, initialData)` → init-script exécuté en admin au premier init, comme Northwind aujourd'hui), généralisé via un **catalogue de seeds**.

- **`SeedCatalog`** (Provisioner) : résout `(templateId, famille moteur)` → le texte du seed, depuis des **ressources embarquées** sous `MayFly.Provisioner/Seeding/` : par template, `<id>.sql` (portable) + `<id>.mongo.js`.
- Chaque provider consulte le catalogue dans `BuildSetup` et livre le seed par le canal adapté à son moteur (§4).
- Le seed s'exécute en **admin** au premier init (init-scripts SQL / `01-seed.js` Mongo / `sqlcmd -U sa` mssql). L'appuser (grants au niveau base) lit ensuite les objets seedés.

---

## 3. Catalogue & authoring portable

**Catalogue** : ressources embarquées `Seeding/<id>.sql` + `Seeding/<id>.mongo.js` pour `northwind`, `ecommerce`, `blog`, `iot`. `MayFly.Provisioner.csproj` déclare chacune en `<EmbeddedResource>`. `SeedCatalog` charge par nom de ressource (comme `ReadEmbeddedNorthwind` actuel).

**Contraintes SQL portable** (pour qu'UN `.sql` tourne sur postgres/mysql/mariadb/mssql) :
- **PK entières explicites** dans les INSERT — **pas** de SERIAL / AUTO_INCREMENT / IDENTITY.
- Types au **dénominateur commun** uniquement : `INT`, `VARCHAR(n)`, `DECIMAL(p,s)`. **Dates/timestamps → `VARCHAR(30)` ISO-8601** (les mots-clés TIMESTAMP/DATETIME/DATETIME2 diffèrent). **Booléens → `INT` 0/1** (SQL Server n'a pas BOOLEAN). **Pas de `TEXT`** (VARCHAR(MAX) mssql non portable).
- Identifiants `lower_snake_case` non-quotés, sans mots réservés. `CREATE TABLE` + `INSERT INTO … (cols) VALUES (…)` standard. Pas de fonctions dialecte-spécifiques.

**Variant Mongo** : `<id>.mongo.js` = `db.getCollection("…").insertMany([ … ])` — le même dataset logique en documents (imbrication naturelle, ex. post avec commentaires imbriqués), exécuté en init `.js`.

**Northwind porté** : le `Seeding/northwind.sql` pg-spécifique actuel est **réécrit** en sous-ensemble portable compact (customers / products / orders / order_details) pour couvrir tous les moteurs uniformément. Les assertions du test pg existant (`InitScriptTests` : table `products` présente, count > 0) restent satisfaites. Ajout de `Seeding/northwind.mongo.js`.

**Datasets (indicatif, petits & déterministes pour des assertions stables)** :
- `northwind` : customers, products, orders, order_details (sous-ensemble).
- `ecommerce` : products, customers, orders, order_items.
- `blog` : authors, posts, comments, tags (post_tags).
- `iot` : devices, sensor_readings (série temporelle avec timestamps ISO en VARCHAR).

---

## 4. Câblage providers + validateurs

**BuildSetup consulte le catalogue** quand `initialData ∈ {northwind, ecommerce, blog, iot}` :
- **Postgres** : ajoute le seed portable comme `01-seed.sql` (init-script) **+ son `GRANT ALL ON ALL TABLES/SEQUENCES IN SCHEMA public TO appuser`** en fin (grants pg par-objet, comme aujourd'hui).
- **MySQL / MariaDB** : ajoute le seed portable comme `01-seed.sql` (init-script), **sans grant** (l'appuser a `ON appdb.*`). MariaDB hérite via la sous-classe MySQL.
- **SQL Server** : pas d'`initdb.d` → le seed passe par le **chemin PostReadyExec `sqlcmd` (admin `sa`)**, après le setup de rôle. Livraison du seed portable à sqlcmd tranchée au plan (fichier monté + `sqlcmd -i`, ou batch `-Q` GO-séparé — le choix gère la taille du seed).
- **Mongo** : ajoute le variant `01-seed.js` (init `.js`, exécuté en admin après `00-roles.js`). L'appuser (readWrite sur appdb) lit ensuite les collections.

**Validateurs (les DEUX — `MayFly.Api/Validation/ApiSpecValidator.cs` ET `MayFly.Provisioner/Validation/InstanceSpecValidator.cs`)** :
- L'ensemble `initialData` autorisé devient `{ blank, northwind, ecommerce, blog, iot }`.
- **La règle « northwind → postgres seul » disparaît** : les 4 templates sont portables → **valides sur tous les moteurs**. (`dump` reste non-autorisé — hors périmètre SP3.)

---

## 5. Frontend

Concentré dans **`MayFly.Web/src/components/InitialDataPicker.vue`** :
- `isEnabled` : `blank` toujours ; **`northwind`/`ecommerce`/`blog`/`iot` activés sur TOUS les moteurs** ; **`dump` reste désactivé** (« soon » — sous-projet suivant).
- Le `watch` de reset-vers-`blank` (au changement de moteur) devient quasi no-op — aucun des 4 templates ne se désactive plus par moteur ; on le garde en garde défensive (utile quand `dump` arrivera avec un gating).

Rien d'autre : le wizard envoie déjà `initialData: <id>` ; le create le transporte ; l'InstanceView affiche le seed choisi ; les descriptions des templates sont déjà dans le design importé.

---

## 6. Phasage & tests

Via l'API Docker dans la VM Docker Desktop, collection `docker-sequential`. mongo/mysql/mariadb arm64-natifs (rapides) ; mssql émulé (lent, timeouts généreux).

| Phase | Contenu | Test |
|---|---|---|
| **1 — Catalogue + Northwind portable** | `SeedCatalog` ; câbler les 5 providers (BuildSetup → catalogue) ; relâcher les 2 validateurs ; porter Northwind en portable + `northwind.mongo.js` | provision `initialData=northwind` sur **les 5 moteurs** → appuser voit les données (le test pg `InitScriptTests` reste vert). Prouve le mécanisme de bout en bout. |
| **2 — E-commerce** | `ecommerce.sql` portable + `ecommerce.mongo.js` | provision `ecommerce` × 5 moteurs → row/doc count connu. |
| **3 — Blog** | `blog.sql` + `blog.mongo.js` | idem × 5. |
| **4 — IoT** | `iot.sql` + `iot.mongo.js` (timestamps ISO en VARCHAR) | idem × 5. |
| **5 — Frontend + e2e** | InitialDataPicker active les 4 templates sur tous les moteurs (`dump` désactivé) ; validateurs | Vitest (gating) ; e2e full-stack : créer un instance avec un template sur quelques moteurs via Caddy → interroger les données seedées. |

**Risque principal couvert par les tests** : l'incompatibilité de dialecte du SQL "portable" — **chaque moteur SQL** exécute réellement le seed dans son test d'intégration (postgres/mysql/mariadb/mssql), ce qui prouve la portabilité. Le variant Mongo est testé séparément.

---

## 7. Hors périmètre (différé)

- **Import dump** (`.sql`/dump uploadé + restore) — sous-projet distinct juste après SP3 (endpoint multipart, limite de taille, application dans le conteneur confiné, sécurité des dumps non-fiables).
- Datasets volumineux / réalistes au-delà d'échantillons compacts déterministes.
- Édition/personnalisation de template par l'utilisateur.

---

## 8. Critères de succès

Depuis le wizard, un utilisateur peut choisir **Northwind, E-commerce, Blog ou IoT** sur **n'importe lequel des 5 moteurs**, obtenir une base **pré-remplie** avec le dataset, et **interroger les données seedées** dans la console (en tant qu'appuser) et de l'extérieur. Un seul `.sql` portable par template tourne sur les 4 moteurs SQL (prouvé par un test d'intégration par moteur) ; un variant JS seede Mongo. Les 2 validateurs acceptent les 4 templates sur tous les moteurs ; le durcissement SP6 et l'isolation restent intacts (le seed s'exécute en admin au provisioning, jamais exposé). La suite complète (slice-1 + SP6 + SP2 + Mongo + SP3) reste verte ; l'e2e crée un instance seedé avec succès. Import dump reste `« soon »` (sous-projet suivant).
