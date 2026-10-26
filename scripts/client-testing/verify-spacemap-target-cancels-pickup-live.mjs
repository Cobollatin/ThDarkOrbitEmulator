// Exercise the shipped page over live Kestrel: locking an NPC cancels a pending box pickup.
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
let poll, state;
const requests = [];
window.setInterval = callback => { poll = callback; return 1; };
window.fetch = async (path, options = {}) => {
  const target = new URL(path, baseUrl);
  requests.push(`${options.method ?? 'GET'} ${target.pathname}`);
  const response = await fetch(target, {
    ...options, headers: { ...(options.headers ?? {}), cookie: `dosid=${dosid}` }
  });
  if (target.pathname.endsWith('/state') && response.ok) state = await response.clone().json();
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
const click = item => window.document.getElementById('spaceCanvas').dispatchEvent(new window.MouseEvent('click', {
  bubbles: true, clientX: item.x / state.map.width * 1000,
  clientY: item.y / state.map.height * 600
}));
try {
  window.eval(html.match(/<script>([\s\S]*?)<\/script>/)?.[1]);
  await until(() => state?.boxes?.length && state?.npcs?.length && poll, 'world entry failed');
  const npc = state.npcs[0];
  const box = state.boxes.find(item => Math.hypot(item.x - npc.x, item.y - npc.y) > 600);
  assert.ok(box, 'no box distinct from NPC');
  click(box);
  await until(() => requests.includes('POST /spacemap/world/move'), 'box move missing');
  await pause(100);
  click(npc);
  await until(() => requests.includes('POST /spacemap/world/target') &&
    window.document.getElementById('target').textContent.includes('Streuner'), 'NPC target missing');
  assert.match(window.document.getElementById('target').textContent, /Streuner/);
  // Poll while the existing flight arrives at the box. The target action must not collect it.
  for (let i = 0; i < 100; i++) {
    await pause(50);
    await poll();
    if (Math.hypot(box.x - state.pilot.x, box.y - state.pilot.y) <= state.collectionRadius) break;
  }
  const distance = Math.hypot(box.x - state.pilot.x, box.y - state.pilot.y);
  assert.ok(distance <= state.collectionRadius, `ship did not reach box: ${distance}`);
  assert.ok(!requests.includes('POST /spacemap/world/collect'), 'stale pickup was collected');
  assert.ok(state.boxes.some(item => item.id === box.id), 'box vanished');
  console.log(JSON.stringify({ targetId: npc.id, boxId: box.id, distance, status: window.document.getElementById('status').textContent, requests }));
} finally {
  dom.window.close();
}
