// Delay a real target or laser response until the shipped page has jumped maps.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { JSDOM } from '../../ThDarkOrbitEmulator.Cms/node_modules/jsdom/lib/api.js';

const [baseUrl, dosid, action, jumpMode = 'local'] = process.argv.slice(2);
assert.ok(baseUrl && dosid && ['target', 'fire'].includes(action));
const source = readFileSync(new URL('../../ThDarkOrbitEmulator.ApiService/Features/World/LocalSpacemapPage.cs', import.meta.url), 'utf8');
const html = source.match(/<!doctype html>[\s\S]*?<\/html>/i)?.[0];
assert.ok(html);
const dom = new JSDOM(html, { url: new URL('/spacemap', baseUrl).href, runScripts: 'outside-only' });
const { window } = dom;
window.HTMLCanvasElement.prototype.getContext = () => new Proxy({}, { get: () => () => {} });
window.HTMLCanvasElement.prototype.getBoundingClientRect = () => ({ left: 0, top: 0, width: 1000, height: 600 });
let state, release, heldStatus;
const requests = [];
let poll;
window.setInterval = callback => { poll = callback; return 1; };
window.fetch = async (path, options = {}) => {
  const target = new URL(path, baseUrl);
  requests.push(`${options.method ?? 'GET'} ${target.pathname}`);
  const response = await fetch(target, { ...options, headers: { ...(options.headers ?? {}), cookie: `dosid=${dosid}` } });
  if (response.ok && /\/(enter|state|jump)$/.test(target.pathname)) state = await response.clone().json();
  if (target.pathname.endsWith(`/${action}`)) {
    heldStatus = response.status;
    await new Promise(resolve => { release = resolve; });
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
  await until(() => state?.npcs?.length, 'world entry failed');
  const oldMap = state.map.id;
  const npc = state.npcs[0];
  window.document.getElementById('spaceCanvas').dispatchEvent(new window.MouseEvent('click', {
    bubbles: true, clientX: npc.x / state.map.width * 1000,
    clientY: npc.y / state.map.height * 600
  }));
  if (action === 'fire') {
    await until(() => window.document.getElementById('target').textContent.includes(npc.kind), 'target missing');
    window.document.getElementById('fire').click();
  }
  await until(() => release, `${action} response was not held`);
  assert.equal(heldStatus, action === 'target' ? 200 : 409);
  if (jumpMode === 'external') {
    const gate = state.map.portals.reduce((best, item) => !best ||
      Math.hypot(item.x - state.pilot.x, item.y - state.pilot.y) <
      Math.hypot(best.x - state.pilot.x, best.y - state.pilot.y) ? item : best, null);
    const jumped = await fetch(new URL('/spacemap/world/jump', baseUrl), {
      method: 'POST', headers: { cookie: `dosid=${dosid}`, 'Content-Type': 'application/json' },
      body: JSON.stringify({ portalId: gate.id })
    });
    assert.equal(jumped.status, 200);
    poll();
  } else window.document.getElementById('jump').click();
  await until(() => state?.map.id !== oldMap && window.document.getElementById('mapName').textContent === `Map ${state.map.name}`, 'jump missing');
  const destination = state.map;
  const jumpStatus = window.document.getElementById('status').textContent;
  release();
  await pause(100);
  assert.equal(window.document.getElementById('mapName').textContent, `Map ${destination.name}`);
  assert.equal(window.document.getElementById('status').textContent, jumpStatus);
  assert.equal(window.document.getElementById('target').textContent, 'None');
  assert.ok(!requests.includes('POST /spacemap/world/collect'));
  console.log(JSON.stringify({ action, jumpMode, oldMap, newMap: destination.id, heldStatus, jumpStatus, requests }));
} finally {
  release?.();
  dom.window.close();
}
