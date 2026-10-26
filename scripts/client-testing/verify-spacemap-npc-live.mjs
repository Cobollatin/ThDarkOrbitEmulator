// Runs the shipped Spacemap HTML/JS against live HTTP and observes a remote NPC kill.
// Usage: node verify-spacemap-npc-live.mjs <baseUrl> <dosid>
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
const context = new Proxy({}, { get: (target, key) => target[key] ?? (() => {}),
  set: (target, key, value) => { target[key] = value; return true; } });
window.HTMLCanvasElement.prototype.getContext = () => context;
window.HTMLCanvasElement.prototype.getBoundingClientRect = () => ({ left: 0, top: 0, width: 1000, height: 600 });
let poll, lastNpcs;
window.setInterval = callback => { poll = callback; return 1; };
window.fetch = async (path, options = {}) => {
  const response = await fetch(new URL(path, baseUrl),
    { ...options, headers: { ...(options.headers ?? {}), cookie: `dosid=${dosid}` } });
  if (response.ok && (path.endsWith('/state') || path.endsWith('/enter')))
    lastNpcs = (await response.clone().json()).npcs;
  return response;
};
window.eval(html.match(/<script>([\s\S]*?)<\/script>/)?.[1]);
const settle = async () => { for (let i = 0; i < 40; i++) await new Promise(resolve => setTimeout(resolve, 5)); };
await settle();
const count = () => window.document.getElementById('npcCount').textContent;
assert.equal(count(), '2 visible');
assert.equal(lastNpcs.length, 2);
const target = lastNpcs[0];
const response = await fetch(new URL(`/test/remote-kill/${target.id}`, baseUrl), { method: 'POST' });
assert.equal(response.status, 200);
await new Promise(resolve => setTimeout(resolve, 650));
await poll(); await settle();
assert.equal(count(), '1 visible');
assert.ok(lastNpcs.every(npc => npc.id !== target.id));
const after = count();
window.close();
console.log(JSON.stringify({ surface: 'shipped Spacemap DOM/JS', transport: 'live HTTP',
  before: '2 visible', after, removedNpcId: target.id }));
