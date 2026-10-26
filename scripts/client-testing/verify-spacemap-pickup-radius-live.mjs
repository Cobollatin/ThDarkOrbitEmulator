// Run the shipped Spacemap page against live Kestrel world endpoints.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { JSDOM } from '../../ThDarkOrbitEmulator.Cms/node_modules/jsdom/lib/api.js';

const [baseUrl, dosid] = process.argv.slice(2);
assert.ok(baseUrl && dosid);
const source = readFileSync(new URL('../../ThDarkOrbitEmulator.ApiService/Features/World/LocalSpacemapPage.cs', import.meta.url), 'utf8');
const html = source.match(/<!doctype html>[\s\S]*?<\/html>/i)?.[0];
assert.ok(html);
const dom = new JSDOM(html, { url: new URL('/spacemap', baseUrl).href, runScripts: 'outside-only' });
const { window } = dom;
window.HTMLCanvasElement.prototype.getContext = () => new Proxy({}, { get: () => () => {} });
window.HTMLCanvasElement.prototype.getBoundingClientRect = () =>
  ({ left: 0, top: 0, width: 1000, height: 600 });
let poll;
window.setInterval = callback => { poll = callback; return 1; };
let state;
let pickupDistance;
let moveCompleted = false;
const requests = [];
window.fetch = async (url, options = {}) => {
  const target = new URL(url, baseUrl);
  requests.push(`${options.method ?? 'GET'} ${target.pathname}`);
  if (target.pathname.endsWith('/collect'))
    pickupDistance = Math.hypot(state.pilot.x - state.boxes[0].x, state.pilot.y - state.boxes[0].y);
  const response = await fetch(target, {
    ...options, headers: { ...(options.headers ?? {}), cookie: `dosid=${dosid}` }
  });
  if (target.pathname.endsWith('/move')) moveCompleted = true;
  if (target.pathname.endsWith('/state') && response.ok) state = await response.clone().json();
  return response;
};
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
const until = async (predicate, label) => {
  for (let i = 0; i < 100; i++) {
    if (predicate()) return;
    await pause(50);
  }
  throw new Error(`${label}: ${window.document.getElementById('status').textContent}`);
};
try {
  window.eval(html.match(/<script>([\s\S]*?)<\/script>/)?.[1]);
  await until(() => state?.boxes?.length && poll, 'world entry failed');
  assert.equal(state.collectionRadius, 400);
  const box = state.boxes[0];
  const startingDistance = Math.hypot(state.pilot.x - box.x, state.pilot.y - box.y);
  assert.ok(startingDistance > 180 && startingDistance < 400, `unexpected start distance ${startingDistance}`);
  window.document.getElementById('spaceCanvas').dispatchEvent(new window.MouseEvent('click', {
    bubbles: true, clientX: box.x / state.map.width * 1000,
    clientY: box.y / state.map.height * 600
  }));
  await until(() => requests.includes('POST /spacemap/world/move'), 'page did not move toward box');
  await until(() => moveCompleted, 'move did not complete');
  await poll();
  await until(() => requests.includes('POST /spacemap/world/collect'), 'page did not collect');
  assert.ok(pickupDistance > 180 && pickupDistance <= 400, `pickup distance ${pickupDistance}`);
  assert.match(window.document.getElementById('status').textContent, /^Collected /);
  console.log(JSON.stringify({ collectionRadius: state.collectionRadius, startingDistance,
    pickupDistance, status: window.document.getElementById('status').textContent, requests }));
} finally {
  dom.window.close();
}
