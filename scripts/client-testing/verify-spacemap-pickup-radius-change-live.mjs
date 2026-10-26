// Exercise the shipped page against live world HTTP while the authoritative pickup radius changes.
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
let poll, state, firstCollectStatus, firstCollectDistance, finalCollectDistance, moveCompleted = false;
const requests = [];
window.setInterval = callback => { poll = callback; return 1; };
window.fetch = async (url, options = {}) => {
  const target = new URL(url, baseUrl);
  const method = options.method ?? 'GET';
  requests.push(`${method} ${target.pathname}`);
  if (target.pathname.endsWith('/collect') && firstCollectStatus === undefined) {
    firstCollectDistance = Math.hypot(state.pilot.x - state.boxes[0].x, state.pilot.y - state.boxes[0].y);
    const change = await fetch(new URL('/test/pickup-radius', baseUrl), { method: 'POST' });
    assert.equal(change.status, 200);
  }
  const response = await fetch(target, {
    ...options, headers: { ...(options.headers ?? {}), cookie: `dosid=${dosid}` }
  });
  if (target.pathname.endsWith('/move')) moveCompleted = true;
  if (target.pathname.endsWith('/state') && response.ok) state = await response.clone().json();
  if (target.pathname.endsWith('/collect')) {
    if (firstCollectStatus === undefined) firstCollectStatus = response.status;
    if (response.ok)
      finalCollectDistance = Math.hypot(state.pilot.x - state.boxes[0].x, state.pilot.y - state.boxes[0].y);
  }
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
  window.document.getElementById('spaceCanvas').dispatchEvent(new window.MouseEvent('click', {
    bubbles: true, clientX: box.x / state.map.width * 1000,
    clientY: box.y / state.map.height * 600
  }));
  await until(() => requests.includes('POST /spacemap/world/move'), 'page did not move toward box');
  await until(() => moveCompleted, 'move did not complete');
  await poll();
  assert.equal(firstCollectStatus, 409);
  assert.ok(firstCollectDistance > 180 && firstCollectDistance <= 400, `first distance ${firstCollectDistance}`);
  assert.match(window.document.getElementById('status').textContent, /Pickup range changed/);
  for (let i = 0; i < 100 && finalCollectDistance === undefined; i++) {
    await pause(50);
    await poll();
  }
  assert.equal(state.collectionRadius, 180);
  assert.ok(finalCollectDistance !== undefined && finalCollectDistance <= 180,
    `final distance ${finalCollectDistance}`);
  assert.match(window.document.getElementById('status').textContent, /^Collected /);
  console.log(JSON.stringify({ initialRadius: 400, finalRadius: state.collectionRadius,
    firstCollectStatus, firstCollectDistance, finalCollectDistance,
    status: window.document.getElementById('status').textContent, requests }));
} finally {
  dom.window.close();
}
