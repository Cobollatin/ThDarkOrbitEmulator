// Hold a real laser response after server processing, then select another live NPC.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { JSDOM } from '../../ThDarkOrbitEmulator.Cms/node_modules/jsdom/lib/api.js';

const [baseUrl, dosid, expectedDestroyed] = process.argv.slice(2);
assert.ok(baseUrl && dosid);
const source = readFileSync(new URL('../../ThDarkOrbitEmulator.ApiService/Features/World/LocalSpacemapPage.cs', import.meta.url), 'utf8');
const html = source.match(/<!doctype html>[\s\S]*?<\/html>/i)?.[0];
assert.ok(html);
const dom = new JSDOM(html, { url: new URL('/spacemap', baseUrl).href, runScripts: 'outside-only' });
const { window } = dom;
let shipDraws = 0;
window.HTMLCanvasElement.prototype.getContext = () => new Proxy({}, {
  get: (_, property) => property === 'translate' ? () => { shipDraws++; } : () => {}
});
window.HTMLCanvasElement.prototype.getBoundingClientRect = () =>
  ({ left: 0, top: 0, width: 1000, height: 600 });
window.setInterval = () => 1;
let releaseFire, heldHit;
const requests = [];
window.fetch = async (path, options = {}) => {
  const url = new URL(path, baseUrl);
  requests.push(`${options.method ?? 'GET'} ${url.pathname}`);
  const response = await fetch(url, { ...options, headers: { ...(options.headers ?? {}), cookie: `dosid=${dosid}` } });
  if (url.pathname.endsWith('/fire')) {
    assert.equal(response.status, 200);
    heldHit = await response.clone().json();
    await new Promise(resolve => { releaseFire = resolve; });
  }
  return response;
};
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
const text = id => window.document.getElementById(id).textContent;
const until = async (predicate, label) => {
  for (let i = 0; i < 100; i++) {
    if (predicate()) return;
    await pause(50);
  }
  throw new Error(`${label}: target=${text('target')}; status=${text('status')}`);
};
try {
  window.eval(html.match(/<script>([\s\S]*?)<\/script>/)?.[1]);
  await until(() => text('position').includes('/'), 'world entry failed');
  const stateResponse = await window.fetch('/spacemap/world/state');
  assert.equal(stateResponse.status, 200);
  const state = await stateResponse.json();
  const [first, second] = state.npcs;
  assert.ok(first && second, 'two NPCs required');
  const canvas = window.document.getElementById('spaceCanvas');
  const select = npc => canvas.dispatchEvent(new window.MouseEvent('click', { bubbles: true,
    clientX: npc.x / state.map.width * 1000, clientY: npc.y / state.map.height * 600 }));
  select(first);
  await until(() => text('status') === `Locked ${first.kind}`, 'first target not locked');
  window.document.getElementById('fire').click();
  await until(() => releaseFire, 'laser result was not held');
  assert.equal(heldHit.destroyed, expectedDestroyed === 'true');
  select(second);
  await until(() => text('status') === `Locked ${second.kind}`, 'second target not locked');
  const secondTarget = text('target');
  releaseFire();
  await until(() => !window.document.getElementById('fire').disabled, 'Fire did not settle');
  assert.equal(text('status'), `Locked ${second.kind}`);
  assert.equal(text('target'), secondTarget);
  assert.ok(shipDraws > 0, 'ship was not painted');
  const selectedResponse = await window.fetch('/spacemap/world/state');
  assert.equal(selectedResponse.status, 200);
  assert.ok((await selectedResponse.json()).npcs.some(npc => npc.id === second.id));
  console.log(JSON.stringify({ destroyed: heldHit.destroyed, firstId: first.id, secondId: second.id,
    target: secondTarget, status: text('status'), shipDraws, requests }));
} finally {
  releaseFire?.();
  window.close();
}
