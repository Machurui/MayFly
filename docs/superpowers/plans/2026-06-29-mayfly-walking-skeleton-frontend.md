# MayFly Walking Skeleton — Frontend Implementation Plan (Plan 2/2)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> **Depends on Plan 1 (backend).** The API contract from Plan 1 Tasks 14–15 is the source of truth for `src/api/types.ts`.

**Goal:** Build the MayFly SPA — the four axes (new / instance / console / all) — from the imported Claude Design, wired to the real Plan 1 API: create a Postgres DB, view its connection string + connect snippets, run real queries in a console, and see the dashboard.

**Architecture:** Vue 3 `<script setup>` + TypeScript, Vite build, Vue Router for the four axes, Pinia for cross-view state, TanStack Query (vue-query) for server state/polling, CodeMirror 6 for the SQL console. The Claude Design `index.html` is imported via the `claude_design` MCP and decomposed into per-axis components. Served statically behind Caddy under the same origin as `/api/*` (no CORS).

**Tech Stack:** Vue 3, Vite, TypeScript, Tailwind CSS, Vue Router, Pinia, @tanstack/vue-query, CodeMirror 6 (`@codemirror/lang-sql`), Vitest + @vue/test-utils.

## Global Constraints

- **Node:** v22 / npm 10.
- **Same-origin API:** all calls go to `/api/*` (Caddy proxies to the backend); no base URL, no CORS, cookies sent automatically (`credentials: "same-origin"`).
- **Allowed values (must match backend, drive the wizard UI):** engine ∈ {`postgres`} (others shown disabled); ttlHours ∈ {3, 6, 12}; storageMb ∈ {256, 512, 1024, 2048}; initialData ∈ {`blank`, `northwind`} (others shown disabled).
- **Capability token in URL:** instance/console routes are `/instance/:token` and `/console/:token`. The token is the only credential.
- **Design source of truth:** the imported Claude Design `index.html` drives the visual layer; do not invent a different look. Tailwind classes come from the import.
- **Slice-1 thin axes:** only the features listed per axis in the spec §3 are built; deferred features are not stubbed in the UI.

---

## File Structure

```
MayFly/MayFly.Web/
  package.json  vite.config.ts  tsconfig.json  tailwind.config.js  postcss.config.js
  index.html
  src/
    main.ts                       # app bootstrap: router, pinia, vue-query
    App.vue                       # shell + axis nav
    style.css                     # tailwind entry + imported design tokens
    router/index.ts               # /, /new, /instance/:token, /console/:token
    api/types.ts                  # mirrors Plan 1 DTOs
    api/client.ts                 # fetch wrapper (same-origin, JSON, error mapping)
    api/instances.ts              # API fns + vue-query hooks
    lib/format.ts                 # bytes, countdown helpers
    lib/snippets.ts               # connection code snippets (bash/python/node/go/.net)
    components/
      design/                     # raw imported Claude Design fragments
      AppNav.vue
      EnginePicker.vue  TtlPicker.vue  StoragePicker.vue  InitialDataPicker.vue
      ConnectionSnippets.vue
      QueryResults.vue
      StatCard.vue
    views/
      NewView.vue                 # axe new (wizard)
      AllView.vue                 # axe all (dashboard)
      InstanceView.vue            # axe instance
      ConsoleView.vue             # axe console
  tests/
    snippets.test.ts
    format.test.ts
    NewView.test.ts
    QueryResults.test.ts
```

---

## Phase 0 — Scaffold & design import

### Task 1: Vite + Vue + TS + Tailwind scaffold

**Files:** Create `MayFly.Web/` project.

- [ ] **Step 1: Scaffold and install**

```bash
cd MayFly
npm create vite@latest MayFly.Web -- --template vue-ts
cd MayFly.Web
npm install
npm install vue-router pinia @tanstack/vue-query
npm install codemirror @codemirror/lang-sql @codemirror/view @codemirror/state
npm install -D tailwindcss @tailwindcss/postcss postcss autoprefixer vitest @vue/test-utils jsdom
```

- [ ] **Step 2: Configure Tailwind (v4 PostCSS plugin)**

`postcss.config.js`:
```js
export default { plugins: { '@tailwindcss/postcss': {}, autoprefixer: {} } }
```
`src/style.css` (first line):
```css
@import "tailwindcss";
```

- [ ] **Step 3: Configure Vite dev proxy + Vitest**

`vite.config.ts`:
```ts
import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

export default defineConfig({
  plugins: [vue()],
  server: { proxy: { '/api': 'http://localhost:5000' } },
  test: { environment: 'jsdom', globals: true },
})
```

- [ ] **Step 4: Verify dev build runs**

Run: `npm run build`
Expected: `vite build` completes, `dist/` produced, 0 errors.

- [ ] **Step 5: Commit**

```bash
cd /Users/fanfan/Documents/App/MayFly
git add -A && git commit -m "chore(web): scaffold Vue 3 + Vite + TS + Tailwind + deps"
```

---

### Task 2: Import the Claude Design

**Files:** Create `MayFly.Web/src/components/design/` (imported fragments), update `src/style.css` with design tokens.

- [ ] **Step 1: Authenticate to the design MCP**

Run the Claude Code command: `/design-login` (authenticates the `claude_design` MCP at `https://api.anthropic.com/v1/design/mcp`).

