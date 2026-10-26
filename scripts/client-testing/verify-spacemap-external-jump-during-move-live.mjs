// Hold a map-1 move before delivery, jump through a second client, then verify immediate browser reconciliation.
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
window.HTMLCanvasElement.prototype.getBoundingClientRect = () => ({ left: 0, top: 0, width: 1000, height: 600 });
let state, releaseMove, moveResponse;
const requests = [];
window.setInterval = () => 1; // No poll may rescue the conflict path.
window.fetch = async (path, options = {}) => {
  const target = new URL(path, baseUrl);
  requests.push(`${options.method ?? 'GET'} ${target.pathname}`);
  if (target.pathname.endsWith('/move')) {
    assert.equal(JSON.parse(options.body).mapId, 1);
    await new Promise(resolve => { releaseMove = resolve; });
  }
  const response = await fetch(target, {
    ...options, headers: { ...(options.headers ?? {}), cookie: `dosid=${dosid}` }
  });
  if (target.pathname.endsWith('/move')) moveResponse = { status: response.status, body: await response.clone().json() };
  else if (response.ok && /\/(enter|state)$/.test(target.pathname)) state = await response.clone().json();
  return response;
};
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
const until = async (predicate, label) => {
  for (let i = 0; i < 120; i++) {
    if (predicate()) return;
    await pause(50);
  }
  throw new Error(`${label}: ${window.document.getElementById('status').textContent}`);
};
try {
  window.eval(html.match(/<script>([\s\S]*?)<\/script>/)?.[1]);
  await until(() => state?.map?.id === 1, 'map-1 entry');
  window.document.getElementById('spaceCanvas').dispatchEvent(new window.MouseEvent('click', {
    bubbles: true, clientX: 720, clientY: 470
  }));
  await until(() => releaseMove, 'held map-1 move');
  const gate = state.map.portals.reduce((best, item) => !best ||
    Math.hypot(item.x - state.pilot.x, item.y - state.pilot.y) <
    Math.hypot(best.x - state.pilot.x, best.y - state.pilot.y) ? item : best, null);
  const jump = await fetch(new URL('/spacemap/world/jump', baseUrl), {
    method: 'POST', headers: { cookie: `dosid=${dosid}`, 'Content-Type': 'application/json' },
    body: JSON.stringify({ portalId: gate.id })
  });
  assert.equal(jump.status, 200);
  releaseMove();
  await until(() => moveResponse?.status === 409 && window.document.getElementById('mapName').textContent === 'Map 1-2', 'immediate map-2 reconciliation');
  assert.equal(moveResponse.body.status, 'map_changed');
  assert.equal(window.document.getElementById('target').textContent, 'None');
  assert.equal(requests.filter(path => path === 'GET /spacemap/world/state').length, 2, 'entry poll and conflict refresh');
  assert.ok(!requests.includes('POST /spacemap/world/collect'));
  console.log(JSON.stringify({ moveStatus: moveResponse.status, mapId: state.map.id, requests }));
} finally {
  releaseMove?.();
  dom.window.close();
}
