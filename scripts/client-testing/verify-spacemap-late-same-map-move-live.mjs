// Delay delivery of the first move until after a second click; the page must serialize them.
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
window.setInterval = () => 1;
let entered, firstMove, secondMove, releaseFirst, moves = 0;
const requests = [];
window.fetch = async (path, options = {}) => {
  const target = new URL(path, baseUrl);
  requests.push(`${options.method ?? 'GET'} ${target.pathname}`);
  const moveIndex = target.pathname.endsWith('/move') ? ++moves : 0;
  if (moveIndex === 1) {
    await new Promise(resolve => { releaseFirst = resolve; });
  }
  const response = await fetch(target, { ...options, headers: { ...(options.headers ?? {}), cookie: `dosid=${dosid}` } });
  if (target.pathname.endsWith('/enter') && response.ok) entered = await response.clone().json();
  if (target.pathname.endsWith('/move')) {
    const result = await response.clone().json();
    if (moveIndex === 1) firstMove = result;
    else secondMove = result;
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
const click = (x, y) => window.document.getElementById('spaceCanvas').dispatchEvent(
  new window.MouseEvent('click', { bubbles: true, clientX: x, clientY: y }));
try {
  window.eval(html.match(/<script>([\s\S]*?)<\/script>/)?.[1]);
  await until(() => entered && window.document.getElementById('mapName').textContent === `Map ${entered.map.name}`, 'entry missing');
  click(720, 470);
  await until(() => releaseFirst, 'first move delivery was not held');
  click(850, 510);
  await pause(150);
  assert.equal(moves, 1, 'second move must wait for the first to complete');
  releaseFirst();
  await until(() => secondMove && window.document.getElementById('destination').textContent === `${secondMove.pilot.targetX} / ${secondMove.pilot.targetY}`, 'second move was not rendered');
  const destination = window.document.getElementById('destination').textContent;
  const map = window.document.getElementById('mapName').textContent;
  assert.notEqual(`${firstMove.pilot.targetX} / ${firstMove.pilot.targetY}`, `${secondMove.pilot.targetX} / ${secondMove.pilot.targetY}`);
  const saved = await fetch(new URL('/spacemap/world/state', baseUrl), { headers: { cookie: `dosid=${dosid}` } }).then(r => r.json());
  assert.equal(`${saved.pilot.targetX} / ${saved.pilot.targetY}`, destination, 'server must retain the latest destination');
  assert.equal(window.document.getElementById('mapName').textContent, map);
  assert.equal(requests.filter(item => item === 'POST /spacemap/world/move').length, 2);
  console.log(JSON.stringify({ map, firstTarget: [firstMove.pilot.targetX, firstMove.pilot.targetY], secondTarget: [secondMove.pilot.targetX, secondMove.pilot.targetY], serverTarget: [saved.pilot.targetX, saved.pilot.targetY], serialized: true }));
} finally {
  releaseFirst?.();
  dom.window.close();
}
