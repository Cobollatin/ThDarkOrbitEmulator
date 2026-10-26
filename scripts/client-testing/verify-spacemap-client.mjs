import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { JSDOM } from '../../ThDarkOrbitEmulator.Cms/node_modules/jsdom/lib/api.js';

const source = readFileSync(new URL('../../ThDarkOrbitEmulator.ApiService/Features/World/LocalSpacemapPage.cs', import.meta.url), 'utf8');
const html = source.match(/<!doctype html>[\s\S]*?<\/html>/i)?.[0];
assert.ok(html, 'Spacemap HTML was not found');
const results = [];
for (const [company, shipId] of [['mmo', 'ship_pusat-plus'], ['eic', 'ship_phoenix'],
  ['vru', 'ship_phoenix'], ['tst', 'ship_phoenix']]) {
  const dom = new JSDOM(html, { url: 'http://localhost/spacemap', runScripts: 'outside-only' });
  const { window } = dom;
  const draws = [];
  const calls = [];
  let speed = 300, laserDamage = 30, peaceRadius = 1000, x = 2000, y = 1300, changeId = 7;
  let mapName = company.toUpperCase();
  let rejectNextMoveForLease = false, rejectNextTargetForLease = false;
  let rejectNextStateForLease = false, poll;
  let nearby = [];
  const history = [];
  const state = () => ({
    map: { name: mapName, width: 21000, height: 13100,
      station: { x: 2000, y: 1300, peaceRadius },
      portals: [{ id: 101, x: 19500, y: 1300, destinationMapId: 2 }] },
    pilot: { id: 1, name: 'Pilot', company, shipId, x, y, targetX: x, targetY: y,
      speed, laserDamage, isAdmin: true, hp: 1000, maxHp: 1000, shield: 500,
      maxShield: 500, configuration: 1, companions: [
        { kind: 'drone', itemId: 'drone_iris', x: x - 40, y },
        { kind: 'pet', itemId: 'pet_10', x: x + 40, y }] },
    pilots: [{ id: 1, name: 'Pilot', x, y }, ...nearby],
    npcs: [{ id: 5, kind: 'Streuner', x: 7000, y: 7000, hp: 100, maxHp: 100,
      shield: 0, maxShield: 0 }],
    boxes: [{ id: 9, kind: 'bonus', x: 8000, y: 7000, assetId: 'credits' }]
  });
  const context = new Proxy({}, { get(target, property) {
    if (property === 'canvas') return {};
    if (property === 'measureText') return () => ({ width: 20 });
    if (property === 'fillText') return (...args) => draws.push(['fillText', ...args]);
    if (['arc', 'moveTo', 'lineTo', 'fill', 'stroke'].includes(property))
      return (...args) => draws.push([property, target.fillStyle, ...args]);
    return target[property] ?? (() => {});
  }, set(target, property, value) { target[property] = value; return true; } });
  window.HTMLCanvasElement.prototype.getContext = () => context;
  window.HTMLCanvasElement.prototype.getBoundingClientRect = () =>
    ({ left: 0, top: 0, width: 1000, height: 600 });
  window.setInterval = callback => { poll = callback; return 1; };
  window.fetch = async (url, options = {}) => {
    const body = options.body ? JSON.parse(options.body) : undefined;
    calls.push({ url, method: options.method ?? 'GET', body });
    if (url.endsWith('/state') && rejectNextStateForLease) {
      rejectNextStateForLease = false;
      return { ok: false, status: 409, json: async () => ({ status: 'map_lease_changed' }) };
    }
    if (url.endsWith('/move') && rejectNextMoveForLease) {
      rejectNextMoveForLease = false;
      return { ok: false, status: 409, json: async () => ({ status: 'map_lease_changed' }) };
    }
    if (url.endsWith('/target') && rejectNextTargetForLease) {
      rejectNextTargetForLease = false;
      return { ok: false, status: 409, json: async () => ({ status: 'map_lease_changed' }) };
    }
    let result;
    if (url.endsWith('/enter') || url.endsWith('/state')) result = state();
    else if (url.endsWith('/move')) { x = body.x; y = body.y; result = state(); }
    else if (url.endsWith('/target')) result = state().npcs.find(npc => npc.id === body.npcId);
    else if (url.endsWith('/admin/vars')) result = [
      { key: 'world.movement.speed_multiplier', currentValue: String(speed / 300) }];
    else if (url.includes('/admin/vars/history')) result = history;
    else if (url.endsWith('/admin/command')) {
      const match = body.text.match(/(?:\/var set|var set) world\.movement\.speed_multiplier (\d+(?:\.\d+)?)/);
      if (match) { const previousValue = String(speed / 300); speed = 300 * Number(match[1]);
        history.unshift({ changeId: changeId++, variableKey: 'world.movement.speed_multiplier',
          previousValue, newValue: match[1], surface: 'InGameAdmin' }); }
      result = { data: { applied: true } };
    } else if (url.endsWith('/admin/vars/rollback')) {
      const change = history.find(row => row.changeId === body.changeId);
      assert.ok(change); speed = 300 * Number(change.previousValue);
      history.unshift({ changeId: changeId++, variableKey: change.variableKey,
        previousValue: change.newValue, newValue: change.previousValue, surface: 'InGameAdmin' });
      result = { changeId: history[0].changeId };
    } else throw new Error(`Unexpected client request: ${url}`);
    return { ok: true, json: async () => result };
  };
  window.eval(html.match(/<script>([\s\S]*?)<\/script>/)?.[1]);
  const settle = async () => { for (let i = 0; i < 12; i++) await new Promise(resolve => setImmediate(resolve)); };
  await settle();
  const text = id => window.document.getElementById(id).textContent;
  assert.equal(text('shipName'), shipId === 'ship_pusat-plus' ? 'Pusat Plus' : 'Phoenix');
  assert.equal(text('factionBadge'), company.toUpperCase());
  assert.equal(text('shipSpeed'), '300');
  assert.equal(text('pilotCount'), '0 nearby');
  assert.equal(text('safeZone'), 'Protected');
  x = 2800; peaceRadius = 500;
  await poll();
  assert.equal(text('safeZone'), 'Outside (radius 500)');
  peaceRadius = 1000;
  await poll();
  assert.equal(text('safeZone'), 'Protected');
  x = 2000;
  await poll();
  assert.equal(window.document.getElementById('adminPanel').hidden, false);
  for (const label of ['Home base', 'Gate 2', 'Streuner', 'Bonus', 'Pilot'])
    assert.ok(draws.some(call => call[0] === 'fillText' && call[1] === label), `${company}: missing ${label}`);
  for (const [entity, color] of [['station', '#85dce4'], ['NPC', '#f58465'],
    ['bonus box', '#f7c759'], ['drone', '#b9a2ff'], ['P.E.T.', '#7eebbd'],
    ['pilot ship', '#f7d774']])
    assert.ok(draws.some(call => call[0] === 'fill' && call[1] === color),
      `${company}: ${entity} was not painted`);
  nearby = [{ id: 2, name: 'Wingmate', x: 4000, y: 2500 }];
  draws.length = 0;
  await poll();
  assert.equal(text('pilotCount'), '1 nearby');
  assert.ok(draws.some(call => call[0] === 'fillText' && call[1] === 'Wingmate'),
    `${company}: nearby pilot label was not painted`);
  assert.ok(draws.some(call => call[0] === 'fill' && call[1] === '#65c8f5'),
    `${company}: nearby ship was not painted`);
  nearby = [{ id: 2, name: 'Wingmate', x: 5000, y: 3500 }];
  draws.length = 0;
  await poll();
  assert.ok(draws.some(call => call[0] === 'moveTo' && Math.abs(call[2] - 5000 * 1000 / 21000) < 0.01),
    `${company}: nearby ship did not move`);
  nearby = [];
  draws.length = 0;
  await poll();
  assert.equal(text('pilotCount'), '0 nearby');
  assert.ok(!draws.some(call => call[0] === 'fillText' && call[1] === 'Wingmate'),
    `${company}: departed pilot remained visible`);
  window.document.getElementById('spaceCanvas').dispatchEvent(new window.MouseEvent('click',
    { bubbles: true, clientX: 500, clientY: 300 }));
  await settle();
  assert.ok(calls.some(call => call.url.endsWith('/move') && call.body.x === 10500 && call.body.y === 6550));
  assert.equal(text('position'), '10500 / 6550');
  assert.equal(text('safeZone'), 'Outside (radius 1000)');
  rejectNextStateForLease = true;
  await poll();
  assert.equal(text('status'), 'Map ownership changed. Try again.');
  assert.equal(text('position'), '10500 / 6550');
  await poll();
  assert.equal(text('position'), '10500 / 6550');
  rejectNextMoveForLease = true;
  window.document.getElementById('spaceCanvas').dispatchEvent(new window.MouseEvent('click',
    { bubbles: true, clientX: 700, clientY: 300 }));
  await settle();
  assert.equal(text('status'), 'Map ownership changed. Try again.');
  assert.equal(text('position'), '10500 / 6550');
  window.document.getElementById('spaceCanvas').dispatchEvent(new window.MouseEvent('click',
    { bubbles: true, clientX: 700, clientY: 300 }));
  await settle();
  assert.equal(text('position'), '14700 / 6550');
  rejectNextTargetForLease = true;
  window.document.getElementById('spaceCanvas').dispatchEvent(new window.MouseEvent('click',
    { bubbles: true, clientX: 333, clientY: 321 }));
  await settle();
  assert.equal(text('status'), 'Map ownership changed. Try again.');
  assert.equal(text('target'), 'None');
  window.document.getElementById('spaceCanvas').dispatchEvent(new window.MouseEvent('click',
    { bubbles: true, clientX: 333, clientY: 321 }));
  await settle();
  assert.match(text('target'), /Streuner/);
  window.document.getElementById('adminCommand').value = '/var set world.movement.speed_multiplier 2';
  window.document.getElementById('adminCommandForm').dispatchEvent(new window.Event('submit',
    { bubbles: true, cancelable: true }));
  await settle();
  assert.equal(text('shipSpeed'), '600');
  assert.match(text('adminHistory'), /InGameAdmin/);
  window.document.getElementById('adminReason').value = 'Restore original speed';
  window.document.querySelector('#adminHistory button').click();
  await settle();
  assert.equal(text('shipSpeed'), '300');
  assert.equal(history[0].newValue, '1');
  // A teleport from another replica becomes visible on the next state poll.
  mapName = '5-1'; x = 3400; y = 2100;
  await poll();
  assert.equal(text('mapName'), 'Map 5-1');
  assert.equal(text('position'), '3400 / 2100');
  assert.equal(text('safeZone'), 'Outside (radius 1000)');
  results.push({ company, ship: text('shipName'), position: text('position'),
    canvasLabels: ['Home base', 'Gate 2', 'Streuner', 'Bonus', 'Pilot'],
    speedAfterSet: 600, speedAfterRollback: 300, requests: calls.length });
  window.close();
}
console.log(JSON.stringify({ surface: 'actual LocalSpacemapPage HTML/JS in jsdom',
  transport: 'synthetic fetch responses', results }));
