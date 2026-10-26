// Hold a real old-map move response until after the shipped page completes a gate jump.
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
let poll, state, heldMove, releaseMove;
const requests = [];
window.setInterval = callback => { poll = callback; return 1; };
window.fetch = async (path, options = {}) => {
  const target = new URL(path, baseUrl);
  requests.push(`${options.method ?? 'GET'} ${target.pathname}`);
  const response = await fetch(target, {
    ...options, headers: { ...(options.headers ?? {}), cookie: `dosid=${dosid}` }
  });
  if (response.ok && /\/(enter|state|move|jump)$/.test(target.pathname)) {
    state = await response.clone().json();
  }
  if (target.pathname.endsWith('/move')) {
    heldMove = structuredClone(state);
    await new Promise(resolve => { releaseMove = resolve; });
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
  await until(() => state?.boxes?.length && poll, 'world entry failed');
  const oldMap = state.map.id;
  const box = state.boxes[0];
  window.document.getElementById('spaceCanvas').dispatchEvent(new window.MouseEvent('click', {
    bubbles: true, clientX: box.x / state.map.width * 1000,
    clientY: box.y / state.map.height * 600
  }));
  await until(() => releaseMove, 'old-map move response was not held');
  assert.equal(heldMove.map.id, oldMap);
  window.document.getElementById('jump').click();
  await until(() => state?.map.id !== oldMap && window.document.getElementById('mapName').textContent === `Map ${state.map.name}`, 'jump missing');
  const destination = state.map;
  const jumpStatus = window.document.getElementById('status').textContent;
  releaseMove();
  await pause(100);
  assert.equal(window.document.getElementById('mapName').textContent, `Map ${destination.name}`, 'late move repainted the old map');
  assert.equal(window.document.getElementById('status').textContent, jumpStatus, 'late move changed jump status');
  await poll();
  assert.equal(window.document.getElementById('mapName').textContent, `Map ${destination.name}`);
  assert.ok(!requests.includes('POST /spacemap/world/collect'), 'old-map pickup was sent');
  console.log(JSON.stringify({ oldMap, newMap: destination.id, delayedOldMapMove: true, requests }));
} finally {
  releaseMove?.();
  dom.window.close();
}