- [ ] **Step 2: Import the project file**

Use the `claude_design` MCP to import `index.html` from:
`https://claude.ai/design/p/0ff7f810-464e-4010-95ac-5f49cecf4ae6?file=index.html`
Save the raw imported HTML to `src/components/design/index.html` for reference.

- [ ] **Step 3: Extract design tokens and layout**

From the imported HTML: copy color tokens / fonts / spacing conventions into `src/style.css` (below the Tailwind import), and identify the markup blocks for each of the four axes (new / instance / console / all). Record which block maps to which view in a comment header in each `views/*.vue` file (created in later tasks).

- [ ] **Step 4: Verify tokens render**

Run: `npm run dev`, open the app — base styling (background, fonts, colors) matches the Claude Design.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(web): import Claude Design (tokens + per-axis markup reference)"
```

---

## Phase 1 — API layer

### Task 3: API types + client

**Files:** Create `src/api/types.ts`, `src/api/client.ts`.
**Test:** none (pure types + thin wrapper; covered via consumers).

**Interfaces:**
- Produces (mirrors Plan 1 DTOs):
```ts
export interface InstanceDto {
  token: string; engine: string; state: string; ttlHours: number; storageQuotaMb: number;
  lastSizeBytes: number; initialData: string; createdAt: string; expiresAt: string;
  connectionString: string; publicPort: number; dbName: string; dbUser: string;
}
export interface CreateInstanceDto { engine: string; ttlHours: number; storageMb: number; initialData: string; }
export interface QueryResultDto {
  success: boolean; columns: string[]; rows: unknown[][]; rowCount: number;
  durationMs: number; message: string; error: string | null;
}
export interface DashboardSummary {
  aliveCount: number; maxAlive: number; queriesToday: number;
  storageUsedBytes: number; nextExpiry: string | null;
}
```
- `client.ts` produces `api.get<T>(path)`, `api.post<T>(path, body)`, `api.del(path)`; throws `ApiError{status, message}` on non-2xx.

- [ ] **Step 1: Write `src/api/types.ts`** (content above).

- [ ] **Step 2: Write `src/api/client.ts`**
```ts
export class ApiError extends Error {
  constructor(public status: number, message: string) { super(message) }
}

async function handle<T>(resp: Response): Promise<T> {
  if (!resp.ok) {
    let msg = resp.statusText
    try { const b = await resp.json(); msg = b.error ?? msg } catch { /* non-json */ }
    throw new ApiError(resp.status, msg)
  }
  return resp.status === 204 ? (undefined as T) : await resp.json() as T
}

const opts: RequestInit = { credentials: 'same-origin', headers: { 'Content-Type': 'application/json' } }

export const api = {
  get: <T>(p: string) => fetch(p, opts).then(r => handle<T>(r)),
  post: <T>(p: string, body: unknown) =>
    fetch(p, { ...opts, method: 'POST', body: JSON.stringify(body) }).then(r => handle<T>(r)),
  del: (p: string) => fetch(p, { ...opts, method: 'DELETE' }).then(r => handle<void>(r)),
}
```

- [ ] **Step 3: Typecheck**

Run: `npx vue-tsc --noEmit`
Expected: 0 errors.

- [ ] **Step 4: Commit**
```bash
git add -A && git commit -m "feat(web): API types + fetch client"
```

---

### Task 4: Instances API module + vue-query hooks

**Files:** Create `src/api/instances.ts`. Modify `src/main.ts` (install VueQueryPlugin, router, pinia).

**Interfaces:**
- Produces: `createInstance(dto)`, `getInstance(token)`, `listMine()`, `destroyInstance(token)`, `runQuery(token, sql)`, `getDashboard()`; plus vue-query hooks `useMyInstances()`, `useDashboard()`, `useInstance(token)`.

- [ ] **Step 1: Write `src/api/instances.ts`**
```ts
import { useQuery } from '@tanstack/vue-query'
import { api } from './client'
import type { InstanceDto, CreateInstanceDto, QueryResultDto, DashboardSummary } from './types'

export const createInstance = (dto: CreateInstanceDto) => api.post<InstanceDto>('/api/instances', dto)
export const getInstance = (token: string) => api.get<InstanceDto>(`/api/instances/${token}`)
export const listMine = () => api.get<InstanceDto[]>('/api/instances')
export const destroyInstance = (token: string) => api.del(`/api/instances/${token}`)
export const runQuery = (token: string, sql: string) =>
  api.post<QueryResultDto>(`/api/instances/${token}/query`, { sql })
export const getDashboard = () => api.get<DashboardSummary>('/api/dashboard')

export const useMyInstances = () =>
  useQuery({ queryKey: ['instances'], queryFn: listMine, refetchInterval: 10000 })
export const useDashboard = () =>
  useQuery({ queryKey: ['dashboard'], queryFn: getDashboard, refetchInterval: 10000 })
export const useInstance = (token: string) =>
  useQuery({ queryKey: ['instance', token], queryFn: () => getInstance(token), refetchInterval: 10000 })
```

- [ ] **Step 2: Write `src/main.ts`**
```ts
import { createApp } from 'vue'
import { createPinia } from 'pinia'
import { VueQueryPlugin } from '@tanstack/vue-query'
import App from './App.vue'
import { router } from './router'
import './style.css'

