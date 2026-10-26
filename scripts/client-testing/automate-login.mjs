#!/usr/bin/env node
// Drives the DarkOrbit Unity client's embedded Vuplex 3D WebView (CEF / Chrome 100) over the Chrome DevTools Protocol.
// Started by Start-CaptureSession.ps1 -AutoLogin (which installs the CDP shim and passes credentials via env).
//
//   node automate-login.mjs --cdp http://127.0.0.1:9222 --config automate-login.config.json --out <dir>
//   node automate-login.mjs --cdp http://127.0.0.1:9222 --list      # print page targets and exit (no login)
//
// Why raw CDP instead of Playwright: playwright-core's connectOverCDP fails against this CEF build with
// "Browser.setDownloadBehavior: Browser context management is not supported" (verified 2026-10-21), so this
// script talks to the page targets from /json/list directly using Node 22's built-in WebSocket. No npm deps.
//
// Selectors: CSS, or "text=Some label" (first visible element whose trimmed innerText equals/contains it).
// Env: DO_USERNAME, DO_PASSWORD; fallback --creds-har <har> (captured SAS login POST). Never logged or written.
// Exit: 0 ok, 2 bad args/config, 3 CDP unreachable, 4 login page not found, 5 login failed, 6 menu step failed, 7 --max-seconds TTL hit.
import fs from 'node:fs';
import path from 'node:path';
import { provisionLocalAccount } from './provision-local-account.mjs';

const argv = process.argv.slice(2);
const arg = (n, d) => { const i = argv.indexOf(`--${n}`); return i >= 0 ? argv[i + 1] : d; };
const has = (n) => argv.includes(`--${n}`);
const cdp = arg('cdp', 'http://127.0.0.1:9222');
const outDir = arg('out', path.resolve('screenshots'));
const log = (...a) => console.log(new Date().toISOString(), ...a);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function getJson(p) { const r = await fetch(`${cdp}${p}`); if (!r.ok) throw new Error(`${p}: ${r.status}`); return r.json(); }
async function waitForCdp(timeoutMs) {
  const end = Date.now() + timeoutMs;
  while (Date.now() < end) { try { return await getJson('/json/version'); } catch {} await sleep(1000); }
  return null;
}
const pages = async () => (await getJson('/json/list')).filter((t) => t.type === 'page');

class Session {
  constructor(target) { this.target = target; this.id = 0; this.pending = new Map(); }
  open() {
    return new Promise((resolve, reject) => {
      this.ws = new WebSocket(this.target.webSocketDebuggerUrl);
      this.ws.onopen = () => resolve(this);
      this.ws.onerror = (e) => reject(new Error(`ws error ${e.message ?? ''}`));
      this.ws.onmessage = (m) => {
        const msg = JSON.parse(typeof m.data === 'string' ? m.data : m.data.toString());
        const p = msg.id && this.pending.get(msg.id);
        if (p) { this.pending.delete(msg.id); msg.error ? p.reject(new Error(msg.error.message)) : p.resolve(msg.result); }
      };
    });
  }
  send(method, params = {}, timeoutMs = 30000) {
    const id = ++this.id;
    this.ws.send(JSON.stringify({ id, method, params }));
    return new Promise((resolve, reject) => { this.pending.set(id, { resolve, reject }); setTimeout(() => { if (this.pending.delete(id)) reject(new Error(`${method} timeout`)); }, timeoutMs); });
  }
  async eval(fnSrc, ...args) {
    const expr = `(${fnSrc})(...${JSON.stringify(args)})`;
    const r = await this.send('Runtime.evaluate', { expression: expr, awaitPromise: true, returnByValue: true });
    if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description ?? r.exceptionDetails.text);
    return r.result.value;
  }
  close() { try { this.ws.close(); } catch {} }
}

