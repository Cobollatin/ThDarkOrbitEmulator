// Run the shipped page against live Kestrel while another tab claims its selected box.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { JSDOM } from '../../ThDarkOrbitEmulator.Cms/node_modules/jsdom/lib/api.js';

const [baseUrl, dosid, claimantBaseUrl = baseUrl] = process.argv.slice(2);
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
const clientFetch = (path, options = {}, host = baseUrl) => fetch(new URL(path, host), {
  ...options, headers: { ...(options.headers ?? {}), cookie: `dosid=${dosid}` }
});
window.fetch = async (path, options = {}) => {
  const target = new URL(path, baseUrl);
  requests.push(`${options.method ?? 'GET'} ${target.pathname}`);
  const response = await clientFetch(target, options);
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
try {
  window.eval(html.match(/<script>([\s\S]*?)<\/script>/)?.[1]);
  await until(() => state?.boxes?.length && poll, 'world entry failed');
  const box = state.boxes[0];
  window.document.getElementById('spaceCanvas').dispatchEvent(new window.MouseEvent('click', {
    bubbles: true, clientX: box.x / state.map.width * 1000,
    clientY: box.y / state.map.height * 600
  }));
  await until(() => requests.includes('POST /spacemap/world/move'), 'page did not move toward box');
  // A second browser tab uses the same authenticated world contract. Wait for the
  // moving pilot to enter range, then claim before the first tab's next poll.
  let claimStatus;
  for (let i = 0; i < 120; i++) {
    const response = await clientFetch('/spacemap/world/state', {}, claimantBaseUrl);
    assert.equal(response.status, 200);
    const snapshot = await response.json();
    if (Math.hypot(box.x - snapshot.pilot.x, box.y - snapshot.pilot.y) <= snapshot.collectionRadius) {
      const claim = await clientFetch('/spacemap/world/collect', {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ boxId: box.id })
      }, claimantBaseUrl);
      claimStatus = claim.status;
      break;
    }
    await pause(50);
  }
  assert.equal(claimStatus, 200);
  await poll();
  const status = window.document.getElementById('status').textContent;
  assert.equal(status, 'Bonus box is no longer available.');
  assert.ok(!state.boxes.some(item => item.id === box.id));
  assert.ok(!requests.includes('POST /spacemap/world/collect'), 'first tab retried an unavailable box');
  console.log(JSON.stringify({ claimStatus, status, selectedBoxId: box.id, requests,
    replicaClaim: claimantBaseUrl !== baseUrl }));
} finally {
  dom.window.close();
}
