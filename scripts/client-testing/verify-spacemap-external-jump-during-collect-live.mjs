// Delay the page's pickup before delivery, jump externally, then verify conflict and DOM.
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
let poll, state, releaseCollect, collectResponse, stateReads = 0, moved = false;
window.setInterval = callback => { poll = callback; return 1; };
window.fetch = async (path, options = {}) => {
  const url = new URL(path, baseUrl);
  if (url.pathname.endsWith('/collect')) {
    assert.equal(JSON.parse(options.body).mapId, 1);
    await new Promise(resolve => { releaseCollect = resolve; });
  }
  const response = await fetch(url, {
    ...options, headers: { ...(options.headers ?? {}), cookie: `dosid=${dosid}` }
  });
  if (url.pathname.endsWith('/collect')) collectResponse =
    { status: response.status, body: await response.clone().json() };
  if (url.pathname.endsWith('/state')) stateReads++;
  if (response.ok && /\/(enter|state|move)$/.test(url.pathname)) {
    state = await response.clone().json();
    if (url.pathname.endsWith('/move')) moved = true;
  }
  return response;
};
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
const text = id => window.document.getElementById(id).textContent;
const until = async (predicate, label) => {
  for (let i = 0; i < 120; i++) {
    if (predicate()) return;
    await pause(50);
  }
  throw new Error(`${label}: map=${text('mapName')}, status=${text('status')}`);
};
try {
  window.eval(html.match(/<script>([\s\S]*?)<\/script>/)?.[1]);
  await until(() => state?.boxes?.length && poll, 'map-1 entry');
  const box = state.boxes[0];
  window.document.getElementById('spaceCanvas').dispatchEvent(new window.MouseEvent('click', {
    bubbles: true, clientX: box.x / state.map.width * 1000,
    clientY: box.y / state.map.height * 600
  }));
  await until(() => moved, 'box movement');
  void poll();
  await until(() => releaseCollect, 'held collect');
  const before = stateReads;
  const gate = state.map.portals.reduce((best, item) => !best ||
    Math.hypot(item.x - state.pilot.x, item.y - state.pilot.y) <
    Math.hypot(best.x - state.pilot.x, best.y - state.pilot.y) ? item : best, null);
  const external = await fetch(new URL('/spacemap/world/jump', baseUrl), {
    method: 'POST', headers: { cookie: `dosid=${dosid}`, 'Content-Type': 'application/json' },
    body: JSON.stringify({ portalId: gate.id, mapId: 1 })
  });
  assert.equal(external.status, 200);
  releaseCollect();
  await until(() => collectResponse?.status === 409 && state?.map?.id === 2 &&
    text('mapName') === 'Map 1-2', 'map-2 reconciliation');
  assert.equal(collectResponse.body.status, 'map_changed');
  assert.ok(stateReads > before, 'collect conflict did not refresh world state');
  assert.equal(text('status'), 'Map changed to 1-2');
  assert.ok(!state.boxes.some(item => item.id === box.id), 'source-map box survived jump');
  console.log(JSON.stringify({ collectStatus: collectResponse.status, mapId: state.map.id,
    stateReads, status: text('status') }));
} finally {
  releaseCollect?.();
  dom.window.close();
}
