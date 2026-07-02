<script setup lang="ts">
const model = defineModel<string>({ required: true })

const engines = [
  { id: 'postgres',  label: 'PostgreSQL', glyph: 'PG', version: '17.5', port: 5432, driver: 'postgresql', color: '#4ade80', enabled: true },
  { id: 'mysql',     label: 'MySQL',      glyph: 'MY', version: '8.4',  port: 3306, driver: 'mysql',      color: '#60a5fa', enabled: false },
  { id: 'mariadb',   label: 'MariaDB',    glyph: 'MB', version: '11.4', port: 3306, driver: 'mariadb',    color: '#f59e0b', enabled: false },
  { id: 'mongodb',   label: 'MongoDB',    glyph: 'MG', version: '7.0',  port: 27017,driver: 'mongodb',    color: '#22c55e', enabled: false },
  { id: 'sqlserver', label: 'SQL Server', glyph: 'SS', version: '22',   port: 1433, driver: 'sqlserver',  color: '#e11d48', enabled: false },
]
</script>

<template>
  <div class="grid-3" style="gap: 10px;">
    <div
      v-for="e in engines"
      :key="e.id"
      :class="['engine-card', model === e.id && 'selected', !e.enabled && 'disabled']"
      @click="e.enabled && (model = e.id)"
    >
      <div class="row g-3">
        <div class="engine-logo" :style="{ color: e.color }">{{ e.glyph }}</div>
        <div class="col g-1">
          <div style="font-weight: 500;">{{ e.label }}</div>
          <div class="dim" style="font-size: 11px;">v{{ e.version }} · port {{ e.port }}</div>
        </div>
      </div>
      <div class="dim" style="font-size: 11px;">{{ e.driver }}://…</div>
    </div>
  </div>
</template>
