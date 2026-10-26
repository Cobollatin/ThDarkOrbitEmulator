// Deliver the first target request only after the second click, then verify server selection.
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
let releaseFirst, entries = 0, firstId, secondId, fireResponse;
window.fetch = async (path, options = {}) => {
  const url = new URL(path, baseUrl);
  if (url.pathname.endsWith('/target')) {
    entries++;
    if (entries === 1) {
      firstId = JSON.parse(options.body).npcId;
      await new Promise(resolve => { releaseFirst = resolve; });
    } else secondId = JSON.parse(options.body).npcId;
  }
  const response = await fetch(url, {
    ...options, headers: { ...(options.headers ?? {}), cookie: `dosid=${dosid}` }
  });
  if (url.pathname.endsWith('/fire')) fireResponse =
    { status: response.status, body: await response.clone().json() };
  return response;
};
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
const text = id => window.document.getElementById(id).textContent;
const until = async (predicate, label) => {
  for (let i = 0; i < 100; i++) {
    if (predicate()) return;
    await pause(50);
  }
  throw new Error(`${label}: status=${text('status')}, entries=${entries}`);
};
try {
  window.eval(html.match(/<script>([\s\S]*?)<\/script>/)?.[1]);
  await until(() => text('position').includes('/'), 'world entry');
  const response = await window.fetch('/spacemap/world/state');
  assert.equal(response.status, 200);
  const state = await response.json();
  const [first, second] = state.npcs;
  assert.ok(first && second);
  const canvas = window.document.getElementById('spaceCanvas');
  const select = npc => canvas.dispatchEvent(new window.MouseEvent('click', { bubbles: true,
    clientX: npc.x / state.map.width * 1000, clientY: npc.y / state.map.height * 600 }));
  select(first);
  await until(() => releaseFirst, 'first target request');
  select(second);
  await pause(100);
  assert.equal(entries, 1, 'second selection overtook first request');
  releaseFirst();
  await until(() => entries === 2 && text('status') === `Locked ${second.kind}`, 'second target lock');
  assert.equal(firstId, first.id);
  assert.equal(secondId, second.id);
  window.document.getElementById('fire').click();
  await until(() => fireResponse, 'fire result');
  assert.notEqual(fireResponse.body.status, 'not_selected', 'server retained the older selection');
  assert.notEqual(fireResponse.body.status, 'unselected', 'server retained the older selection');
  assert.ok(text('target').startsWith(second.kind), 'second NPC is not rendered as target');
  console.log(JSON.stringify({ firstId, secondId, targetRequests: entries,
    fireStatus: fireResponse.status, fireResult: fireResponse.body.status ?? 'hit', target: text('target') }));
} finally {
  releaseFirst?.();
  window.close();
}
