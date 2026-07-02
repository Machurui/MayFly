# App shell (from app.jsx) — reference for the Vue App/router

- **Topbar** (see primitives.jsx `Topbar`): logo `≈ mayfly / <title>` + nav `▸ new` `▸ instance` `▸ console` `▸ all` (active link = accent underline) + right side `region fra-1 · status ● online · [?] shortcuts`.
- **Screens** switched by nav: `new` → ScreenNew, `instance` → ScreenInstance, `console` → ScreenConsole, `list` → ScreenList (= our "all" axis).
- In Vue these become routes: `/new`, `/instance/:token`, `/console/:token`, `/` (all). The topbar nav maps to RouterLinks; instance/console links carry the capability token.
- **Modals** (prototype, mostly out of slice-1 scope but nice-to-have): provisioning steps, "instance ready" (shows ConnString), destroy confirmation (`.modal-scrim` + `.modal` with `[ title ]` header + `esc` button), keyboard shortcuts. Header style: `[ <title> ]`, uppercase.
- **Toasts**: `.toast-stack` bottom-left, `.toast` with accent border, auto-dismiss.
- Theme: `<html data-theme="dark">` default. Density default. Font JetBrains Mono.

Slice-1 mapping: build the topbar + 4 routes. Modals/toasts/shortcuts are polish — the wizard's "create" can navigate straight to the instance route (no modal required), but a success toast is a nice touch.
