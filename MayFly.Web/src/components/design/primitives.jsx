// Shared UI primitives (Claude Design reference — not compiled). Vue components should mirror these.
const { useState, useEffect, useRef, useMemo, useCallback } = React;

// ASCII mark/logo — the "≈" glyph in an accent box
function Mark({ size = 22 }) {
  return (
    <div className="mark" style={{
      width: size, height: size, display: "grid", placeItems: "center",
      color: "var(--accent)", border: "1px solid var(--accent-line)",
      background: "var(--accent-dim)", fontFamily: "var(--font-mono)",
      fontSize: size <= 22 ? 12 : 14, fontWeight: 700,
    }}>≈</div>
  );
}

function Badge({ children, tone = "default", pulse = false, dot = true }) {
  const cls = "badge" + (tone === "accent" ? " accent" : tone === "warn" ? " warn" : tone === "danger" ? " danger" : "");
  return (
    <span className={cls}>
      {dot && <span className={"dot" + (pulse ? " pulse" : "")} />}
      {children}
    </span>
  );
}

function EngineGlyph({ engine, size = 36 }) {
  const e = ENGINES.find(x => x.id === engine) || ENGINES[0];
  return (
    <div className="engine-logo" style={{ width: size, height: size, fontSize: size * 0.4, color: e.color }}>
      {e.glyph}
    </div>
  );
}

function StatTile({ label, value, sub, trend }) {
  return (
    <div className="stat-tile">
      <div className="label">{label}</div>
      <div className="value num">{value}</div>
      <div className="row between" style={{ alignItems: "flex-end" }}>
        {sub ? <div className="sub">{sub}</div> : <div />}
        {trend && <Sparkline values={trend} width={80} height={20} />}
      </div>
    </div>
  );
}

function Sparkline({ values, width = 200, height = 36, fill = true }) {
  const { line, fill: fillD } = sparkPath(values, width, height, 1);
  return (
    <svg className="spark" viewBox={`0 0 ${width} ${height}`} preserveAspectRatio="none" style={{ width, height }}>
      {fill && <path className="fill" d={fillD} />}
      <path className="line" d={line} />
    </svg>
  );
}

function Countdown({ msLeft, large = false, warn, danger }) {
  const cls = "num " + (danger ? "danger" : warn ? "warn" : "");
  return <span className={cls}>{fmtDuration(msLeft)}</span>;
}

function CopyButton({ value, label = "copy", className = "btn sm" }) {
  const [copied, setCopied] = useState(false);
  const onClick = (e) => {
    e.stopPropagation();
    navigator.clipboard?.writeText(value).catch(() => {});
    setCopied(true);
    setTimeout(() => setCopied(false), 1400);
  };
  return <button className={className} onClick={onClick}>{copied ? "✓ copied" : label}</button>;
}

// Connection string box — colorizes protocol://user:pw@host:port/db
function ConnString({ value, masked = true }) {
  const m = value.match(/^([a-z+]+):\/\/([^:]+):([^@]+)@([^:/]+)(:\d+)?\/(.+)$/i);
  if (!m) return <div className="connstr"><span className="url">{value}</span></div>;
  const [, proto, user, pw, host, port, db] = m;
  const pwShown = masked ? "••••••••" : pw;
  return (
    <div className="connstr">
      <span className="url">
        <span style={{ color: "var(--text-3)" }}>{proto}://</span>
        <span className="user">{user}</span>
        <span style={{ color: "var(--text-3)" }}>:</span>
        <span style={{ color: "var(--warn)" }}>{pwShown}</span>
        <span style={{ color: "var(--text-3)" }}>@</span>
        <span className="host">{host}{port || ""}</span>
        <span style={{ color: "var(--text-3)" }}>/</span>
        <span className="host">{db}</span>
      </span>
      <CopyButton value={value} />
    </div>
  );
}

// Topbar: logo (Mark + "mayfly / <title>") + nav (▸ new / ▸ instance / ▸ console / ▸ all) + region/status/shortcuts
function Topbar({ screen, setScreen, instance, tweaks, onShortcuts }) {
  const ttl = { new: "create", instance: instance ? instance.name : "instance",
    console: instance ? `${instance.name} — sql console` : "sql console", list: "instances" }[screen];
  const navItems = [
    { id: "new", label: "▸ new" },
    { id: "instance", label: "▸ instance" },
    ...(tweaks.showConsole ? [{ id: "console", label: "▸ console" }] : []),
    { id: "list", label: "▸ all" },
  ];
  return (
    <div className="topbar">
      <div className="logo">
        <Mark />
        <span style={{ color: "var(--text)" }}>mayfly</span>
        <span style={{ color: "var(--text-3)" }}>/</span>
        <span style={{ color: "var(--text-2)" }}>{ttl}</span>
      </div>
      <div className="nav">
        {navItems.map(n => (
          <a key={n.id} className={screen === n.id ? "active" : ""} onClick={() => setScreen(n.id)}>{n.label}</a>
        ))}
      </div>
      <div style={{ flex: 1 }} />
      <div className="row g-3" style={{ fontSize: 11, color: "var(--text-3)" }}>
        <span>region <span style={{ color: "var(--text-2)" }}>fra-1</span></span>
        <span>status <span style={{ color: "var(--accent)" }}>● online</span></span>
        <span onClick={onShortcuts} style={{ cursor: "pointer" }} className="dim"><kbd>?</kbd> shortcuts</span>
      </div>
    </div>
  );
}

function useToasts() { /* toast-stack with .toast items, auto-dismiss ~2.4s */ }
function Code({ children, lang = "sh" }) { return <pre className="code">{children}</pre>; }
