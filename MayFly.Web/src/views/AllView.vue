<script setup lang="ts">
import { RouterLink, useRouter } from 'vue-router'
import { useDashboard, useMyInstances } from '../api/instances'
import { formatBytes, timeUntil } from '../lib/format'
import StatTile from '../components/StatTile.vue'

const router = useRouter()
const { data: d } = useDashboard()
const { data: instances } = useMyInstances()

function timeAgo(iso: string): string {
  const ms = Date.now() - new Date(iso).getTime()
  if (ms < 60_000) return 'just now'
  const h = Math.floor(ms / 3_600_000)
  const m = Math.floor((ms % 3_600_000) / 60_000)
  return h > 0 ? `${h}h ${m}m ago` : `${m}m ago`
}

function shortToken(token: string): string {
  return token.slice(0, 8)
}

function dotClass(state: string): string {
  if (state === 'running') return 'accent'
  return 'dimmer'
}
</script>

<template>
  <div class="page">
    <div class="container wide" style="padding-top: 18px">

      <!-- Hero -->
      <div class="row between" style="margin-bottom: 18px">
        <div>
          <div class="upper dim" style="font-size: 11px; margin-bottom: 4px">$ mayfly ls --mine</div>
          <h1 style="margin: 0; font-size: 22px; font-weight: 500">
            Your instances
            <span class="dim" style="font-size: 14px">{{ d?.aliveCount ?? 0 }} active</span>
          </h1>
        </div>
        <RouterLink to="/new" class="btn primary">+ new database</RouterLink>
      </div>

      <!-- Stat row -->
      <div class="grid-4" style="margin-bottom: 18px">
        <StatTile
          label="alive"
          :value="String(d?.aliveCount ?? 0)"
          :sub="`of ${d?.maxAlive ?? 3} quota`"
        />
        <StatTile
          label="total queries today"
          :value="(d?.queriesToday ?? 0).toLocaleString()"
        />
        <StatTile
          label="storage used"
          :value="formatBytes(d?.storageUsedBytes ?? 0)"
          sub="across instances"
        />
        <StatTile
          label="next expiry"
          :value="d?.nextExpiry ? timeUntil(d.nextExpiry) : '—'"
        />
      </div>

      <!-- Empty state (loaded, no instances) -->
      <div v-if="instances && instances.length === 0" class="empty">
        <div style="font-size: 32px; margin-bottom: 12px">≈</div>
        <div style="margin-bottom: 6px">No databases here yet.</div>
        <div class="dimmer" style="font-size: 12px; margin-bottom: 20px">instances are tied to this browser</div>
        <RouterLink to="/new" class="btn primary lg">▸ create your first database</RouterLink>
      </div>

      <!-- Instances card -->
      <div v-else-if="instances && instances.length > 0" class="card">
        <div class="hd"><span class="ttl">[ instances ]</span></div>
        <table class="tbl">
          <thead>
            <tr>
              <th></th>
              <th>name</th>
              <th>engine</th>
              <th>storage</th>
              <th>created</th>
              <th>expires in</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            <tr
              v-for="i in instances"
              :key="i.token"
              @click="router.push('/instance/' + i.token)"
            >
              <td>
                <span
                  :class="dotClass(i.state)"
                  style="display: inline-block; width: 8px; height: 8px; border-radius: 50%; background: currentColor"
                />
              </td>
              <td>
                {{ i.dbName }}
                <span class="dimmer">{{ shortToken(i.token) }}</span>
              </td>
              <td>{{ i.engine }}</td>
              <td class="dim">{{ formatBytes(i.lastSizeBytes) }}</td>
              <td class="dim">{{ timeAgo(i.createdAt) }}</td>
              <td class="accent">{{ timeUntil(i.expiresAt) }}</td>
              <td>
                <RouterLink
                  :to="'/instance/' + i.token"
                  class="btn sm ghost"
                  @click.stop
                >open ▸</RouterLink>
              </td>
            </tr>
          </tbody>
        </table>
      </div>

    </div>
  </div>
</template>