createApp(App).use(createPinia()).use(router).use(VueQueryPlugin).mount('#app')
```

- [ ] **Step 3: Typecheck**

Run: `npx vue-tsc --noEmit`
Expected: 0 errors (router/App created in Task 5; create stubs now if needed).

- [ ] **Step 4: Commit**
```bash
git add -A && git commit -m "feat(web): instances API module + vue-query hooks + app bootstrap"
```

---

## Phase 2 — Shell, helpers, snippets

### Task 5: Router + App shell + AppNav

**Files:** Create `src/router/index.ts`, `src/App.vue`, `src/components/AppNav.vue`. Stub `views/NewView.vue`, `AllView.vue`, `InstanceView.vue`, `ConsoleView.vue`.

**Interfaces:**
- Routes: `/` → AllView, `/new` → NewView, `/instance/:token` → InstanceView, `/console/:token` → ConsoleView.

- [ ] **Step 1: Write `src/router/index.ts`**
```ts
import { createRouter, createWebHistory } from 'vue-router'

export const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/', name: 'all', component: () => import('../views/AllView.vue') },
    { path: '/new', name: 'new', component: () => import('../views/NewView.vue') },
    { path: '/instance/:token', name: 'instance', component: () => import('../views/InstanceView.vue'), props: true },
    { path: '/console/:token', name: 'console', component: () => import('../views/ConsoleView.vue'), props: true },
  ],
})
```

- [ ] **Step 2: Write `src/components/AppNav.vue`** (axis nav; classes from imported design)
```vue
<script setup lang="ts">
import { RouterLink } from 'vue-router'
</script>
<template>
  <nav class="flex gap-2 p-4 border-b">
    <RouterLink to="/new" class="px-3 py-1.5 rounded hover:bg-black/5">new</RouterLink>
    <RouterLink to="/" class="px-3 py-1.5 rounded hover:bg-black/5">all</RouterLink>
  </nav>
</template>
```

- [ ] **Step 3: Write `src/App.vue` + view stubs**
`src/App.vue`:
```vue
<script setup lang="ts">
import AppNav from './components/AppNav.vue'
</script>
<template>
  <div class="min-h-screen">
    <AppNav />
    <main class="p-6"><RouterView /></main>
  </div>
</template>
```
Each stub view (e.g. `views/NewView.vue`):
```vue
<template><div>new</div></template>
```
(Repeat the stub for AllView, InstanceView, ConsoleView — they are filled in later tasks.)

- [ ] **Step 4: Verify**

Run: `npm run build`
Expected: builds; navigating `/new` and `/` renders stubs.

- [ ] **Step 5: Commit**
```bash
git add -A && git commit -m "feat(web): router + app shell + axis nav"
```

---

### Task 6: Format helpers (bytes, countdown)

**Files:** Create `src/lib/format.ts`. **Test:** `tests/format.test.ts`.

**Interfaces:** `formatBytes(n: number): string`, `timeUntil(iso: string): string`.

- [ ] **Step 1: Write the failing test**

`tests/format.test.ts`:
```ts
import { describe, it, expect } from 'vitest'
import { formatBytes, timeUntil } from '../src/lib/format'

describe('formatBytes', () => {
  it('formats MB and GB', () => {
    expect(formatBytes(0)).toBe('0 B')
    expect(formatBytes(256 * 1024 * 1024)).toBe('256.0 MB')
    expect(formatBytes(2 * 1024 ** 3)).toBe('2.0 GB')
  })
})

describe('timeUntil', () => {
  it('returns expired for past dates', () => {
    expect(timeUntil(new Date(Date.now() - 1000).toISOString())).toBe('expired')
  })
  it('returns h/m for future dates', () => {
    expect(timeUntil(new Date(Date.now() + 2 * 3600_000 + 60_000).toISOString())).toMatch(/^2h/)
  })
})
```

- [ ] **Step 2: Run test to verify it fails**

Run: `npx vitest run tests/format.test.ts`
Expected: FAIL (module not found).

- [ ] **Step 3: Write `src/lib/format.ts`**
```ts
export function formatBytes(n: number): string {
  if (n <= 0) return '0 B'
  const units = ['B', 'KB', 'MB', 'GB']
  let i = 0, v = n
  while (v >= 1024 && i < units.length - 1) { v /= 1024; i++ }
  return i === 0 ? `${v} B` : `${v.toFixed(1)} ${units[i]}`
}

