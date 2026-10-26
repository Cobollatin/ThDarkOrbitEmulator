// Delay delivery of a map-1 move until after a real jump; the server must reject it.
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
let state, moveStarted = false, releaseMove, moveResponse;
window.setInterval = () => 1;
window.fetch = async (path, options = {}) => {
  const target = new URL(path, baseUrl);
  if (target.pathname.endsWith('/move')) {
    const body = JSON.parse(options.body);
    assert.equal(body.mapId, 1, 'the shipped page must send its source map');
    moveStarted = true;
    await new Promise(resolve => { releaseMove = resolve; });
  }
  const response = await fetch(target, {
    ...options, headers: { ...(options.headers ?? {}), cookie: `dosid=${dosid}` }
  });
  if (target.pathname.endsWith('/move')) moveResponse = { status: response.status, body: await response.clone().json() };
  else if (response.ok && /\/(enter|state|jump)$/.test(target.pathname)) state = await response.clone().json();
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
  await until(() => moveStarted, 'held map-1 move');
  window.document.getElementById('jump').click();
  await until(() => state?.map?.id === 2 && window.document.getElementById('mapName').textContent === 'Map 1-2', 'map-2 jump');
  const arrival = `${state.pilot.x} / ${state.pilot.y}`;
  releaseMove();
  await until(() => moveResponse, 'late move response');
  assert.equal(moveResponse.status, 409);
  assert.equal(moveResponse.body.status, 'map_changed');
  assert.equal(window.document.getElementById('mapName').textContent, 'Map 1-2');
  const current = await fetch(new URL('/spacemap/world/state', baseUrl), { headers: { cookie: `dosid=${dosid}` } }).then(r => r.json());
  assert.equal(current.map.id, 2);
  assert.equal(`${current.pilot.targetX} / ${current.pilot.targetY}`, arrival, 'old move must not alter map-2 travel');
  console.log(JSON.stringify({ moveStatus: moveResponse.status, mapId: current.map.id, destination: arrival }));
} finally {
  releaseMove?.();
  dom.window.close();
}
