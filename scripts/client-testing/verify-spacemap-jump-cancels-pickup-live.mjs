// Run the shipped page against live Kestrel and confirm a gate jump clears an old-map pickup.
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
let poll, state, moveDone = false, holdNextState = false, heldState;
const requests = [];
window.setInterval = callback => { poll = callback; return 1; };
window.fetch = async (path, options = {}) => {
  const target = new URL(path, baseUrl);
  requests.push(`${options.method ?? 'GET'} ${target.pathname}`);
  const response = await fetch(target, {
    ...options, headers: { ...(options.headers ?? {}), cookie: `dosid=${dosid}` }
  });
  if (holdNextState && target.pathname.endsWith('/state')) {
    holdNextState = false;
    await new Promise(resolve => { heldState = resolve; });
  }
  if (response.ok && /\/(enter|state|move|jump)$/.test(target.pathname)) {
    state = await response.clone().json();
    if (target.pathname.endsWith('/move')) moveDone = true;
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
  await until(() => moveDone, 'box move missing');
  holdNextState = true;
  const stalePoll = poll();
  await until(() => heldState, 'old-map state response was not held');
  window.document.getElementById('jump').click();
  await until(() => state?.map.id !== oldMap && window.document.getElementById('mapName').textContent === `Map ${state.map.name}`, 'jump missing');
  const jumpStatus = window.document.getElementById('status').textContent;
  const destinationMap = state.map;
  assert.match(jumpStatus, /Jumped to map/);
  heldState();
  await stalePoll;
  assert.equal(state.map.id, oldMap, 'held state response was not from the old map');
  assert.equal(window.document.getElementById('mapName').textContent, `Map ${destinationMap.name}`, 'late old-map state repainted the destination');
  assert.equal(window.document.getElementById('status').textContent, jumpStatus, 'late old-map state changed jump status');
  await poll();
  assert.equal(window.document.getElementById('status').textContent, jumpStatus, 'old-map pickup changed jump status');
  assert.ok(!requests.includes('POST /spacemap/world/collect'), 'old-map pickup was sent');
  assert.ok(state.boxes.every(item => item.id !== box.id), 'old-map box appeared in destination');
  console.log(JSON.stringify({ oldMap, newMap: destinationMap.id, jumpStatus, delayedOldMapPoll: true, requests }));
} finally {
  dom.window.close();
}