export function timeUntil(iso: string): string {
  const ms = new Date(iso).getTime() - Date.now()
  if (ms <= 0) return 'expired'
  const h = Math.floor(ms / 3600_000)
  const m = Math.floor((ms % 3600_000) / 60_000)
  return `${h}h ${m}m`
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `npx vitest run tests/format.test.ts`
Expected: PASS.

- [ ] **Step 5: Commit**
```bash
git add -A && git commit -m "feat(web): format helpers (bytes, countdown)"
```

---

### Task 7: Connection snippets generator (bash/python/node/go/.net)

**Files:** Create `src/lib/snippets.ts`, `src/components/ConnectionSnippets.vue`. **Test:** `tests/snippets.test.ts`.

**Interfaces:** `buildSnippets(inst: InstanceDto): Record<'bash'|'python'|'node'|'go'|'dotnet', string>`. Parses the `connectionString` (`postgresql://user:pass@host:port/db`).

- [ ] **Step 1: Write the failing test**

`tests/snippets.test.ts`:
```ts
import { describe, it, expect } from 'vitest'
import { buildSnippets } from '../src/lib/snippets'
import type { InstanceDto } from '../src/api/types'

const inst = {
  connectionString: 'postgresql://appuser:secret@db.example.com:20005/appdb',
  publicPort: 20005, dbName: 'appdb', dbUser: 'appuser',
} as InstanceDto

describe('buildSnippets', () => {
  const s = buildSnippets(inst)
  it('bash uses psql with the full URL', () => {
    expect(s.bash).toContain('psql "postgresql://appuser:secret@db.example.com:20005/appdb"')
  })
  it('python references psycopg and the host/port', () => {
    expect(s.python).toContain('psycopg')
    expect(s.python).toContain('db.example.com')
    expect(s.python).toContain('20005')
  })
  it('node uses pg Client', () => expect(s.node).toContain("new Client"))
  it('go uses pgx or connection URL', () => expect(s.go).toContain('appdb'))
  it('dotnet uses Npgsql connection string', () => expect(s.dotnet).toContain('Host=db.example.com'))
})
```

- [ ] **Step 2: Run test to verify it fails**

Run: `npx vitest run tests/snippets.test.ts`
Expected: FAIL (module not found).

- [ ] **Step 3: Write `src/lib/snippets.ts`**
```ts
import type { InstanceDto } from '../api/types'

interface Parts { user: string; pass: string; host: string; port: number; db: string; url: string }

function parse(inst: InstanceDto): Parts {
  const u = new URL(inst.connectionString)
  return {
    user: decodeURIComponent(u.username), pass: decodeURIComponent(u.password),
    host: u.hostname, port: Number(u.port), db: u.pathname.replace(/^\//, ''),
    url: inst.connectionString,
  }
}

export function buildSnippets(inst: InstanceDto) {
  const p = parse(inst)
  return {
    bash: `psql "${p.url}"`,
    python: `import psycopg
conn = psycopg.connect(host="${p.host}", port=${p.port}, dbname="${p.db}", user="${p.user}", password="${p.pass}")
with conn.cursor() as cur:
    cur.execute("SELECT 1")
    print(cur.fetchone())`,
    node: `import { Client } from 'pg'
const client = new Client({ host: '${p.host}', port: ${p.port}, database: '${p.db}', user: '${p.user}', password: '${p.pass}' })
await client.connect()
console.log((await client.query('SELECT 1')).rows)`,
    go: `package main

import (
    "context"
    "fmt"
    "github.com/jackc/pgx/v5"
)

func main() {
    conn, _ := pgx.Connect(context.Background(), "${p.url}")
    defer conn.Close(context.Background())
    var n int
    conn.QueryRow(context.Background(), "SELECT 1").Scan(&n)
    fmt.Println(n)
}`,
    dotnet: `using Npgsql;
var cs = "Host=${p.host};Port=${p.port};Database=${p.db};Username=${p.user};Password=${p.pass}";
await using var conn = new NpgsqlConnection(cs);
await conn.OpenAsync();
await using var cmd = new NpgsqlCommand("SELECT 1", conn);
Console.WriteLine(await cmd.ExecuteScalarAsync());`,
  }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `npx vitest run tests/snippets.test.ts`
Expected: PASS.

- [ ] **Step 5: Write `src/components/ConnectionSnippets.vue`** (tabs over the snippets)
```vue
<script setup lang="ts">
import { ref, computed } from 'vue'
import type { InstanceDto } from '../api/types'
import { buildSnippets } from '../lib/snippets'

const props = defineProps<{ instance: InstanceDto }>()
const langs = ['bash', 'python', 'node', 'go', 'dotnet'] as const
const active = ref<typeof langs[number]>('bash')
const snippets = computed(() => buildSnippets(props.instance))
</script>
<template>
  <div class="rounded border">
    <div class="flex gap-1 border-b p-2">
      <button v-for="l in langs" :key="l" @click="active = l"
        :class="['px-2 py-1 rounded text-sm', active === l ? 'bg-black text-white' : 'hover:bg-black/5']">{{ l }}</button>
    </div>
    <pre class="p-3 overflow-x-auto text-sm"><code>{{ snippets[active] }}</code></pre>
  </div>
</template>
```

- [ ] **Step 6: Commit**
```bash
git add -A && git commit -m "feat(web): connection snippets (bash/python/node/go/.net) + tabs"
```

---

## Phase 3 — The four axes

### Task 8: Axe "new" — creation wizard

**Files:** Create `src/components/EnginePicker.vue`, `TtlPicker.vue`, `StoragePicker.vue`, `InitialDataPicker.vue`; fill `src/views/NewView.vue`. **Test:** `tests/NewView.test.ts`.

**Interfaces:** NewView posts `createInstance({engine,ttlHours,storageMb,initialData})`, then `router.push('/instance/'+token)`. On 429 shows quota message.

- [ ] **Step 1: Write the failing component test**

`tests/NewView.test.ts`:
```ts
import { describe, it, expect, vi, beforeEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { VueQueryPlugin } from '@tanstack/vue-query'

const push = vi.fn()
vi.mock('vue-router', () => ({ useRouter: () => ({ push }) }))
const createInstance = vi.fn()
vi.mock('../src/api/instances', () => ({ createInstance }))

import NewView from '../src/views/NewView.vue'

describe('NewView', () => {
  beforeEach(() => { push.mockReset(); createInstance.mockReset() })

  it('creates with defaults and navigates to the instance', async () => {
    createInstance.mockResolvedValue({ token: 'tok123' })
    const w = mount(NewView, { global: { plugins: [VueQueryPlugin] } })
    await w.find('[data-test="create"]').trigger('click')
    await flushPromises()
    expect(createInstance).toHaveBeenCalledWith(
      expect.objectContaining({ engine: 'postgres', ttlHours: 3, storageMb: 256, initialData: 'blank' }))
    expect(push).toHaveBeenCalledWith('/instance/tok123')
  })

  it('shows quota message on 429', async () => {
    createInstance.mockRejectedValue({ status: 429, message: 'IP quota of 3 active databases reached' })
    const w = mount(NewView, { global: { plugins: [VueQueryPlugin] } })
    await w.find('[data-test="create"]').trigger('click')
    await flushPromises()
    expect(w.text()).toContain('quota')
  })
})
```

- [ ] **Step 2: Run test to verify it fails**

Run: `npx vitest run tests/NewView.test.ts`
Expected: FAIL (NewView not implemented).

- [ ] **Step 3: Write the pickers and `NewView.vue`**

`src/components/EnginePicker.vue` (postgres enabled, others disabled):
```vue
<script setup lang="ts">
const model = defineModel<string>({ required: true })
const engines = [
  { id: 'postgres', label: 'PostgreSQL', enabled: true },
  { id: 'mysql', label: 'MySQL', enabled: false },
  { id: 'mariadb', label: 'MariaDB', enabled: false },
  { id: 'mongodb', label: 'MongoDB', enabled: false },
  { id: 'sqlserver', label: 'SQL Server', enabled: false },
]
</script>
<template>
  <div class="grid grid-cols-2 gap-2">
    <button v-for="e in engines" :key="e.id" :disabled="!e.enabled" @click="model = e.id"
      :class="['p-3 rounded border text-left', model === e.id ? 'border-black' : 'border-black/10',
               !e.enabled && 'opacity-40 cursor-not-allowed']">
      {{ e.label }}<span v-if="!e.enabled" class="text-xs"> (soon)</span>
    </button>
  </div>
</template>
```

`src/components/TtlPicker.vue`:
```vue
<script setup lang="ts">
const model = defineModel<number>({ required: true })
const opts = [3, 6, 12]
</script>
<template>
  <div class="flex gap-2">
    <button v-for="h in opts" :key="h" @click="model = h"
      :class="['px-3 py-2 rounded border', model === h ? 'border-black' : 'border-black/10']">{{ h }}h</button>
  </div>
</template>
```

`src/components/StoragePicker.vue`:
```vue
<script setup lang="ts">
const model = defineModel<number>({ required: true })
const opts = [{ mb: 256, l: '256 MB' }, { mb: 512, l: '512 MB' }, { mb: 1024, l: '1 GB' }, { mb: 2048, l: '2 GB' }]
</script>
<template>
  <div class="flex gap-2">
    <button v-for="o in opts" :key="o.mb" @click="model = o.mb"
      :class="['px-3 py-2 rounded border', model === o.mb ? 'border-black' : 'border-black/10']">{{ o.l }}</button>
  </div>
</template>
```

`src/components/InitialDataPicker.vue` (blank + northwind enabled, rest disabled):
```vue
<script setup lang="ts">
const model = defineModel<string>({ required: true })
const opts = [
  { id: 'blank', label: 'Blank', enabled: true },
  { id: 'northwind', label: 'Northwind', enabled: true },
  { id: 'ecommerce', label: 'E-commerce', enabled: false },
  { id: 'blog', label: 'Blog', enabled: false },
  { id: 'iot', label: 'IoT Timeseries', enabled: false },
  { id: 'dump', label: 'Import dump', enabled: false },
]
</script>
<template>
  <div class="grid grid-cols-2 gap-2">
    <button v-for="o in opts" :key="o.id" :disabled="!o.enabled" @click="model = o.id"
      :class="['p-2 rounded border text-left', model === o.id ? 'border-black' : 'border-black/10',
               !o.enabled && 'opacity-40 cursor-not-allowed']">{{ o.label }}</button>
  </div>
