// World-37: runs the shipped LocalSpacemapPage HTML/JS in jsdom against a live HTTP host (no synthetic
// responses) and drives the shipped Server Management CLI between state polls.
// World-39: an optional third argument points the CLI at a different replica than the Spacemap page.
// Usage: node verify-spacemap-live.mjs <baseUrl> <dosid> [cliBaseUrl]   (THDARKORBIT_ADMIN_TOKEN must be set for the CLI)
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { JSDOM } from '../../ThDarkOrbitEmulator.Cms/node_modules/jsdom/lib/api.js';

const [baseUrl, dosid, cliBaseUrl = baseUrl] = process.argv.slice(2);
assert.ok(baseUrl && dosid, 'usage: verify-spacemap-live.mjs <baseUrl> <dosid> [cliBaseUrl]');
const source = readFileSync(new URL('../../ThDarkOrbitEmulator.ApiService/Features/World/LocalSpacemapPage.cs', import.meta.url), 'utf8');
const html = source.match(/<!doctype html>[\s\S]*?<\/html>/i)?.[0];
assert.ok(html, 'Spacemap HTML was not found');
const cli = fileURLToPath(new URL('../admin/Invoke-ServerAdminCommand.ps1', import.meta.url));
const runCli = commandText => JSON.parse(execFileSync('powershell', ['-NoProfile', '-ExecutionPolicy', 'Bypass',
  '-File', cli, '-CommandText', commandText, '-BaseUri', cliBaseUrl],{ encoding: 'utf8', timeout: 60000 })).data;

const dom = new JSDOM(html, { url: new URL('/spacemap', baseUrl).href, runScripts: 'outside-only' });
const { window } = dom;
const shipDraws = [];
const context = new Proxy({ translate: (x, y) => shipDraws.push({ x, y }) }, { get(target, property) {
  if (property === 'canvas') return {};
  if (property === 'measureText') return () => ({ width: 20 });
  return target[property] ?? (() => {});
}, set(target, property, value) { target[property] = value; return true; } });
window.HTMLCanvasElement.prototype.getContext = () => context;
window.HTMLCanvasElement.prototype.getBoundingClientRect = () => ({ left: 0, top: 0, width: 1000, height: 600 });
let poll;
window.setInterval = callback => { poll = callback; return 1; };
const requests = [];
let latestState;
window.fetch = async (url, options = {}) => {
  const target = new URL(url, baseUrl);
  requests.push(`${options.method ?? 'GET'} ${target.pathname}`);
  const response = await fetch(target, { ...options, headers: { ...(options.headers ?? {}), cookie: `dosid=${dosid}` } });
  if (response.ok && (target.pathname.endsWith('/state') || target.pathname.endsWith('/enter')))
    latestState = await response.clone().json();
  return response;
};
window.eval(html.match(/<script>([\s\S]*?)<\/script>/)?.[1]);
const settle = async () => { for (let i = 0; i < 40; i++) await new Promise(resolve => setTimeout(resolve, 5)); };
await settle();
const text = id => window.document.getElementById(id).textContent;
const observed = [];
const observe = label => observed.push({ label, position: text('position'), safeZone: text('safeZone') });

observe('entered');
assert.equal(text('position'), '2800 / 1300');
assert.equal(text('safeZone'), 'Protected');
const set = runCli('var set world.station.peace_radius 500 world-37 live spacemap');
await poll(); await settle();
observe('after cli set 500');
assert.equal(text('safeZone'), 'Outside (radius 500)');
// Let the connected WorldServer client's one-second idle poll observe the changed radius.
await new Promise(resolve => setTimeout(resolve, 1200));
const rollback = runCli(`var rollback ${set.changeId} world-37 restore`);
await poll(); await settle();
observe('after cli rollback');
assert.equal(text('safeZone'), 'Protected');
const canvas = window.document.getElementById('spaceCanvas');
const beforeMove = shipDraws.at(-2);
assert.ok(beforeMove && beforeMove.x > 0 && beforeMove.y > 0, 'ship was drawn on the main canvas');
const fractions = [[0.45, 0.42], [0.55, 0.65], [0.7, 0.35], [0.35, 0.7]];
const destination = fractions.map(([fx, fy]) => ({ fx, fy,
  x: Math.round(fx * latestState.map.width), y: Math.round(fy * latestState.map.height) }))
  .find(point => [...(latestState.npcs ?? []), ...(latestState.boxes ?? [])]
    .every(item => Math.hypot(item.x - point.x, item.y - point.y) > 1000));
assert.ok(destination, 'a clear canvas destination was found');
canvas.dispatchEvent(new window.MouseEvent('click', { bubbles: true,
  clientX: destination.fx * 1000, clientY: destination.fy * 600 }));
await settle();
assert.ok(requests.includes('POST /spacemap/world/move'), 'canvas click reached the live move endpoint');
await new Promise(resolve => setTimeout(resolve, 1200));
await poll(); await settle();
observe('after canvas move and state poll');
const [movedX, movedY] = text('position').split(' / ').map(Number);
assert.ok(movedX > 2800 && movedY > 1300, 'state poll advanced the ship toward the click');
const afterMove = shipDraws.at(-2);
assert.ok(afterMove.x > beforeMove.x && afterMove.y > beforeMove.y,
  'main canvas drew the ship at its advanced position');
window.close();
console.log(JSON.stringify({ surface: 'actual LocalSpacemapPage HTML/JS in jsdom',
  transport: `live HTTP ${baseUrl}`, cliTarget: cliBaseUrl, observed, requests,
  shipDraws: { beforeMove, afterMove },
  changes: [set, rollback].map(c => ({ changeId: c.changeId, surface: c.surface, newValue: c.newValue })) }));
