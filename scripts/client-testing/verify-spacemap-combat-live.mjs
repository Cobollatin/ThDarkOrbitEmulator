// Exercise the shipped Spacemap DOM against live world HTTP endpoints.
// Usage: node verify-spacemap-combat-live.mjs <baseUrl> <dosid> [retryBaseUrl]
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { JSDOM } from '../../ThDarkOrbitEmulator.Cms/node_modules/jsdom/lib/api.js';

const [baseUrl, dosid, retryBaseUrl = baseUrl] = process.argv.slice(2);
assert.ok(baseUrl && dosid);
const source = readFileSync(new URL('../../ThDarkOrbitEmulator.ApiService/Features/World/LocalSpacemapPage.cs', import.meta.url), 'utf8');
const html = source.match(/<!doctype html>[\s\S]*?<\/html>/i)?.[0];
assert.ok(html);
const dom = new JSDOM(html, { url: new URL('/spacemap', baseUrl).href, runScripts: 'outside-only' });
const { window } = dom;
let shipDraws = 0;
window.HTMLCanvasElement.prototype.getContext = () => new Proxy({}, {
  get: (_, property) => property === 'translate' ? () => { shipDraws++; } : () => {},
  set: () => true
});
window.HTMLCanvasElement.prototype.getBoundingClientRect = () =>
  ({ left: 0, top: 0, width: 1000, height: 600 });
let poll;
window.setInterval = callback => { poll = callback; return 1; };
const requests = [];
let finalReward;
let nonlethalReward;
let useRetryReplica = false;
let releaseFirstFire;
let firstFireHeld = false;
window.fetch = async (url, options = {}) => {
  const target = new URL(url, useRetryReplica ? retryBaseUrl : baseUrl);
  requests.push(`${options.method ?? 'GET'} ${target.pathname}`);
  if (target.pathname.endsWith('/fire') && !firstFireHeld) {
    firstFireHeld = true;
    await new Promise(resolve => { releaseFirstFire = resolve; });
  }
  const response = await fetch(target, { ...options, headers: { ...(options.headers ?? {}), cookie: `dosid=${dosid}` } });
  if (target.pathname.endsWith('/fire') && response.ok) {
    const hit = await response.clone().json();
    if (hit.destroyed) finalReward = hit.reward;
    else nonlethalReward = hit.reward;
  }
  return response;
};
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
const text = id => window.document.getElementById(id).textContent;
const until = async (predicate, label) => {
  for (let i = 0; i < 100; i++) {
    if (await predicate()) return;
    await pause(50);
  }
  throw new Error(`${label}: target=${text('target')}; status=${text('status')}`);
};
try {
  window.eval(html.match(/<script>([\s\S]*?)<\/script>/)?.[1]);
  await until(() => text('position').includes('/'), 'ship did not enter');
  const response = await window.fetch('/spacemap/world/state');
  assert.equal(response.status, 200);
  const state = await response.json();
  const npc = state.npcs[0];
  assert.ok(npc, 'NPC was not visible in live state');
  const canvas = window.document.getElementById('spaceCanvas');
  canvas.dispatchEvent(new window.MouseEvent('click', { bubbles: true,
    clientX: npc.x / state.map.width * 1000, clientY: npc.y / state.map.height * 600 }));
  await until(() => text('target').includes(npc.kind), 'NPC selection did not render');
  const selected = text('target');
  assert.match(selected, new RegExp(`${npc.hp}/${npc.maxHp} HP`));
  window.document.getElementById('fire').click();
  await until(() => releaseFirstFire, 'first laser request was not held');
  assert.equal(window.document.getElementById('fire').disabled, true);
  window.document.getElementById('fire').dispatchEvent(new window.MouseEvent('click', { bubbles: true }));
  assert.equal(requests.filter(path => path === 'POST /spacemap/world/fire').length, 1,
    'repeated input sent a second laser shot while the first was pending');
  releaseFirstFire();
  await until(() => text('status') === 'Target out of laser range. Move closer.', 'range warning did not render');
  assert.equal(window.document.getElementById('fire').disabled, false);
  canvas.dispatchEvent(new window.MouseEvent('click', { bubbles: true,
    clientX: (npc.x - 400) / state.map.width * 1000,
    clientY: npc.y / state.map.height * 600 }));
  await until(() => requests.includes('POST /spacemap/world/move'), 'move request did not reach server');
  await pause(2500);
  await poll();
  window.document.getElementById('fire').click();
  await until(() => text('target') !== selected, 'laser damage did not render');
  assert.match(text('status'), /Laser hit/);
  const damaged = text('target');
  assert.match(damaged, /Streuner/);
  assert.equal(nonlethalReward, null);
  for (let i = 0; i < 10 && text('target') !== 'None'; i++) {
    const before = text('target');
    window.document.getElementById('fire').click();
    await until(() => text('target') !== before || text('status').startsWith('Destroyed'), 'laser did not advance');
  }
  await until(() => text('status').startsWith('Destroyed') && text('target') === 'None', 'NPC was not destroyed');
  assert.deepEqual([finalReward.credits, finalReward.uridium, finalReward.experience, finalReward.honor],
    [400, 1, 400, 2]);
  assert.equal(text('status'), 'Destroyed Streuner; earned 400 credits, 1 uridium, 400 XP, 2 honor.');
  // The host changes the reward variable, removes the spawn and teleports the pilot away.
  useRetryReplica = true;
  let distantState;
  await until(async () => {
    distantState = await (await window.fetch('/spacemap/world/state')).json();
    return Math.hypot(distantState.pilot.x - npc.x, distantState.pilot.y - npc.y) > 1000
      && !distantState.npcs.some(other => other.id === npc.id);
  }, 'host did not move the pilot and remove the NPC spawn');
  await poll();
  assert.match(text('position'), /0 \/ 0/);
  const retry = await window.fetch('/spacemap/world/fire', {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ npcId: npc.id })
  });
  assert.equal(retry.status, 409);
  assert.equal((await retry.json()).status, 'destroyed');
  assert.ok(shipDraws > 0, 'ship was not painted');
  for (const endpoint of ['POST /spacemap/world/enter', 'POST /spacemap/world/target',
    'POST /spacemap/world/fire', 'GET /spacemap/world/state'])
    assert.ok(requests.includes(endpoint), endpoint);
  console.log(JSON.stringify({ selected, damaged, finalTarget: text('target'),
    status: text('status'), reward: finalReward, retryStatus: retry.status,
    retryOrigin: new URL(retryBaseUrl).origin,
    retryPosition: [distantState.pilot.x, distantState.pilot.y], shipDraws, requests }));
} finally {
  releaseFirstFire?.();
  window.close();
}