</template>
```

`src/views/NewView.vue`:
```vue
<script setup lang="ts">
import { ref } from 'vue'
import { useRouter } from 'vue-router'
import { createInstance } from '../api/instances'
import EnginePicker from '../components/EnginePicker.vue'
import TtlPicker from '../components/TtlPicker.vue'
import StoragePicker from '../components/StoragePicker.vue'
import InitialDataPicker from '../components/InitialDataPicker.vue'

const router = useRouter()
const engine = ref('postgres')
const ttlHours = ref(3)
const storageMb = ref(256)
const initialData = ref('blank')
const error = ref('')
const busy = ref(false)

async function create() {
  busy.value = true; error.value = ''
  try {
    const inst = await createInstance({ engine: engine.value, ttlHours: ttlHours.value,
      storageMb: storageMb.value, initialData: initialData.value })
    router.push('/instance/' + inst.token)
  } catch (e: any) {
    error.value = e?.status === 429
      ? 'You reached the quota of 3 active databases for your IP.'
      : (e?.message ?? 'Creation failed')
  } finally { busy.value = false }
}
</script>
<template>
  <div class="max-w-xl space-y-6">
    <h1 class="text-2xl font-semibold">New database</h1>
    <section><h2 class="mb-2 font-medium">Engine</h2><EnginePicker v-model="engine" /></section>
    <section><h2 class="mb-2 font-medium">Time to live</h2><TtlPicker v-model="ttlHours" /></section>
    <section><h2 class="mb-2 font-medium">Storage quota</h2><StoragePicker v-model="storageMb" /></section>
    <section><h2 class="mb-2 font-medium">Initial data</h2><InitialDataPicker v-model="initialData" /></section>
    <p v-if="error" class="text-red-600">{{ error }}</p>
    <button data-test="create" :disabled="busy" @click="create"
      class="px-4 py-2 rounded bg-black text-white disabled:opacity-50">
      {{ busy ? 'Creating…' : 'Create database' }}
    </button>
  </div>
