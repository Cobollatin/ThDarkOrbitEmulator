// Delay a page target before delivery, jump through a second client, then inspect the page.
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
window.setInterval = () => 1;
let state, releaseTarget, targetResponse, stateReads = 0;
window.fetch = async (path, options = {}) => {
  const url = new URL(path, baseUrl);
  if (url.pathname.endsWith('/target')) {
    assert.equal(JSON.parse(options.body).mapId, 1);
    await new Promise(resolve => { releaseTarget = resolve; });
  }
  const response = await fetch(url, {
    ...options, headers: { ...(options.headers ?? {}), cookie: `dosid=${dosid}` }
  });
  if (url.pathname.endsWith('/target')) targetResponse =
    { status: response.status, body: await response.clone().json() };
  if (url.pathname.endsWith('/state')) stateReads++;
  if (response.ok && /\/(enter|state)$/.test(url.pathname)) state = await response.clone().json();
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
  await until(() => state?.map?.id === 1, 'map-1 entry');
  const initialStateReads = stateReads;
  const npc = state.npcs[0];
  assert.ok(npc);
  window.document.getElementById('spaceCanvas').dispatchEvent(new window.MouseEvent('click', {
    bubbles: true, clientX: npc.x / state.map.width * 1000,
    clientY: npc.y / state.map.height * 600
  }));
  await until(() => releaseTarget, 'held target');
  const gate = state.map.portals.reduce((best, item) => !best ||
    Math.hypot(item.x - state.pilot.x, item.y - state.pilot.y) <
    Math.hypot(best.x - state.pilot.x, best.y - state.pilot.y) ? item : best, null);
  const external = await fetch(new URL('/spacemap/world/jump', baseUrl), {
    method: 'POST', headers: { cookie: `dosid=${dosid}`, 'Content-Type': 'application/json' },
    body: JSON.stringify({ portalId: gate.id, mapId: 1 })
  });
  assert.equal(external.status, 200);
  releaseTarget();
  await until(() => targetResponse?.status === 409 && state?.map?.id === 2 &&
    text('mapName') === 'Map 1-2', 'map-2 reconciliation');
  assert.equal(targetResponse.body.status, 'map_changed');
  assert.equal(text('target'), 'None');
  assert.ok(stateReads > initialStateReads,
    'target conflict did not trigger a state refresh');
  assert.equal(text('status'), 'Map changed to 1-2');
  console.log(JSON.stringify({ targetStatus: targetResponse.status, mapId: state.map.id,
    stateReads, target: text('target'), status: text('status') }));
} finally {
  releaseTarget?.();
  dom.window.close();
}
