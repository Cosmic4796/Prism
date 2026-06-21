# Prism — Roadmap (planning doc)

Current: **v1.0.1** (new private-server share links shipped).
Effort key: 🟢 small · 🟡 medium · 🔴 large · 💲 cost (not code)

---

## v1.2 — "Client Control" (headline multi-client update) ⭐
**Foundation — Launched-client registry** 🟡
Generalize the auto-rejoin PID capture so *every* launched account is tracked
(account → PID → window handle), regardless of whether auto-rejoin is on.
Reuse `CapturePidAsync` / `RobloxProcesses` in `AppShell`. This single piece unlocks
the next three features.

- **Name each client window** 🟡 ⭐ — after a client's PID is captured, find its main
  window and `SetWindowText(hwnd, "<alias> — Prism")` (Win32 P/Invoke; we already
  P/Invoke user32). Re-apply on a short timer in case Roblox overwrites the title.
  *Solves the #1 multi-account pain: every window says "Roblox."*
- **Running panel + status dots + uptime + "Close all"** 🟢 — UI panel listing tracked
  clients (alias + live dot + **uptime**); "Close all" kills the tracked
  `RobloxPlayerBeta` PIDs. Bridge pushes the running list (with each client's process
  StartTime); the web UI shows a **live-ticking uptime** counter per client (e.g.
  "2h 14m") computed client-side so it updates every second without spamming the bridge.
  *Later:* feed totals into Analytics (total time today / longest session).
- **Relaunch last session** 🟢 — persist the last launch batch (userIds + placeId +
  server/private) to `settings.json`; a button re-runs `DoLaunchAsync` with it.

## v1.2.x — Window tiling
- **Auto-tile / arrange clients** 🟡 ⭐ — `SetWindowPos` each tracked window into a grid
  over the monitor work area (count → rows×cols). Optional layouts (grid / side-by-side).
  Depends on the registry + hwnd lookup from v1.2.

## v1.3 — Quality of life
- **Favorites/pin + search/filter + drag-reorder** 🟡 — web UI: search box, star toggle,
  draggable cards. Persist favorite IDs + custom order in `settings.json`; `render()`
  honors them. (Matters once people have 20+ alts.)
- **Launch history timeline + most-played** 🟡 — append timestamped entries to
  `stats.json` (bounded, e.g. last 500); Analytics pane renders a timeline + top games
  (partly there already).
- **Compact / dense layout mode** 🟢 — settings toggle → `body` class that tightens
  paddings/sizes (pure CSS).
- **Custom background image** 🟡 — settings file-picker → C# reads the image, base64 →
  push to JS → set as `body` background; persist the choice.

## v1.4 — Updates & trust
- **Update checker + in-app changelog** 🟡 ⭐ **(keystone — do this early so all later
  updates actually reach users)** — new `UpdateChecker` service: GET
  `api.github.com/repos/Cosmic4796/Prism/releases/latest` (plain HttpClient, no cookie),
  compare the tag to the csproj `Version`; if newer, show a banner/toast with the release
  notes (changelog) + a Download button.
- **Crash / error log export** 🟢 — file logger to `%APPDATA%\Prism\logs\` (rotating),
  hooked to the existing global exception handlers; Settings → "Export logs" opens/zips
  the folder (feeds #get-help).
- **Code signing** 💲 — buy a cert, sign `Prism.exe` at publish (signtool). Removes the
  SmartScreen warning. Infra task, no dev-build testing.

## v1.5 — Community & reach
- **Theme export/import + gallery** 🟡 — export current theme → shareable base64 string;
  paste-to-import; gallery browses a curated themes JSON (raw GitHub or bundled) to
  apply. Ties into the #showcase channel.
- **Localization (i18n)** 🔴 — extract UI strings to keyed JSON, one file per language,
  load by setting; also localize the C# user-facing messages. Biggest effort, lowest
  urgency (English-first), so last.

---

## 🔨 Build first in a dev build (best value-to-effort, several reuse existing code)
1. **Update checker + changelog** (keystone; independent; safe)
2. **Relaunch last session** (trivial; high daily use)
3. **Running panel + "Close all"** (reuses PID tracking)
4. **Name each client window** (the standout differentiator)

Then: window tiling → favorites/search → the rest.