</template>
```

- [ ] **Step 4: Run test to verify it passes**

Run: `npx vitest run tests/NewView.test.ts`
Expected: PASS.

- [ ] **Step 5: Commit**
```bash
git add -A && git commit -m "feat(web): axe new — creation wizard (engine/ttl/storage/initial data)"
```

---

### Task 9: Axe "all" — dashboard

**Files:** Create `src/components/StatCard.vue`; fill `src/views/AllView.vue`. **Test:** none new (vue-query hooks covered indirectly; logic is presentational).

**Interfaces:** Uses `useDashboard()` + `useMyInstances()`. Shows: DB alive (`aliveCount/maxAlive`), queries today, storage used (`formatBytes`), next expiry (`timeUntil`), and the instance list with links to `/instance/:token`.

- [ ] **Step 1: Write `src/components/StatCard.vue`**
```vue
<script setup lang="ts">
defineProps<{ label: string; value: string }>()
</script>
<template>
  <div class="rounded border p-4">
    <div class="text-sm text-black/60">{{ label }}</div>
    <div class="text-2xl font-semibold">{{ value }}</div>
  </div>
</template>
```

- [ ] **Step 2: Write `src/views/AllView.vue`**
```vue
<script setup lang="ts">
import { RouterLink } from 'vue-router'
import { useDashboard, useMyInstances } from '../api/instances'
import { formatBytes, timeUntil } from '../lib/format'
import StatCard from '../components/StatCard.vue'

const { data: d } = useDashboard()
const { data: instances } = useMyInstances()
</script>
<template>
  <div class="space-y-6">
    <div class="grid grid-cols-2 md:grid-cols-4 gap-3">
      <StatCard label="DB alive" :value="`${d?.aliveCount ?? 0} / ${d?.maxAlive ?? 3}`" />
      <StatCard label="Queries today" :value="String(d?.queriesToday ?? 0)" />
      <StatCard label="Storage used" :value="formatBytes(d?.storageUsedBytes ?? 0)" />
      <StatCard label="Next expiry" :value="d?.nextExpiry ? timeUntil(d.nextExpiry) : '—'" />
    </div>
    <section>
      <h2 class="mb-2 font-medium">Your instances</h2>
      <div class="space-y-2">
        <RouterLink v-for="i in instances" :key="i.token" :to="'/instance/' + i.token"
          class="block rounded border p-3 hover:bg-black/5">
          <div class="flex justify-between">
            <span>{{ i.engine }} · {{ i.dbName }}</span>
            <span class="text-sm text-black/60">{{ i.state }} · {{ timeUntil(i.expiresAt) }}</span>
          </div>
        </RouterLink>
        <p v-if="!instances?.length" class="text-black/50">No databases yet. <RouterLink to="/new" class="underline">Create one</RouterLink>.</p>
      </div>
    </section>
  </div>
</template>
```

- [ ] **Step 3: Verify build**

Run: `npm run build`
Expected: builds clean.

- [ ] **Step 4: Commit**
```bash
git add -A && git commit -m "feat(web): axe all — dashboard (quota/queries/storage/next expiry + instances)"
```

---

### Task 10: Axe "instance" — recap, connection string, snippets, destroy

**Files:** Fill `src/views/InstanceView.vue`. Uses `ConnectionSnippets.vue`.

**Interfaces:** Props `{ token: string }`. Uses `useInstance(token)`; "Open console" → `/console/:token`; "Destroy" → `destroyInstance` then `/`.

- [ ] **Step 1: Write `src/views/InstanceView.vue`**
```vue
<script setup lang="ts">
import { ref } from 'vue'
import { useRouter } from 'vue-router'
import { useInstance, destroyInstance } from '../api/instances'
import { formatBytes, timeUntil } from '../lib/format'
import ConnectionSnippets from '../components/ConnectionSnippets.vue'

const props = defineProps<{ token: string }>()
const router = useRouter()
const { data: inst } = useInstance(props.token)
const copied = ref(false)

