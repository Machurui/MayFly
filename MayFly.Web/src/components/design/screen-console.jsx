// Screen: CONSOLE — in-browser SQL editor (Claude Design reference — not compiled).
// Vue target: views/ConsoleView.vue. Layout: .console-grid (rows: toolbar / editor / 280px results).
const { useState: useStateC } = React;

function ScreenConsole({ instance, pushToast, tweaks }) {
  const [query, setQuery] = useStateC("-- Sample query\nSELECT 1;");
  const [tab, setTab] = useStateC("results"); // results | messages | plan | history
  const [running, setRunning] = useStateC(false);

  return (
    <div className="console-grid">
      {/* TOOLBAR (.console-toolbar): EngineGlyph(22) + name + id·engine | ▸ run (primary, kbd ⌘⏎, disabled while running)
          | ⌘S save (snippet) | ⌥E explain | clear | right: "schema public · ● idle · ttl <countdown>".
          SLICE-1: keep [▸ run] + [clear]; save/explain are deferred (sub-project 5) — omit or disable. */}
      <div className="console-toolbar">
        <button className="btn primary" onClick={() => {}} disabled={running}>
          {running ? "running…" : "▸ run"} <kbd style={{ marginLeft: 6 }}>⌘⏎</kbd>
        </button>
      </div>

      {/* EDITOR area: grid [210px sidebar | 1fr editor].
          SIDEBAR (.sidebar) — DEFERRED (sub-project 5): "tables" list, "snippets" list, "shortcuts". SLICE-1: omit sidebar
          (editor spans full width) OR leave a thin placeholder.
          EDITOR: design uses a line-numbered textarea overlay with a tiny SQL highlighter. SLICE-1 uses CodeMirror 6
          (@codemirror/lang-sql, PostgreSQL dialect) mounted in a div — gives real highlighting + line numbers. */}
      <div style={{ overflow: "auto", background: "var(--bg-2)" }}>
        {/* <div ref="editorEl" /> — CodeMirror instance */}
      </div>

      {/* RESULTS pane (.console-results): tab bar buttons "▸ results (N)" / "▸ messages" / "▸ plan" / "▸ history"
          (active tab = accent + 2px accent bottom-border). Right of tabs: "N rows · Xms exec · C cols · ↓csv ↓json".
          SLICE-1 tabs: results + messages ONLY (plan=EXPLAIN + history are sub-project 5).
          - results: <ResultsTable> — table.tbl, header "# | col<type> | …", numeric cells .num.
                     Empty/running states: .empty "no results yet — hit ⌘⏎ to run" / blink "running query…".
          - messages: <pre> status lines (server, rows returned, exec time, error). SLICE-1: show QueryResultDto.message
                     or .error (danger) + "N row(s) · Xms".
          Feed from POST /api/instances/{token}/query -> QueryResultDto { success, columns, rows, rowCount, durationMs, message, error }. */}
      <div className="console-results" style={{ display: "flex", flexDirection: "column" }}>
        {/* tab bar + <QueryResults :result="result" /> */}
      </div>
    </div>
  );
}

// ResultsTable: table.tbl, first col "#" row index (dimmer, right), then columns with lowercase type hint.
//   Numeric-looking cells get .num. SLICE-1: columns/rows from QueryResultDto (no type hints -> omit the type span).
// ExplainPlan / HistoryList / SNIPPETS (large per-engine snippet library) — DEFERRED to sub-project 5.
// highlightSQL: prototype's regex highlighter — replaced by CodeMirror in the Vue app.