// Runs inside the page: resolve a selector to the first visible element (searching same-origin iframes too).
const FIND = `function find(sel) {
  const docs = [document];
  for (const f of document.querySelectorAll('iframe')) { try { if (f.contentDocument) docs.push(f.contentDocument); } catch {} }
  const vis = (e) => { const r = e.getBoundingClientRect(); const s = getComputedStyle(e); return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none'; };
  for (const d of docs) {
    let els;
    if (sel.startsWith('text=')) { const t = sel.slice(5).trim().toLowerCase();
      els = [...d.querySelectorAll('a,button,input[type=submit],input[type=button],[role=button],li,span,div')].filter((e) => (e.innerText || e.value || '').trim().toLowerCase() === t);
      if (!els.length) els = [...d.querySelectorAll('a,button,[role=button]')].filter((e) => (e.innerText || '').trim().toLowerCase().includes(t));
    } else els = [...d.querySelectorAll(sel)];
    const e = els.find(vis); if (e) return e;
  }
  return null;
}`;
const visible = (s, sel) => s.eval(`(sel) => { ${FIND}; return !!find(sel); }`, sel).catch(() => false);
async function fill(s, sel, value) {
  const ok = await s.eval(`(sel) => { ${FIND}; const e = find(sel); if (!e) return false; e.focus(); e.select?.(); return true; }`, sel);
  if (!ok) throw new Error(`fill: '${sel}' not found`);
  await s.send('Input.insertText', { text: value });   // real input events, works with React/Vue inputs
  await s.eval(`(sel) => { ${FIND}; const e = find(sel); e.dispatchEvent(new Event('change', { bubbles: true })); }`, sel);
}
async function click(s, sel) {
  const r = await s.eval(`(sel) => { ${FIND}; const e = find(sel); if (!e) return null; e.scrollIntoView({block:'center'}); const b = e.getBoundingClientRect();
    let x = b.left + b.width / 2, y = b.top + b.height / 2, w = e.ownerDocument.defaultView;
    while (w !== w.top && w.frameElement) { const fb = w.frameElement.getBoundingClientRect(); x += fb.left; y += fb.top; w = w.parent; } return { x, y }; }`, sel);
  if (!r) throw new Error(`click: '${sel}' not found`);
  for (const type of ['mouseMoved', 'mousePressed', 'mouseReleased'])
    await s.send('Input.dispatchMouseEvent', { type, x: r.x, y: r.y, button: 'left', clickCount: 1 });
}
async function pressEnter(s) {
  for (const type of ['keyDown', 'keyUp']) await s.send('Input.dispatchKeyEvent', { type, key: 'Enter', code: 'Enter', windowsVirtualKeyCode: 13, text: type === 'keyDown' ? '\r' : undefined });
}

async function navigateMenu(s, relativeUrl, waitMs) {
  const base = new URL(s.target.url);
  const url = new URL(relativeUrl, base);
  if (!/^gbl\d+\.darkorbit\.com$/i.test(base.hostname) || url.origin !== base.origin ||
      url.pathname !== '/indexInternal.es' || !/^internal[A-Za-z0-9]+$/.test(url.searchParams.get('action') || '') ||
      [...url.searchParams.keys()].some((key) => key !== 'action') || url.hash) {
    throw new Error('menu navigation must be an action-only indexInternal.es URL on the logged-in host');
  }
  const timeoutMs = MENU_TIMEOUT_S * 1000;
  const deadline = Date.now() + timeoutMs;
  const result = await s.send('Page.navigate', { url: url.href }, timeoutMs);
  if (result.errorText) throw new Error(`menu navigation failed: ${result.errorText}`);
  while (Date.now() < deadline) {
    const ready = await s.eval('() => document.readyState').catch(() => 'loading');
    if (ready === 'complete') break;
    await sleep(100);
  }
  if (Date.now() >= deadline) throw new Error(`menu navigation load timed out after ${MENU_TIMEOUT_S}s`);
  log(`menu navigation ${url.pathname}?action=${url.searchParams.get('action')}`);
  await sleep(waitMs ?? 1000);
  const current = (await pages()).find(t => t.id === s.target.id);
  const actual = current && new URL(current.url).searchParams.get('action');
  if (actual !== url.searchParams.get('action')) {
    log(`menu redirected to ${/^internal[A-Za-z0-9]+$/.test(actual || '') ? actual : 'another page'}`);
    return false;
  }
  return true;
}

let shotN = 0;
async function shot(s, name) {
  try {
    fs.mkdirSync(outDir, { recursive: true });
    const f = path.join(outDir, `${String(++shotN).padStart(2, '0')}-${String(name).replace(/[^\w.-]/g, '_')}.png`);
    const { data } = await s.send('Page.captureScreenshot', { format: 'png' });
    fs.writeFileSync(f, Buffer.from(data, 'base64')); log('screenshot', f);
  } catch (e) { log('screenshot failed', e.message); }
}

const sessions = new Map();
async function sessionFor(t) {
  if (!sessions.has(t.id)) sessions.set(t.id, await new Session(t).open());
  else sessions.get(t.id).target = t; // CDP keeps the target ID across login redirects.
  return sessions.get(t.id);
}
async function findPage(urlPattern, selector, timeoutMs) {
  const re = new RegExp(urlPattern || '.*', 'i'); const end = Date.now() + timeoutMs;
  while (Date.now() < end) {
    for (const t of await pages().catch(() => [])) {
      if (!re.test(t.url)) continue;
      const s = await sessionFor(t).catch(() => null); if (!s) continue;
      if (!selector || await visible(s, selector)) return s;
    }
    await sleep(1000);
  }
  return null;
}
const finish = (code) => { for (const s of sessions.values()) s.close(); process.exit(code); };
// TTLs: menu element waits are capped (--menu-timeout, default 5s); the whole run is capped (--max-seconds, default 240s, exit 7).
const MENU_TIMEOUT_S = Math.max(1, Number(arg('menu-timeout', '5')));
const MAX_SECONDS = Number(arg('max-seconds', '240'));
if (MAX_SECONDS > 0) setTimeout(() => { log(`max-seconds ${MAX_SECONDS}s reached, aborting`); finish(7); }, MAX_SECONDS * 1000).unref();