async function copyCs() {
  if (!inst.value) return
  await navigator.clipboard.writeText(inst.value.connectionString)
  copied.value = true; setTimeout(() => (copied.value = false), 1500)
}
async function destroy() {
  await destroyInstance(props.token); router.push('/')
}
</script>
<template>
  <div v-if="inst" class="max-w-2xl space-y-6">
    <header class="flex items-center justify-between">
      <h1 class="text-2xl font-semibold">{{ inst.engine }} · {{ inst.dbName }}</h1>
      <RouterLink :to="'/console/' + token" class="px-3 py-1.5 rounded bg-black text-white">Open console</RouterLink>
    </header>

    <div class="grid grid-cols-2 md:grid-cols-4 gap-3 text-sm">
      <div class="rounded border p-3"><div class="text-black/60">State</div>{{ inst.state }}</div>
      <div class="rounded border p-3"><div class="text-black/60">Expires</div>{{ timeUntil(inst.expiresAt) }}</div>
      <div class="rounded border p-3"><div class="text-black/60">Storage</div>{{ formatBytes(inst.lastSizeBytes) }} / {{ inst.storageQuotaMb }} MB</div>
      <div class="rounded border p-3"><div class="text-black/60">Port</div>{{ inst.publicPort }}</div>
    </div>

    <section>
      <h2 class="mb-2 font-medium">Connection string</h2>
      <div class="flex gap-2">
        <code class="flex-1 rounded border p-2 text-sm break-all">{{ inst.connectionString }}</code>
        <button @click="copyCs" class="px-3 rounded border">{{ copied ? 'Copied' : 'Copy' }}</button>
      </div>
    </section>

    <section>
      <h2 class="mb-2 font-medium">Connect</h2>
      <ConnectionSnippets :instance="inst" />
    </section>

    <button @click="destroy" class="px-4 py-2 rounded border border-red-600 text-red-600">Destroy database</button>
  </div>
  <p v-else class="text-black/50">Loading…</p>
</template>
```

- [ ] **Step 2: Verify build**

Run: `npm run build`
Expected: builds clean.

- [ ] **Step 3: Commit**
```bash
git add -A && git commit -m "feat(web): axe instance — recap, connection string, snippets, destroy"
```

---

### Task 11: Axe "console" — CodeMirror editor + results/messages

**Files:** Create `src/components/QueryResults.vue`; fill `src/views/ConsoleView.vue`. **Test:** `tests/QueryResults.test.ts`.

**Interfaces:** Props `{ token }`. CodeMirror SQL editor; "Run" → `runQuery(token, sql)`; tabs **results** (table) + **messages** (status/error/duration).

- [ ] **Step 1: Write the failing test**

`tests/QueryResults.test.ts`:
```ts
import { describe, it, expect } from 'vitest'
import { mount } from '@vue/test-utils'
import QueryResults from '../src/components/QueryResults.vue'
import type { QueryResultDto } from '../src/api/types'

const ok: QueryResultDto = {
  success: true, columns: ['n', 'name'], rows: [[1, 'a'], [2, 'b']],
  rowCount: 2, durationMs: 5, message: '2 row(s)', error: null,
}

describe('QueryResults', () => {
  it('renders columns and rows', () => {
    const w = mount(QueryResults, { props: { result: ok } })
    expect(w.findAll('thead th').map(t => t.text())).toEqual(['n', 'name'])
    expect(w.findAll('tbody tr')).toHaveLength(2)
    expect(w.text()).toContain('a')
  })

  it('shows error on failure', () => {
    const w = mount(QueryResults, { props: { result: {
      ...ok, success: false, columns: [], rows: [], rowCount: 0, error: 'relation does not exist' } } })
    expect(w.text()).toContain('relation does not exist')
  })
})
```

- [ ] **Step 2: Run test to verify it fails**

Run: `npx vitest run tests/QueryResults.test.ts`
Expected: FAIL (component not found).

- [ ] **Step 3: Write `src/components/QueryResults.vue`**
```vue
<script setup lang="ts">
import { ref } from 'vue'
import type { QueryResultDto } from '../api/types'
defineProps<{ result: QueryResultDto }>()
const tab = ref<'results' | 'messages'>('results')
</script>
<template>
  <div class="rounded border">
    <div class="flex gap-1 border-b p-2 text-sm">
      <button @click="tab = 'results'" :class="tab === 'results' ? 'font-semibold' : ''">results</button>
      <button @click="tab = 'messages'" :class="tab === 'messages' ? 'font-semibold' : ''">messages</button>
    </div>
    <div v-if="tab === 'results'" class="overflow-x-auto p-2">
      <p v-if="!result.success" class="text-red-600">{{ result.error }}</p>
      <table v-else class="text-sm w-full">
        <thead><tr><th v-for="c in result.columns" :key="c" class="text-left border-b px-2 py-1">{{ c }}</th></tr></thead>
        <tbody>
          <tr v-for="(r, ri) in result.rows" :key="ri">
            <td v-for="(cell, ci) in r" :key="ci" class="border-b px-2 py-1">{{ cell }}</td>
          </tr>
        </tbody>
      </table>
    </div>
    <div v-else class="p-3 text-sm">
      <p :class="result.success ? '' : 'text-red-600'">{{ result.error ?? result.message }}</p>
      <p class="text-black/60">{{ result.rowCount }} row(s) · {{ result.durationMs }} ms</p>
    </div>
  </div>
