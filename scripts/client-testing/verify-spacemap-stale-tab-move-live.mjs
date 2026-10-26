// Two shipped page instances share one pilot; the second must display a rejected stale move.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { JSDOM } from '../../ThDarkOrbitEmulator.Cms/node_modules/jsdom/lib/api.js';

const [baseUrl, dosid, replicaUrl = baseUrl] = process.argv.slice(2);
assert.ok(baseUrl && dosid);
const source = readFileSync(new URL('../../ThDarkOrbitEmulator.ApiService/Features/World/LocalSpacemapPage.cs', import.meta.url), 'utf8');
const html = source.match(/<!doctype html>[\s\S]*?<\/html>/i)?.[0];
assert.ok(html);
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
async function until(predicate, label) {
  for (let i = 0; i < 120; i++) {
    if (predicate()) return;
    await pause(50);
  }
  throw new Error(label);
}
function openPage() {
  const dom = new JSDOM(html, { url: new URL('/spacemap', replicaUrl).href, runScripts: 'outside-only' });
  const { window } = dom;
  window.HTMLCanvasElement.prototype.getContext = () => new Proxy({}, { get: () => () => {} });
  window.HTMLCanvasElement.prototype.getBoundingClientRect = () => ({ left: 0, top: 0, width: 1000, height: 600 });
  window.setInterval = () => 1;
  const moves = [];
  window.fetch = async (path, options = {}) => {
    const url = new URL(path, replicaUrl);
    const response = await fetch(url, { ...options, headers: { ...(options.headers ?? {}), cookie: `dosid=${dosid}` } });
    if (url.pathname.endsWith('/move')) moves.push({ status: response.status, body: JSON.parse(options.body) });
    return response;
  };
  window.eval(html.match(/<script>([\s\S]*?)<\/script>/)?.[1]);
  return { dom, window, moves };
}
const second = openPage();
try {
  await until(() => second.window.document.getElementById('destination').textContent, 'page did not enter');
  const before = await fetch(new URL('/spacemap/world/state', baseUrl), { headers: { cookie: `dosid=${dosid}` } }).then(r => r.json());
  const first = await fetch(new URL('/spacemap/world/move', baseUrl), { method: 'POST',
    headers: { cookie: `dosid=${dosid}`, 'Content-Type': 'application/json' },
    body: JSON.stringify({ x: 15120, y: 10262, mapId: before.map.id, expectedMoveId: before.pilot.moveId }) });
  assert.equal(first.status, 200);
  const click = (page, x, y) => page.window.document.getElementById('spaceCanvas').dispatchEvent(
    new page.window.MouseEvent('click', { bubbles: true, clientX: x, clientY: y }));
  click(second, 850, 510);
  await until(() => second.moves.length === 1 && second.window.document.getElementById('status').textContent.includes('Destination changed in another client'), 'stale move was not rejected');
  const state = await fetch(new URL('/spacemap/world/state', replicaUrl), { headers: { cookie: `dosid=${dosid}` } }).then(r => r.json());
  assert.equal(second.moves[0].status, 409);
  assert.equal(before.pilot.moveId, second.moves[0].body.expectedMoveId);
  assert.equal(second.window.document.getElementById('destination').textContent, '15120 / 10262');
  assert.deepEqual([state.pilot.targetX, state.pilot.targetY], [15120, 10262]);
  click(second, 850, 510);
  await until(() => second.moves.length === 2 && second.moves[1].status === 200 &&
    second.window.document.getElementById('destination').textContent === '17850 / 11135', 'retry did not render');
  assert.equal(second.moves[1].body.expectedMoveId, state.pilot.moveId);
  const after = await fetch(new URL('/spacemap/world/state', replicaUrl), { headers: { cookie: `dosid=${dosid}` } }).then(r => r.json());
  assert.deepEqual([after.pilot.targetX, after.pilot.targetY], [17850, 11135]);
  console.log(JSON.stringify({ first: first.status, stale: second.moves[0].status, retry: second.moves[1].status,
    rendered: second.window.document.getElementById('destination').textContent,
    server: [after.pilot.targetX, after.pilot.targetY], replicas: [baseUrl, replicaUrl] }));
} finally {
  second.dom.window.close();
}