const version = await waitForCdp(Number(arg('cdp-timeout', '90')) * 1000);
if (!version) { log(`CDP not reachable at ${cdp}`); process.exit(3); }
log('CDP', version.Browser, version['User-Agent']);
if (has('list')) { for (const t of await pages()) log('page', t.url, JSON.stringify(t.title)); process.exit(0); }

const cfgPath = arg('config');
if (!cfgPath || !fs.existsSync(cfgPath)) { log('missing --config'); process.exit(2); }
const cfg = JSON.parse(fs.readFileSync(cfgPath, 'utf8'));
// Credentials: env first; otherwise the most recent captured SAS login POST in --creds-har (values never printed).
function credsFromHar(file) {
  if (!file || !fs.existsSync(file)) return null;
  const har = JSON.parse(fs.readFileSync(file, 'utf8'));
  let found = null;
  for (const e of har.log?.entries ?? []) {
    const r = e.request;
    if (r.method !== 'POST' || !/\/Sas\/Authentication/i.test(r.url)) continue;
    const pd = r.postData ?? {};
    let text = pd.text ?? '';
    if (pd.encoding === 'base64') text = Buffer.from(text, 'base64').toString('utf8');
    const q = new URLSearchParams(text);
    if (q.get('username') && q.get('password')) found = { user: q.get('username'), pass: q.get('password') };
  }
  return found;
}
let user = process.env.DO_USERNAME, pass = process.env.DO_PASSWORD;
if (!user || !pass) {
  const c = credsFromHar(arg('creds-har'));
  if (c) { user = c.user; pass = c.pass; log('using credentials from captured SAS login in', arg('creds-har')); }
}
if (!user || !pass) { log('no credentials (DO_USERNAME/DO_PASSWORD or --creds-har)'); process.exit(2); }

if (has('provision-local-account')) {
  try {
    const status = await provisionLocalAccount(user, pass);
    log(`local account ready (HTTP ${status})`);
  } catch (error) {
    log(error.message);
    process.exit(8);
  }
}

let code = 0;
try {
  const L = cfg.login;
  for (const [i, step] of (L.preSteps ?? []).entries()) {        // e.g. cookie banner, "Login" tab
    if (step.enabled === false) continue;
    const s = await findPage(step.urlPattern, step.selector, (step.timeoutSeconds ?? 15) * 1000);
    if (s) { await click(s, step.selector); log(`pre-step ${i} '${step.name}' clicked`); await sleep(step.waitMs ?? 1000); }
    else log(`pre-step ${i} '${step.name}' not present, skipped`);
  }
  const s = await findPage(L.urlPattern, L.usernameSelector, (L.timeoutSeconds ?? 120) * 1000);
  if (!s) { log('login page not found; pages:'); for (const t of await pages()) log(' ', t.url); finish(4); }
  log('login page', s.target.url);
  await shot(s, 'login-page');
  await fill(s, L.usernameSelector, user);
  await fill(s, L.passwordSelector, pass);
  if (L.submitSelector && await visible(s, L.submitSelector)) await click(s, L.submitSelector); else await pressEnter(s);
  log('login submitted');
  let menuPage = s;
  if (L.successSelector || L.successUrlPattern) {
    const ok = await findPage(L.successUrlPattern, L.successSelector, (L.successTimeoutSeconds ?? 60) * 1000);
    if (!ok) { await shot(s, 'login-failed'); log('login success marker not seen'); finish(5); }
    await shot(ok, 'logged-in');
    menuPage = ok;
  }
  for (const [i, step] of (cfg.menuSteps ?? []).entries()) {
    if (step.enabled === false) continue;
    if (step.navigate) {
      try {
        const reached = await navigateMenu(menuPage, step.navigate, step.waitMs);
        await shot(menuPage, `menu-${step.name ?? i}`);
        if (!reached && !step.optional) { code = 6; break; }
      } catch (e) {
        log(`menu step ${i} '${step.name}' failed: ${e.message}`);
        if (!step.optional) { code = 6; break; }
      }
      continue;
    }
    const p = await findPage(step.urlPattern, step.selector, Math.min(step.timeoutSeconds ?? MENU_TIMEOUT_S, MENU_TIMEOUT_S) * 1000);
    if (!p) { log(`menu step ${i} '${step.name}' not found`); if (step.optional) continue; code = 6; break; }
    await click(p, step.selector);
    log(`menu step ${i} '${step.name}' clicked`);
    await sleep(step.waitMs ?? 1000);
    let resultPage = p;
    if (step.expectedUrlPattern) {
      resultPage = await findPage(step.expectedUrlPattern, '', Math.min(step.timeoutSeconds ?? MENU_TIMEOUT_S, MENU_TIMEOUT_S) * 1000);
      if (!resultPage) {
        log(`menu step ${i} '${step.name}' destination not found`);
        if (!step.optional) { code = 6; break; }
        resultPage = p;
      } else log(`menu step ${i} '${step.name}' destination reached`);
    }
    await shot(resultPage, `menu-${step.name ?? i}`);
  }
} catch (e) { log('error', e.message); code = code || 5; }
finish(code);