</template>
```

- [ ] **Step 4: Run test to verify it passes**

Run: `npx vitest run tests/QueryResults.test.ts`
Expected: PASS.

- [ ] **Step 5: Write `src/views/ConsoleView.vue`** (CodeMirror SQL + run)
```vue
<script setup lang="ts">
import { ref, onMounted, shallowRef } from 'vue'
import { EditorView, basicSetup } from 'codemirror'
import { sql, PostgreSQL } from '@codemirror/lang-sql'
import { runQuery } from '../api/instances'
import type { QueryResultDto } from '../api/types'
import QueryResults from '../components/QueryResults.vue'

const props = defineProps<{ token: string }>()
const editorEl = ref<HTMLDivElement>()
const view = shallowRef<EditorView>()
const result = ref<QueryResultDto | null>(null)
const busy = ref(false)

onMounted(() => {
  view.value = new EditorView({
    parent: editorEl.value!,
    doc: 'SELECT 1;',
    extensions: [basicSetup, sql({ dialect: PostgreSQL })],
  })
})

async function run() {
  busy.value = true
  try { result.value = await runQuery(props.token, view.value!.state.doc.toString()) }
  finally { busy.value = false }
}
</script>
<template>
  <div class="space-y-4">
    <div class="flex items-center justify-between">
      <RouterLink :to="'/instance/' + token" class="underline text-sm">← instance</RouterLink>
      <button @click="run" :disabled="busy" class="px-4 py-1.5 rounded bg-black text-white disabled:opacity-50">
        {{ busy ? 'Running…' : 'Run' }}
      </button>
    </div>
    <div ref="editorEl" class="rounded border overflow-hidden"></div>
    <QueryResults v-if="result" :result="result" />
  </div>
</template>
```

- [ ] **Step 6: Commit**
```bash
git add -A && git commit -m "feat(web): axe console — CodeMirror SQL editor + results/messages"
```

---

## Phase 4 — End-to-end

### Task 12: Full-stack smoke + production build

**Files:** Create `MayFly.Web/Dockerfile`, `MayFly.Web/nginx`-less (served by Caddy from `dist`). Update `docker-compose.yml` `web` service (Plan 1 Task 15).

- [ ] **Step 1: Add web to compose (static via Caddy)**

`MayFly.Web/Dockerfile`:
```dockerfile
FROM node:22-alpine AS build
WORKDIR /app
COPY MayFly.Web/package*.json ./
RUN npm ci
COPY MayFly.Web/ ./
RUN npm run build

FROM caddy:2-alpine
COPY --from=build /app/dist /srv
```
Update `Caddyfile` `handle { reverse_proxy web:80 }` → serve static: in dev keep proxy to `web:80` (the web container can run `caddy file-server`), or simplest: have the `caddy` service mount `dist`. Document the chosen wiring in a comment.

- [ ] **Step 2: Run the whole stack**

```bash
cd /Users/fanfan/Documents/App/MayFly
docker compose up -d --build
```
Expected: `caddy`, `web`, `api`, `provisioner`, `metadata-db` all healthy.

- [ ] **Step 3: Walk the four axes in the browser**

Open `http://localhost`:
1. **new** → pick Postgres / 3h / 256 MB / Northwind → Create.
2. **instance** → connection string shown; copy works; snippets render for all 5 langs.
3. **console** → run `SELECT * FROM products;` → results tab shows seeded Northwind rows.
4. **all** → DB alive `1/3`, queries today ≥ 1, storage used > 0, next expiry counts down.

- [ ] **Step 4: Verify external connect + auto-destroy**

```bash
psql "<connection string from instance view>" -c "SELECT count(*) FROM products;"
```
Expected: returns a count. Then verify (after TTL, or by clicking Destroy) the instance disappears from **all** and `psql` can no longer connect.

- [ ] **Step 5: Commit verification**
```bash
git commit --allow-empty -m "test: verified full-stack e2e across all four axes"
```

---

## Self-Review (completed)

**Spec coverage (frontend slice-1):** axe new → Task 8 (engine/ttl/storage/initial data, postgres-only + blank/northwind enabled, rest disabled); axe instance → Task 10 (recap, connection string, snippets bash/python/node/go/.net, destroy); axe console → Task 11 (CodeMirror editor + results + messages); axe all → Task 9 (DB alive/queries today/storage used/next expiry + instances list); stats (storage + queries) surfaced in axes all/instance. Claude Design import → Task 2. Same-origin/cookie/capability-token routing → Tasks 3–5.

**Placeholder scan:** every code step has concrete code; the only intentionally-open step is Task 2 (design import) which is an external MCP action with exact URL + command, not a code placeholder.

**Type consistency:** `InstanceDto`/`QueryResultDto`/`DashboardSummary` in `types.ts` mirror Plan 1 DTOs field-for-field; `buildSnippets` consumes `InstanceDto.connectionString`; `QueryResults` consumes `QueryResultDto` with identical field names used in `ConsoleView`.

**Deferred (sub-projects 2–6, not stubbed in UI):** other engines, other initial-data templates, permissions, schema explorer, activity log, live connections/IO stats, console snippets/shortcuts/plan(EXPLAIN)/history, SignalR.
