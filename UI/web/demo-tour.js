/* Prism demo auto-tour — injected via ExecuteScriptAsync only when PRISM_DEMO is set.
   Drives the REAL UI with scripted placeholder data (no backend, no real accounts).
   Captured live via DevTools screencast (real frames, no interpolation).
   Includes animated lower-third captions + an end card for a polished showcase. */
(async () => {
  const sleep = ms => new Promise(r => setTimeout(r, ms));
  const Q = s => document.querySelector(s);

  // ---- caption / end-card overlay (styled to match Prism) -----------------
  const style = document.createElement('style');
  style.textContent = `
    #demo-cap{position:fixed;left:50%;bottom:52px;transform:translate(-50%,14px);z-index:900;
      display:flex;align-items:center;gap:13px;padding:13px 22px 13px 18px;border-radius:14px;
      background:rgba(12,13,18,.74);border:1px solid rgba(255,255,255,.12);
      -webkit-backdrop-filter:blur(18px);backdrop-filter:blur(18px);
      box-shadow:0 22px 55px -18px rgba(0,0,0,.85);
      opacity:0;transition:opacity .45s ease,transform .45s cubic-bezier(.2,.8,.3,1);white-space:nowrap;pointer-events:none}
    #demo-cap.show{opacity:1;transform:translate(-50%,0)}
    #demo-cap .bar{width:4px;height:32px;border-radius:3px;background:linear-gradient(180deg,var(--accent),var(--accent2))}
    #demo-cap .eb{font:700 10.5px var(--font);letter-spacing:.16em;text-transform:uppercase;color:var(--accent2)}
    #demo-cap .ln{font:700 19px var(--font);color:#fff;letter-spacing:-.2px;margin-top:2px}
    @keyframes capIn{from{opacity:0;transform:translateY(7px)}to{opacity:1;transform:none}}
    #demo-end{position:fixed;inset:0;z-index:950;display:flex;align-items:center;justify-content:center;
      background:radial-gradient(620px 620px at 50% 42%,color-mix(in srgb,var(--accent) 22%,transparent),transparent 70%),rgba(8,9,12,.9);
      -webkit-backdrop-filter:blur(10px);backdrop-filter:blur(10px);opacity:0;transition:opacity .6s ease;pointer-events:none}
    #demo-end.show{opacity:1}
    #demo-end .ec-in{display:flex;flex-direction:column;align-items:center;gap:14px;text-align:center;
      transform:scale(.96);opacity:0;transition:.7s cubic-bezier(.2,.8,.3,1) .1s}
    #demo-end.show .ec-in{transform:none;opacity:1}
    #demo-end .ec-word{font:800 56px var(--font);letter-spacing:-.5px;
      background:linear-gradient(95deg,var(--accent),var(--accent2) 55%,#ec4899);-webkit-background-clip:text;background-clip:text;color:transparent}
    #demo-end .ec-tag{font:500 16.5px var(--font);color:var(--sub)}`;
  document.head.appendChild(style);

  const cap = document.createElement('div');
  cap.id = 'demo-cap';
  cap.innerHTML = '<div class="bar"></div><div><div class="eb"></div><div class="ln"></div></div>';
  document.body.appendChild(cap);

  const endcard = document.createElement('div');
  endcard.id = 'demo-end';
  endcard.innerHTML = `<div class="ec-in">
    <svg viewBox="0 0 100 100" width="80" height="80">
      <polygon points="52,16 84,84 18,84" fill="url(#tg)" stroke="#cdd0ff" stroke-width="3" stroke-linejoin="round"/>
      <line x1="4" y1="50" x2="34" y2="52" stroke="#fff" stroke-width="4"/>
      <line x1="55" y1="51" x2="98" y2="40" stroke="#ff5a5a" stroke-width="2.6"/>
      <line x1="55" y1="51" x2="98" y2="52" stroke="#ffe14d" stroke-width="2.6"/>
      <line x1="55" y1="51" x2="98" y2="64" stroke="#46e08a" stroke-width="2.6"/>
      <line x1="55" y1="51" x2="98" y2="78" stroke="#4aa3ff" stroke-width="2.6"/>
      <line x1="55" y1="51" x2="98" y2="90" stroke="#9d6bff" stroke-width="2.6"/></svg>
    <div class="ec-word">Prism</div>
    <div class="ec-tag">Launch &amp; manage every Roblox alt — in one window</div></div>`;
  document.body.appendChild(endcard);

  function caption(eb, ln) {
    cap.querySelector('.eb').textContent = eb;
    cap.querySelector('.ln').textContent = ln;
    cap.classList.add('show');
    const t = cap.children[1];
    t.style.animation = 'none'; void t.offsetWidth; t.style.animation = 'capIn .45s ease';
  }
  const hideCaption = () => cap.classList.remove('show');

  // smooth, controllable scroll
  function smoothScroll(el, to, ms) {
    return new Promise(res => {
      const start = el.scrollTop, d = to - start, t0 = performance.now();
      (function step(now) {
        const k = Math.min(1, (now - t0) / ms);
        const e = k < .5 ? 2 * k * k : 1 - Math.pow(-2 * k + 2, 2) / 2;
        el.scrollTop = start + d * e;
        if (k < 1) requestAnimationFrame(step); else res();
      })(performance.now());
    });
  }

  // ---- placeholder art (offline canvas gradients) -------------------------
  function avatar(name, c1, c2) {
    const s = 120, cv = document.createElement('canvas'); cv.width = cv.height = s;
    const x = cv.getContext('2d'), g = x.createLinearGradient(0, 0, s, s);
    g.addColorStop(0, c1); g.addColorStop(1, c2); x.fillStyle = g; x.fillRect(0, 0, s, s);
    const i = name.replace(/[^A-Za-z0-9]/g, '').slice(0, 2).toUpperCase();
    x.fillStyle = 'rgba(255,255,255,.96)'; x.font = '700 54px Inter,Segoe UI,sans-serif';
    x.textAlign = 'center'; x.textBaseline = 'middle'; x.fillText(i, s / 2, s / 2 + 3);
    return cv.toDataURL('image/png');
  }
  function icon(emoji, c1, c2) {
    const s = 168, cv = document.createElement('canvas'); cv.width = cv.height = s;
    const x = cv.getContext('2d'), g = x.createLinearGradient(0, 0, s, s);
    g.addColorStop(0, c1); g.addColorStop(1, c2); x.fillStyle = g; x.fillRect(0, 0, s, s);
    x.font = '92px "Segoe UI Emoji",serif'; x.textAlign = 'center'; x.textBaseline = 'middle';
    x.fillText(emoji, s / 2, s / 2 + 6); return cv.toDataURL('image/png');
  }

  // ---- placeholder data ---------------------------------------------------
  const ACCTS = [
    { userId: 1001, alias: 'MainGrind', username: 'MainGrind',  displayName: 'MainGrind', robux: 45120,  premium: true,  c1: '#7c6aff', c2: '#9d6bff' },
    { userId: 1002, alias: 'AltTrader', username: 'AltTrader2', displayName: 'AltTrader', robux: 8240,   premium: false, c1: '#5ab0ff', c2: '#22d3ee' },
    { userId: 1003, alias: 'FarmBot',   username: 'FarmBot_x',  displayName: 'FarmBot',   robux: 132990, premium: true,  c1: '#4ade9e', c2: '#22d3ee' },
    { userId: 1004, alias: 'QuietAlt',  username: 'QuietAlt',   displayName: 'QuietAlt',  robux: 540,    premium: false, c1: '#ff9248', c2: '#ff6eb2' },
    { userId: 1005, alias: 'SoloMule',  username: 'SoloMule',   displayName: 'SoloMule',  robux: 21030,  premium: false, c1: '#ff6eb2', c2: '#9d6bff' },
    { userId: 1006, alias: 'GrindKing', username: 'GrindKing',  displayName: 'GrindKing', robux: 76400,  premium: true,  c1: '#22d3ee', c2: '#5ab0ff' },
    { userId: 1007, alias: 'PetFarmer', username: 'PetFarmer_', displayName: 'PetFarmer', robux: 3110,   premium: false, c1: '#4ade9e', c2: '#7c6aff' },
    { userId: 1008, alias: 'TradeAlt',  username: 'TradeAlt99', displayName: 'TradeAlt',  robux: 58900,  premium: false, c1: '#ff9248', c2: '#ffe14d' },
  ];
  ACCTS.forEach(a => a.avatar = avatar(a.alias, a.c1, a.c2));

  const G = (name, emoji, playing, c1, c2, placeId) => ({ placeId, name, playing, icon: icon(emoji, c1, c2) });
  const POPULAR = [
    G('Grow a Garden',    '🌱', 1320000, '#4ade9e', '#22d3ee', 126884695634066),
    G('Adopt Me!',        '🐾', 154200,  '#ff6eb2', '#9d6bff', 920587237),
    G('Blox Fruits',      '🍈', 78231,   '#ff9248', '#ff6eb2', 2753915549),
    G('Pet Simulator 99', '🐶', 92040,   '#5ab0ff', '#22d3ee', 8737899170),
    G('Brookhaven 🏡 RP',  '🏡', 412300,  '#7c6aff', '#5ab0ff', 4924922222),
    G('Blade Ball',       '⚔️', 64110,   '#9d6bff', '#7c6aff', 13772394625),
    G('Murder Mystery 2', '🔪', 41880,   '#ff6b78', '#ff9248', 142823291),
    G('Jailbreak',        '🚓', 28760,   '#5ab0ff', '#4ade9e', 606849621),
  ];
  const TRADING = [
    G('Adopt Me!',        '🐾', 154200,  '#ff6eb2', '#9d6bff', 920587237),
    G('Pet Simulator 99', '🐶', 92040,   '#5ab0ff', '#22d3ee', 8737899170),
    G('Murder Mystery 2', '🔪', 41880,   '#ff6b78', '#ff9248', 142823291),
    G('Trade Hangout',    '💱', 5240,    '#4ade9e', '#5ab0ff', 808530300),
  ];

  // ---- helpers tied to the app's own functions ----------------------------
  function seedAccounts() {
    accounts = ACCTS.map(a => ({ ...a, health: undefined, robux: null, premium: false }));
    render();
  }
  async function typeInto(sel, text, per = 75) {
    const el = Q(sel); el.value = ''; el.focus();
    for (const ch of text) { el.value += ch; await sleep(per); }
  }
  window.loadStats = async function () {
    Q('#stAccounts').textContent = '8';
    Q('#stLaunches').textContent = '214';
    Q('#stTop').textContent = 'Adopt Me!';
    bars('#topGames', [
      { label: 'Adopt Me!', count: 74 }, { label: 'Blox Fruits', count: 52 },
      { label: 'Pet Simulator 99', count: 33 }, { label: 'Murder Mystery 2', count: 19 },
    ]);
    bars('#topAccts', [
      { label: 'MainGrind', count: 58 }, { label: 'FarmBot', count: 47 },
      { label: 'GrindKing', count: 39 }, { label: 'TradeAlt', count: 31 },
      { label: 'AltTrader', count: 22 }, { label: 'SoloMule', count: 17 },
    ]);
    Q('#genInfo').innerHTML = 'Multi-instance: <b>ON</b><br>Saved accounts: <b>8</b><br>Total launches: <b>214</b>';
  };
  const navTo = t => Q(`.nav[data-tab="${t}"]`).click();

  // ===== choreography (brisk, readable, captioned) =========================
  setStatus(true, 'Multi-instance: ON');
  seedAccounts();
  logLine('Prism ready.');
  logLine('Multi-instance enabled (singleton lock held).');
  logLine('Loaded 8 accounts.');

  await sleep(2200);
  Q('#splash').classList.add('gone');
  Q('.appinner').classList.add('ready');
  await sleep(700);
  caption('Accounts', 'Every alt in one window');
  await sleep(1200);

  // health checks -> Robux / Premium badges
  for (const a of ACCTS) { accounts.find(x => x.userId === a.userId).health = 'checking'; updateCardInfo(a.userId); }
  caption('At a glance', 'Live Robux, Premium & cookie health');
  await sleep(550);
  for (const a of ACCTS) {
    const live = accounts.find(x => x.userId === a.userId);
    live.health = 'ok'; live.robux = a.robux; live.premium = a.premium;
    updateCardInfo(a.userId);
    await sleep(150);
  }
  logLine('Checked 8 accounts — all cookies valid.');
  await sleep(800);

  const accts = Q('#accounts');
  await smoothScroll(accts, accts.scrollHeight, 1300);
  await sleep(450);
  await smoothScroll(accts, 0, 1100);
  await sleep(500);

  // select + launch
  caption('Launch', 'Drop them into the same server together');
  for (const id of [1001, 1003, 1006]) { selected.add(id); render(); await sleep(420); }
  logLine('3 accounts selected.');
  await sleep(600);
  await typeInto('#placeId', '920587237', 70);
  await sleep(500);
  Q('#busy').classList.add('show');
  toast('Launching 3 clients…');
  await sleep(850);
  Q('#busy').classList.remove('show');
  for (const n of ['MainGrind', 'FarmBot', 'GrindKing']) { logLine(`Launched ${n} → place 920587237 (any server).`); await sleep(300); }
  toast('3 clients launched', 'ok');
  await sleep(1500);

  // Discover
  renderSections([{ title: 'Popular right now', games: POPULAR }, { title: 'Trading & economy', games: TRADING }]);
  discoverLoaded = true;
  navTo('discover');
  caption('Discover', 'Browse Roblox games — no launch needed');
  await sleep(1400);
  const disc = Q('#discover');
  await smoothScroll(disc, disc.scrollHeight, 1700);
  await sleep(450);
  await smoothScroll(disc, 0, 1300);
  await sleep(450);
  await typeInto('#gameSearch', 'blox fruits', 70);
  await sleep(450);
  renderSections([{ title: 'Results for "blox fruits"', games: [
    POPULAR[2],
    { placeId: 1, name: 'Blox Fruits [UPD 25]', playing: 51200, icon: icon('🍈', '#ff9248', '#ffe14d') },
    { placeId: 2, name: 'Fruit Battlegrounds', playing: 33400, icon: icon('🥊', '#ff6b78', '#9d6bff') },
  ] }]);
  await sleep(1700);

  // Analytics
  navTo('analytics');
  caption('Analytics', 'Your grind, at a glance');
  await sleep(2400);

  // Customize — themes & colors
  navTo('customize');
  caption('Customize', 'Themes & colors — make it yours');
  await sleep(800);
  for (const c of ['#5ab0ff', '#4ade9e', '#ff6eb2', '#7c6aff']) { try { setAccent(c, true); } catch (e) {} await sleep(700); }
  await sleep(400);
  const rr = Q('#rRadius');
  if (rr) for (const v of [12, 16, 20, 14, 8]) { rr.value = v; rr.dispatchEvent(new Event('input')); await sleep(260); }
  await sleep(700);

  // Settings — NEW: launch through your bootstrapper + Auto-rejoin
  navTo('settings');
  try { applyLaunchers([
    { kind: 'roblox', name: 'Roblox (official)', installed: true },
    { kind: 'bloxstrap', name: 'Bloxstrap', installed: true },
    { kind: 'fishstrap', name: 'Fishstrap', installed: false },
    { kind: 'froststrap', name: 'Froststrap', installed: true },
  ]); } catch (e) {}
  caption('Bootstrappers', 'Launch via Bloxstrap, Fishstrap or Froststrap');
  await sleep(900);
  const ls = Q('#launcherSel');
  if (ls) for (const v of ['bloxstrap', 'froststrap']) { ls.value = v; ls.dispatchEvent(new Event('change')); await sleep(950); }
  await sleep(700);
  caption('Auto-rejoin', 'Keeps your alts online while you AFK');
  await sleep(600);
  const sw = Q('#swRejoin'); if (sw) sw.classList.add('on');
  toast('Auto-rejoin on — clients stay open', 'ok');
  await sleep(2000);

  // back home, then end card
  hideCaption();
  navTo('accounts');
  await sleep(1100);
  endcard.classList.add('show');
  await sleep(2600);

  try { window.chrome.webview.postMessage(JSON.stringify({ action: 'demoDone' })); } catch (e) {}
})();
