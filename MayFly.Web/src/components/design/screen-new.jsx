// Screen: NEW — create a new ephemeral database (Claude Design reference — not compiled).
// Vue target: views/NewView.vue. Layout: container.narrow.
const { useState: useStateN } = React;

function ScreenNew({ tweaks, onCreate }) {
  const [engine, setEngine] = useStateN("postgres");
  const [duration, setDuration] = useStateN("6h");
  const [size, setSize] = useStateN("s");
  const [seed, setSeed] = useStateN("blank");
  const [name, setName] = useStateN("");
  const [readonly, setReadonly] = useStateN(false);

  const e = ENGINES.find(x => x.id === engine);
  const dur = DURATIONS.find(x => x.id === duration);
  const sz = SIZES.find(x => x.id === size);
  const sd = SEEDS.find(x => x.id === seed);

  // placeholder: random "swift-otter"-style name

  return (
    <div className="container narrow">
      {/* Hero: "$ mayfly init" kicker, h1 "Spin up a database.", subtitle, region badge */}
      <div className="row between" style={{ marginBottom: 28 }}>
        <div>
          <div className="upper dim" style={{ fontSize: 11, marginBottom: 6 }}>$ mayfly init</div>
          <h1 style={{ margin: 0, fontSize: 28, fontWeight: 500, letterSpacing: "-.02em" }}>
            Spin up a database<span className="accent">.</span>
          </h1>
          <div className="dim" style={{ marginTop: 8, fontSize: 13 }}>
            Real engines. Real connection string. Self-destructs after the timer hits zero.<br />
            No account. No card. Just a URL you can ship into your code right now.
          </div>
        </div>
        <div className="col g-2" style={{ alignItems: "flex-end" }}>
          <Badge tone="accent" pulse>region fra-1 — online</Badge>
          <div className="dimmer" style={{ fontSize: 11 }}>~3 s to provision</div>
        </div>
      </div>

      {/* Privacy callout — .frame with accent border, "◊ Your data is yours" + ✓ bullets */}
      <div className="frame" style={{ marginBottom: 28, padding: "14px 16px", borderColor: "var(--accent-line)", background: "var(--accent-dim)" }}>
        <span className="crn-bl" /><span className="crn-br" />
        {/* ✓ not used for training/ads · ✓ not shared · ✓ wiped from disk on expiry · ✓ snapshots deleted after 7 days */}
      </div>

      {/* STEP 01 — Engine: grid-3 of .engine-card (glyph, label, v{version} · port {port}, driver://…), selected=accent.
          Postgres selectable; SLICE-1: others disabled (.engine-card.disabled). Last card "+ coming soon". */}
      <Section index="01" label="Engine">
        <div className="grid-3" style={{ gap: 10 }}>
          {ENGINES.map(en => (
            <div className={"engine-card" + (engine === en.id ? " selected" : "")} onClick={() => setEngine(en.id)}>
              <div className="row g-3">
                <div className="engine-logo" style={{ color: en.color }}>{en.glyph}</div>
                <div className="col g-1"><div style={{ fontWeight: 500 }}>{en.label}</div>
                  <div className="dim" style={{ fontSize: 11 }}>v{en.version} · port {en.port}</div></div>
              </div>
              <div className="dim" style={{ fontSize: 11 }}>{en.driver}://…</div>
            </div>
          ))}
        </div>
      </Section>

      {/* STEP 02 — Time-to-live: .duration-row (3 cards) 3h/6h/12h, big label + desc.
          Note under: "↪ Database will be destroyed at <date>. You can extend once, up to 12h/24h total." */}
      <Section index="02" label="Time-to-live">
        <div className="duration-row">
          {DURATIONS.map(d => (
            <div className={"duration-card" + (duration === d.id ? " selected" : "")} onClick={() => setDuration(d.id)}>
              <div style={{ fontSize: 22, fontWeight: 500 }}>{d.label}</div>
              <div className="dim" style={{ fontSize: 11, marginTop: 4 }}>{d.desc}</div>
            </div>
          ))}
        </div>
      </Section>

      {/* STEP 03 — Storage quota: .opt-row (4 opts) 256MB/512MB/1GB/2GB, label + desc.
          Note: "↪ Hard cap. Writes return ENOSPC past the limit. Compute shared/elastic." */}
      <Section index="03" label="Storage quota">
        <div className="opt-row">
          {SIZES.map(s => (
            <div className={"opt" + (size === s.id ? " selected" : "")} onClick={() => setSize(s.id)}
              style={{ padding: "12px", textAlign: "left" }}>
              <span style={{ fontSize: 16, fontWeight: 500 }}>{s.label}</span>
              <div className="dim" style={{ fontSize: 11 }}>{s.desc}</div>
            </div>
          ))}
        </div>
      </Section>

      {/* STEP 04 — Initial data (optional): grid-3 of .opt (blank/E-commerce/Blog/IoT/Northwind/Import dump).
          SLICE-1: only blank + Northwind enabled; others disabled. Import shows a DumpDropzone (deferred). */}
      <Section index="04" label="Initial data" optional>
        <div className="grid-3" style={{ gap: 6 }}>
          {SEEDS.map(s => (
            <div className={"opt" + (seed === s.id ? " selected" : "")} onClick={() => setSeed(s.id)}
              style={{ padding: "10px 12px", textAlign: "left" }}>
              <div style={{ fontSize: 12, fontWeight: 500 }}>{s.id === "import" ? "↑ " : ""}{s.label}</div>
              <div className="dim" style={{ fontSize: 10.5 }}>{s.desc}</div>
            </div>
          ))}
        </div>
      </Section>

      {/* STEP 05 — Name (optional): .input with random placeholder + "also issue read-only share link" checkbox.
          SLICE-1: name/readonly are cosmetic (backend has no name field yet) — keep the input, ignore value or omit. */}
      <Section index="05" label="Name (optional)">
        <input className="input" placeholder="swift-otter" />
      </Section>

      {/* Summary / submit — .frame with corner brackets: left = <pre class=code> summary of choices;
          right = <button class="btn primary lg">▸ provision now ⏎</button> + "or press ⌘⏎". */}
      <div className="frame" style={{ marginTop: 24, marginBottom: 60 }}>
        <span className="crn-bl" /><span className="crn-br" />
        <div className="row between">
          <pre className="code" style={{ background: "transparent", border: "none", padding: 0 }}>
{`> ${e.driver} ${e.version}  storage ${sz.label}
> ttl    ${dur.label}     — destroyed in ${dur.label}
> seed   ${sd.label.toLowerCase()}
> name   ${name || "swift-otter"}`}
          </pre>
          <button className="btn primary lg" onClick={onCreate}>▸ provision now <span className="dimmer">⏎</span></button>
        </div>
      </div>

      {/* Footer: "fair use: 12h max · 3 active per IP · no PII · 42 dbs alive right now" */}
    </div>
  );
}

// Section: index (01..) + label + optional tag + trailing "────" rule
function Section({ index, label, optional, children }) { /* see .row between header */ }
// DumpDropzone: drag/drop file zone (Import dump seed) — deferred to sub-project 3.
