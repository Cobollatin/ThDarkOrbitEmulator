// Observe CMS edits through the shipped Spacemap page and live HTTP on a second replica.
// Usage: node verify-spacemap-cms-live.mjs <worldBaseUrl> <dosid> [surfaceLabel=CMS]
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { JSDOM } from '../../ThDarkOrbitEmulator.Cms/node_modules/jsdom/lib/api.js';

const [baseUrl, dosid, label = 'CMS', pickupCheck] = process.argv.slice(2);
assert.ok(baseUrl && dosid, 'world URL and dosid are required');
const source = readFileSync(new URL('../../ThDarkOrbitEmulator.ApiService/Features/World/LocalSpacemapPage.cs', import.meta.url), 'utf8');
const html = source.match(/<!doctype html>[\s\S]*?<\/html>/i)?.[0];
assert.ok(html, 'Spacemap HTML was not found');
const dom = new JSDOM(html, { url: new URL('/spacemap', baseUrl).href, runScripts: 'outside-only' });
const { window } = dom;
const draws = [];
const context = new Proxy({ translate: (x, y) => draws.push({ x, y }) }, {
  get(target, property) {
    if (property === 'canvas') return {};
    if (property === 'measureText') return () => ({ width: 20 });
    return target[property] ?? (() => {});
  },
  set(target, property, value) { target[property] = value; return true; }
});
window.HTMLCanvasElement.prototype.getContext = () => context;
let poll;
window.setInterval = callback => { poll = callback; return 1; };
const requests = [];
let latestState, collectStatus, pickupDistance;
window.fetch = async (url, options = {}) => {
  const target = new URL(url, baseUrl);
  requests.push(`${options.method ?? 'GET'} ${target.pathname}`);
  if (target.pathname.endsWith('/collect')) {
    const boxId = JSON.parse(options.body).boxId;
    const box = latestState.boxes.find(item => item.id === boxId);
    pickupDistance = Math.hypot(box.x - latestState.pilot.x, box.y - latestState.pilot.y);
  }
  const response = await fetch(target, { ...options,
    headers: { ...(options.headers ?? {}), cookie: `dosid=${dosid}` } });
  if (target.pathname.endsWith('/state') && response.ok) latestState = await response.clone().json();
  if (target.pathname.endsWith('/collect')) collectStatus = response.status;
  return response;
};
window.eval(html.match(/<script>([\s\S]*?)<\/script>/)?.[1]);
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
const zone = () => window.document.getElementById('safeZone').textContent;
const pickupRange = () => window.document.getElementById('pickupRange').textContent;
const position = () => window.document.getElementById('position').textContent;
window.HTMLCanvasElement.prototype.getBoundingClientRect = () =>
  ({ left: 0, top: 0, width: 1000, height: 600 });
const waitFor = async (read, expected) => {
  const deadline = Date.now() + 90000;
  while (Date.now() < deadline) {
    await poll();
    await pause(80);
    if (read() === expected) return;
    await pause(70);
  }
  throw new Error(`Spacemap did not render ${expected}; last state: ${read()}`);
};
try {
  await pause(150);
  await waitFor(zone, 'Protected');
  assert.equal(position(), '2800 / 1300');
  assert.ok(draws.length > 0, 'ship canvas did not draw');
  const observed = [{ step: 'entered', safeZone: zone(), position: position() }];
  await waitFor(zone, 'Outside (radius 500)');
  observed.push({ step: `${label} set`, safeZone: zone(), position: position() });
  await waitFor(zone, 'Protected');
  observed.push({ step: `${label} rollback`, safeZone: zone(), position: position() });
  const observedPickupRange = [];
  if (pickupCheck === 'pickup') {
    observedPickupRange.push(pickupRange());
    assert.equal(pickupRange(), '180 units');
    await waitFor(pickupRange, '400 units');
    observedPickupRange.push(pickupRange());
    const box = latestState.boxes.find(item => item.id < 0 && item.x === 3100 && item.y === 1300);
    assert.ok(box, 'replica B did not render the spawned pickup box');
    window.document.getElementById('spaceCanvas').dispatchEvent(new window.MouseEvent('click', {
      bubbles: true, clientX: box.x / latestState.map.width * 1000,
      clientY: box.y / latestState.map.height * 600
    }));
    await waitFor(() => collectStatus, 200);
    assert.match(window.document.getElementById('status').textContent, /^Collected 200 credits/);
    assert.ok(pickupDistance > 180 && pickupDistance <= 400, `pickup distance ${pickupDistance}`);
    await waitFor(pickupRange, '180 units');
    observedPickupRange.push(pickupRange());
  }
  assert.ok(requests.includes('POST /spacemap/world/enter'));
  assert.ok(requests.includes('GET /spacemap/world/state'));
  console.log(JSON.stringify({ surface: 'shipped LocalSpacemapPage HTML/JS in jsdom', observed, observedPickupRange,
    collectStatus, pickupDistance,
    requests: requests.length, shipCanvasDraws: draws.length }));
} finally {
  window.close();
}
