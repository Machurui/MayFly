// Screen: LIST — all my active ephemeral databases (Claude Design reference — not compiled).
// Vue target: views/AllView.vue (our "all" axis). Layout: container.wide.
const { useState: useStateL, useMemo: useMemoL } = React;

function ScreenList({ onPick, onNew, pushToast, listState }) {
  useTicker(1000); // live countdowns

  const items = INSTANCES; // real Vue app: useMyInstances() from /api/instances
  const totalStorage = items.reduce((s, i) => s + i.storage, 0);
  const totalQueries = items.reduce((s, i) => s + i.queries, 0);

  return (
    <div className="container wide" style={{ paddingTop: 18 }}>
      {/* Hero: "$ mayfly ls --mine" kicker, h1 "Your instances  <N active[, M expired]>",
          right: filter input + "+ new database" primary button */}
      <div className="row between" style={{ marginBottom: 18 }}>
        <div>
          <div className="upper dim" style={{ fontSize: 11, marginBottom: 4 }}>$ mayfly ls --mine</div>
          <h1 style={{ margin: 0, fontSize: 22, fontWeight: 500 }}>
            Your instances <span className="dim" style={{ fontSize: 14 }}>{items.length} active</span>
          </h1>
        </div>
        <div className="row g-2">
          <input className="input" placeholder="filter / fzf …" style={{ width: 220 }} />
          <button className="btn primary" onClick={onNew}>+ new database</button>
        </div>
      </div>

      {/* Stat row — grid-4 of StatTile: "alive" (value N, sub "of 3 quota") · "total queries today" ·
          "storage used" (fmtBytes, sub "across instances") · "next expiry" (fmtDuration min, sub name).
          SLICE-1: fed by GET /api/dashboard (aliveCount/maxAlive, queriesToday, storageUsedBytes, nextExpiry). */}
      <div className="grid-4" style={{ marginBottom: 18 }}>
        <StatTile label="alive" value={items.length} sub="of 3 quota" />
        <StatTile label="total queries today" value={totalQueries.toLocaleString()} />
        <StatTile label="storage used" value={fmtBytes(totalStorage)} sub="across instances" />
        <StatTile label="next expiry" value={fmtDuration(/* min msLeft */ 0)} sub={items[0]?.name} />
      </div>

      {/* Empty state (no instances): .empty with "≈" glyph, "No databases here yet.",
          "instances are tied to this browser", and a "▸ create your first database" button. */}

      {/* Instances table — .card > .hd "[ instances ]" + sort buttons; table.tbl columns:
          [status dot] name(+id) | engine(glyph+label+ver) | storage | queries | used | created | expires in | actions.
          Row dot: accent=alive, warn=expiring(<10m), text-4=expired. Row click -> open instance (onPick).
          "expires in" colored: accent / warn / dimmer(expired). Actions: "⧉ url" (copy), "open ▸".
          SLICE-1 columns available from InstanceDto: name(dbName)/id(token short)/engine/storage(lastSizeBytes)
          /created(createdAt)/expires(expiresAt via timeUntil). "queries"/"used" per-instance are sub-project 4 — omit or 0. */}
      <div className="card">
        <div className="hd"><span className="ttl">[ instances ]</span></div>
        <table className="tbl">
          <thead><tr><th></th><th>name</th><th>engine</th><th>storage</th><th>created</th><th>expires in</th><th></th></tr></thead>
          <tbody>
            {items.map(i => (
              <tr onClick={() => onPick(i)}>
                <td><span className="dot" /></td>
                <td>{i.name} <span className="dimmer">{i.id}</span></td>
                <td>{ENGINES.find(e => e.id === i.engine)?.label}</td>
                <td className="dim">{SIZES.find(s => s.id === i.size)?.label}</td>
                <td className="dim">{fmtRelative(Date.now() - i.createdAt)} ago</td>
                <td className="accent">{fmtDuration(i.expiresAt - Date.now())}</td>
                <td><button className="btn sm ghost">open ▸</button></td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {/* Footer notes: "↪ instances scoped to this browser" / "↪ snapshots kept 7 days" */}
    </div>
  );
}
