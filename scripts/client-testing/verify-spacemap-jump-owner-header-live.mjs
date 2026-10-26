// Hold the page's jump on replica A, jump through replica B, then release it.
// The 409 must advertise the lease owner of the pilot's current map, not the old one.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { JSDOM } from '../../ThDarkOrbitEmulator.Cms/node_modules/jsdom/lib/api.js';

const [baseUrl, dosid, otherReplicaUrl] = process.argv.slice(2);
assert.ok(baseUrl && dosid && otherReplicaUrl);
const source = readFileSync(new URL('../../ThDarkOrbitEmulator.ApiService/Features/World/LocalSpacemapPage.cs', import.meta.url), 'utf8');
const html = source.match(/<!doctype html>[\s\S]*?<\/html>/i)?.[0];
assert.ok(html);
const dom = new JSDOM(html, { url: new URL('/spacemap', baseUrl).href, runScripts: 'outside-only' });
const { window } = dom;
window.HTMLCanvasElement.prototype.getContext = () => new Proxy({}, { get: () => () => {} });
window.HTMLCanvasElement.prototype.getBoundingClientRect = () => ({ left: 0, top: 0, width: 1000, height: 600 });
window.setInterval = () => 1;
let state, releaseJump, jumpResponse, stateRequests = 0;
window.fetch = async (path, options = {}) => {
  const url = new URL(path, baseUrl);
  if (url.pathname.endsWith('/jump')) {
    assert.equal(JSON.parse(options.body).mapId, 1);
    await new Promise(resolve => { releaseJump = resolve; });
  }
  const response = await fetch(url, {
    ...options, headers: { ...(options.headers ?? {}), cookie: `dosid=${dosid}` }
  });
  if (url.pathname.endsWith('/jump'))
    jumpResponse = { status: response.status, body: await response.clone().json(),
      owner: response.headers.get('x-mesh-map-owner-node') };
  if (/\/(enter|state)$/.test(url.pathname) && response.ok) {
    state = await response.clone().json();
    if (url.pathname.endsWith('/state')) stateRequests++;
  }
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
  window.document.getElementById('jump').click();
  await until(() => releaseJump, 'held jump');
  const gate = state.map.portals.reduce((best, item) => !best ||
    Math.hypot(item.x - state.pilot.x, item.y - state.pilot.y) <
    Math.hypot(best.x - state.pilot.x, best.y - state.pilot.y) ? item : best, null);
  const external = await fetch(new URL('/spacemap/world/jump', otherReplicaUrl), {
    method: 'POST', headers: { cookie: `dosid=${dosid}`, 'Content-Type': 'application/json' },
    body: JSON.stringify({ portalId: gate.id, mapId: 1 })
  });
  assert.equal(external.status, 200, await external.text());
  const externalOwner = external.headers.get('x-mesh-map-owner-node');
  const beforeReconcile = stateRequests;
  releaseJump();
  await until(() => jumpResponse?.status === 409 &&
    window.document.getElementById('mapName').textContent === 'Map 1-2', 'map-2 reconciliation');
  assert.equal(jumpResponse.body.status, 'map_changed');
  assert.equal(jumpResponse.owner, externalOwner,
    `jump conflict advertised ${jumpResponse.owner}, current map owner is ${externalOwner}`);
  assert.ok(stateRequests > beforeReconcile, 'the page did not refresh after the conflict');
  console.log(JSON.stringify({ jumpStatus: jumpResponse.status, conflictOwner: jumpResponse.owner,
    externalOwner, mapId: state.map.id, mapName: window.document.getElementById('mapName').textContent }));
} finally {
  releaseJump?.();
  window.close();
}
