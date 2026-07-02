// Screen: INSTANCE — dashboard for an active ephemeral DB (Claude Design reference — not compiled).
// Vue target: views/InstanceView.vue. Layout: container.wide. useTicker(1000) for live countdown.
const { useState: useStateI, useMemo: useMemoI } = React;

function ScreenInstance({ instance, state, onGoConsole, onDestroy, onExtend, pushToast, tweaks }) {
  // state: active | expiring | expired | empty  (drives badge tone + progress color)
  // SLICE-1 map: state derived from InstanceDto.state + timeUntil(expiresAt); empty -> not applicable (route has token).

  return (
    <div className="container wide" style={{ paddingTop: 18 }}>
      {/* HERO row: EngineGlyph(48) + name(dbName) + id(token short) + <Badge tone=state>{stateLabel}</Badge>;
          sub line "PostgreSQL v16 · storage <quota> · region fra-1 · seed <initialData>".
          Right actions (state active): [+ extend 6h] (deferred), [▸ sql console] (primary, -> /console/:token),
          [destroy now] (btn danger -> DELETE). SLICE-1: keep "sql console" + "destroy"; "extend" deferred. */}

      {/* COUNTDOWN frame (.frame corner brackets): left = kicker "self-destruct in" + .big-num fmtDuration(msLeft)
          + "created Xm ago · ttl <h> · auto-destroys at <date>"; right = "<pct>% lifetime remaining" + .progress bar
          (progress / progress.warn / progress.danger by state) + 0h..<ttl> ends. SLICE-1: msLeft = expiresAt - now. */}

      <div className="grid-2" style={{ alignItems: "start" }}>
        {/* LEFT column */}
        <div className="col g-3">
          {/* Card "connection string" (badge "private to you"): <ConnString value={connStr} /> +
              grid-2 of KV rows: host / port / database(dbName) / user(dbUser) / password(masked, copy) / ssl.
              SLICE-1: connectionString from InstanceDto (postgresql://user:pass@host:port/db); parse for host/port/db/user. */}
          <Card title="connection string" badge={<span className="dimmer">private to you</span>}>
            <ConnString value={instance.connStr} />
            {/* KV grid: host, port, database, user, password(••••, copy real), ssl=required */}
          </Card>

          {/* Card "connect from your code" with TABS. Design tabs: bash / python / node / go.
              SLICE-1 REQUIRES 5 tabs: bash / python / node / go / .net (dotnet). Use the ConnectionSnippets component
              (buildSnippets from connectionString). Tab button style: active = surface-3 bg + border-3 + text. */}
          <Card title="connect from your code" tabs={["bash","python","node","go",".net"]}>
            {/* <ConnectionSnippets :instance="inst" /> — pre in .code with a CopyButton top-right */}
          </Card>

          {/* Card "permissions" — DEFERRED (sub-project 4). Rows: PermRow ok/no ✓/✗ with grants list. */}
        </div>

        {/* RIGHT column */}
        <div className="col g-3">
          {/* grid-2 of StatTile: queries · storage(of quota) · connections · io throughput, each with a Sparkline trend.
              SLICE-1: only STORAGE is real (lastSizeBytes of storageQuotaMb) — show it with a progress-toward-quota.
              queries/connections/io throughput + sparkline trends are sub-project 4 -> show "—" or omit. */}
          <div className="grid-2" style={{ gap: 8 }}>
            <StatTile label="storage" value={fmtBytes(instance.storage)} sub={`of ${/*quota*/""}`} />
          </div>

          {/* Card "schema" (table: table/rows/columns) — DEFERRED (sub-project 4, needs schema explorer). */}
          {/* Card "activity log" (table ts/level/elapsed/text) — DEFERRED (sub-project 4). */}
        </div>
      </div>
    </div>
  );
}

// Card: .card > .hd "[ <title> ]" + (tabs row | badge); .bd body. tabs render prop (tab index).
// KV: dashed-underline row, uppercase key + value (+ optional CopyButton).
// PermRow: ✓ accent / ✗ danger (line-through) / · neutral + label.
// CodeSnippet: per-lang connection code in <pre class=code> + CopyButton. SLICE-1 langs incl .net (Npgsql).
// EmptyState: .empty "≈ No instance selected" + "▸ create new database" — N/A for slice-1 (route carries token).
