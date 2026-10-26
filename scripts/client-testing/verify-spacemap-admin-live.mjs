// Exercise the shipped in-game admin controls against a live local world host.
// Usage: node verify-spacemap-admin-live.mjs <baseUrl> <dosid>
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { JSDOM } from '../../ThDarkOrbitEmulator.Cms/node_modules/jsdom/lib/api.js';

const [baseUrl, dosid] = process.argv.slice(2);
assert.ok(baseUrl && dosid, 'world URL and dosid are required');
const source = readFileSync(new URL('../../ThDarkOrbitEmulator.ApiService/Features/World/LocalSpacemapPage.cs', import.meta.url), 'utf8');
const html = source.match(/<!doctype html>[\s\S]*?<\/html>/i)?.[0];
assert.ok(html, 'Spacemap HTML was not found');
const dom = new JSDOM(html, { url: new URL('/spacemap', baseUrl).href, runScripts: 'outside-only' });
const { window } = dom;
let draws = 0;
window.HTMLCanvasElement.prototype.getContext = () => new Proxy({}, {
  get: (_, property) => property === 'canvas' ? {} : property === 'measureText' ? () => ({ width: 20 })
    : property === 'translate' ? () => { draws++; } : () => {},
  set: () => true
});
window.setInterval = () => 1;
const requests = [];
window.fetch = async (url, options = {}) => {
  const target = new URL(url, baseUrl);
  requests.push(`${options.method ?? 'GET'} ${target.pathname}`);
  return fetch(target, { ...options, headers: { ...(options.headers ?? {}), cookie: `dosid=${dosid}` } });
};
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
const text = id => window.document.getElementById(id).textContent;
const until = async (predicate, label) => {
  for (let i = 0; i < 100; i++) {
    if (predicate()) return;
    await pause(50);
  }
  throw new Error(`${label}; result=${text('adminResult')}; zone=${text('safeZone')}`);
};
try {
  window.eval(html.match(/<script>([\s\S]*?)<\/script>/)?.[1]);
  await until(() => !window.document.getElementById('adminPanel').hidden &&
    [...window.document.querySelectorAll('#adminVariable option')].some(o => o.value === 'world.station.peace_radius'),
    'admin panel did not load');
  assert.equal(text('safeZone'), 'Protected');
  assert.equal(text('position'), '2800 / 1300');
  const select = window.document.getElementById('adminVariable');
  select.value = 'world.station.peace_radius';
  select.dispatchEvent(new window.Event('change'));
  assert.equal(window.document.getElementById('adminValue').value, '1000');
  window.document.getElementById('adminValue').value = '500';
  window.document.getElementById('adminVariableForm').dispatchEvent(new window.Event('submit', { bubbles: true, cancelable: true }));
  assert.equal(text('adminResult'), 'Enter a reason before changing a variable.');
  window.document.getElementById('adminReason').value = 'World-46 safe-zone check';
  window.document.getElementById('adminVariableForm').dispatchEvent(new window.Event('submit', { bubbles: true, cancelable: true }));
  await until(() => text('safeZone') === 'Outside (radius 500)' &&
    text('adminHistory').includes('world.station.peace_radius 1000 → 500 (InGameAdmin)'), 'variable form did not update world');
  assert.match(text('adminHistory'), /Live admin:837 on mesh-node-a · World-46 safe-zone check/);
  assert.doesNotMatch(text('adminHistory'), /Invalid Date/);
  const setState = { zone: text('safeZone'), history: text('adminHistory') };
  const missingReason = await window.fetch('/spacemap/world/admin/vars/rollback', {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ changeId: 1 })
  });
  assert.equal(missingReason.status, 400, 'rollback endpoint must require an audit reason');
  window.document.getElementById('adminReason').value = '';
  window.document.querySelector('#adminHistory li button').click();
  assert.equal(text('adminResult'), 'Enter a reason before rolling back.');
  window.document.getElementById('adminReason').value = 'World-46 restore safe zone';
  window.document.querySelector('#adminHistory li button').click();
  await until(() => text('safeZone') === 'Protected' &&
    text('adminHistory').includes('world.station.peace_radius 500 → 1000 (InGameAdmin)'), 'history rollback did not update world');
  assert.match(text('adminHistory'), /World-46 restore safe zone/);
  const historyBeforeConflict = text('adminHistory');
  window.document.getElementById('adminReason').value = 'World-48 stale rollback check';
  [...window.document.querySelectorAll('#adminHistory li')]
    .find(li => li.textContent.includes('#1 ')).querySelector('button').click();
  await until(() => text('adminResult').includes('Rollback conflict:'), 'stale rollback error was not shown');
  assert.equal(text('safeZone'), 'Protected');
  assert.equal(text('adminHistory'), historyBeforeConflict);
  assert.equal(select.selectedOptions[0].dataset.value, '1000');
  assert.ok(draws > 0, 'ship was not drawn');
  for (const endpoint of ['POST /spacemap/world/admin/command', 'GET /spacemap/world/admin/vars',
    'GET /spacemap/world/admin/vars/history', 'POST /spacemap/world/admin/vars/rollback',
    'GET /spacemap/world/state']) assert.ok(requests.includes(endpoint), endpoint);
  console.log(JSON.stringify({ panelVisible: true, setState, rollbackZone: text('safeZone'),
    rollbackHistory: text('adminHistory'), conflict: text('adminResult'), position: text('position'), draws, requests }));
} finally {
  window.close();
}
