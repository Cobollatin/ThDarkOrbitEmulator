// Hold the page's jump before delivery, jump from another client, then check immediate reconciliation.
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
let state, releaseJump, jumpResponse;
const requests = [];
window.HTMLCanvasElement.prototype.getContext = () => new Proxy({}, { get: () => () => {} });
window.HTMLCanvasElement.prototype.getBoundingClientRect = () => ({ left: 0, top: 0, width: 1000, height: 600 });
window.setInterval = () => 1; // A poll must not rescue the pending jump.
window.fetch = async (path, options = {}) => {
  const target = new URL(path, baseUrl);
  requests.push(`${options.method ?? 'GET'} ${target.pathname}`);
  if (target.pathname.endsWith('/jump')) {
    assert.equal(JSON.parse(options.body).mapId, 1);
    await new Promise(resolve => { releaseJump = resolve; });
  }
  const response = await fetch(target, {
    ...options, headers: { ...(options.headers ?? {}), cookie: `dosid=${dosid}` }
  });
  if (target.pathname.endsWith('/jump')) jumpResponse = { status: response.status, body: await response.clone().json() };
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
  window.document.getElementById('jump').click();
  await until(() => releaseJump, 'held map-1 jump');
  const gate = state.map.portals.reduce((best, item) => !best ||
    Math.hypot(item.x - state.pilot.x, item.y - state.pilot.y) <
    Math.hypot(best.x - state.pilot.x, best.y - state.pilot.y) ? item : best, null);
  const external = await fetch(new URL('/spacemap/world/jump', baseUrl), {
    method: 'POST', headers: { cookie: `dosid=${dosid}`, 'Content-Type': 'application/json' },
    body: JSON.stringify({ portalId: gate.id, mapId: 1 })
  });
  assert.equal(external.status, 200);
  releaseJump();
  await until(() => jumpResponse?.status === 409 && window.document.getElementById('mapName').textContent === 'Map 1-2', 'map-2 reconciliation');
  assert.equal(jumpResponse.body.status, 'map_changed');
  assert.equal(window.document.getElementById('status').textContent, 'Map changed to 1-2');
  assert.equal(window.document.getElementById('target').textContent, 'None');
  assert.equal(window.document.getElementById('jump').disabled, false);
  assert.equal(requests.filter(path => path === 'GET /spacemap/world/state').length, 2);
  console.log(JSON.stringify({ jumpStatus: jumpResponse.status, mapId: state.map.id, requests }));
} finally {
  releaseJump?.();
  dom.window.close();
}
