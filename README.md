<div align="center">

<img src="assets/logo.png" width="120" alt="Prism logo" />

# Prism

### Launch & manage every Roblox alt — in one window.

Free and **open source**. Grind with your alts, drop them into the **same server to trade**, browse games in‑app, and theme it your way.

[![Download](https://img.shields.io/badge/⬇_Download-Prism.exe-7c6aff?style=for-the-badge)](https://github.com/Cosmic4796/Prism/releases/latest/download/Prism.exe)
&nbsp;
[![Discord](https://img.shields.io/badge/Discord-Support-5865F2?style=for-the-badge&logo=discord&logoColor=white)](https://discord.gg/CZNm9B8JqY)

[Website](https://cosmic4796.github.io/Prism) ·
![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D6?logo=windows) ·
![License](https://img.shields.io/badge/license-MIT-green) ·
![Downloads](https://img.shields.io/github/downloads/Cosmic4796/Prism/total)

<img src="assets/hero.png" width="760" alt="Prism" />

</div>

---

## ✨ Demo

<div align="center">
<img src="assets/demo.gif" width="760" alt="Prism demo" />
</div>

## 🚀 Features

- **Launch many accounts at once** — open all your alts with one click (true multi‑instance, no third‑party patches).
- **Same‑server trading** — find a server and drop several alts into it together (public or private servers).
- **Works with your bootstrapper** — launch through Bloxstrap, Fishstrap, Froststrap, or the official client.
- **In‑app game browser** — search & browse Roblox games and launch straight into them.
- **Live account info** — Robux balance, Premium badge, and cookie health at a glance.
- **Auto‑rejoin / keep‑alive** — reopens a client if it closes or crashes (great for AFK grinding).
- **Discord Rich Presence** — show "Playing Prism" on your profile (toggleable).
- **Bulk import** — paste or load a file of cookies to add accounts in seconds.
- **Fully themeable** — accent colors, roundness, glass, blur, fonts, and saveable themes.
- **Private & local** — cookies are encrypted on your PC and only ever sent to roblox.com.

## 🖼️ Screenshots

<table>
  <tr>
    <td><img src="assets/accounts.png" alt="Accounts" /></td>
    <td><img src="assets/discover.png" alt="Discover" /></td>
  </tr>
  <tr>
    <td><img src="assets/analytics.png" alt="Analytics" /></td>
    <td><img src="assets/settings.png" alt="Settings" /></td>
  </tr>
</table>

## 🧭 Getting started

1. **Open Prism _before_ any Roblox window** (so it can enable multi‑instance).
2. **Add accounts** — *Log in with Roblox* (real login in an embedded window — password/2FA/captcha all work), *Paste cookie*, or *Import* a list.
3. **Pick a Place ID** (the number in `roblox.com/games/<ID>`) or choose a game from **Discover**.
4. **Grind:** tick your alts → **Launch ▶**.
5. **Trade together:** **Find servers** and pick one (or paste a private‑server link) → tick your alts → **Launch ▶** — they all land in the same server.

## ⬇️ Download

1. Grab the latest **[Prism.exe](https://github.com/Cosmic4796/Prism/releases/latest/download/Prism.exe)** from [Releases](https://github.com/Cosmic4796/Prism/releases/latest).
2. Run it — no installer, no .NET install needed (self‑contained build).

> **SmartScreen:** Prism is unsigned, so Windows may show *"Windows protected your PC."* → **More info → Run anyway**. Some antivirus may heuristically false‑positive because the app reads cookies + launches processes — which is exactly what it does, in the open. Don't trust the binary? **Build it yourself** below.
>
> **Requires** the free [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) (already on most Windows 11).

## 🛠️ Build from source

Compile Prism yourself and get the exact same app as the release.

**You'll need:** Windows 10/11 and the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```sh
git clone https://github.com/Cosmic4796/Prism.git
cd Prism
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

Your `Prism.exe` lands in `bin\Release\net8.0-windows\win-x64\publish\`.

## 🔒 Safety & Privacy

Prism handles Roblox cookies, so here's exactly how it treats them — and it's open source, so you can verify it:

- 🔐 Your `.ROBLOSECURITY` cookies are **encrypted on your PC** with Windows **DPAPI** (bound to your Windows user — they can't be copied to another machine).
- 🌐 They are **only ever sent to roblox.com** (to validate accounts and start the game) — **never to us or any third party.**
- 📡 **No telemetry, no accounts, no servers** — Prism talks to Roblox and nothing else.
- 🙅 **Never share your cookie with anyone.** It's full access to your account; **no one** — including "staff" — should ever ask for it.

## ⚠️ Known limitations

- Roblox doesn't *officially* support running multiple clients; the technique can be changed or blocked by a Roblox update at any time.
- A Roblox‑client regression makes in‑game **`TeleportService`** teleports work only for the most‑recently‑launched client.
- Roblox occasionally **rate‑limits (HTTP 429)** rapid launches; Prism staggers them and tells you if it happens.

## 💬 Support

Need help or found a bug? Join the **[Prism Support Discord](https://discord.gg/CZNm9B8JqY)** — post in `#get-help`, or check `#faq` first.

## ⚠️ Disclaimer

Prism is an independent, fan‑made tool, **not affiliated with or endorsed by Roblox Corporation**. Running multiple accounts may violate Roblox's Terms of Service — **use at your own risk**; you are responsible for your own accounts.

## 📄 License

[MIT](LICENSE) — free to use, modify, and build. If you fork it, please don't pass your build off as the official Prism.
