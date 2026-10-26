// Hold a real jump response while attempting another jump through the shipped page.
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
let entered, jumpResponse, releaseJump, jumpStatus;
const requests = [];
window.fetch = async (path, options = {}) => {
  const target = new URL(path, baseUrl);
  requests.push(`${options.method ?? 'GET'} ${target.pathname}`);
  const response = await fetch(target, { ...options, headers: { ...(options.headers ?? {}), cookie: `dosid=${dosid}` } });
  if (target.pathname.endsWith('/enter') && response.ok) entered = await response.clone().json();
  if (target.pathname.endsWith('/jump')) {
    jumpStatus = response.status;
    if (response.ok) jumpResponse = await response.clone().json();
    await new Promise(resolve => { releaseJump = resolve; });
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
try {
  window.eval(html.match(/<script>([\s\S]*?)<\/script>/)?.[1]);
  await until(() => entered && window.document.getElementById('mapName').textContent === `Map ${entered.map.name}`, 'entry missing');
  const jump = window.document.getElementById('jump');
  jump.click();
  await until(() => releaseJump, 'jump response was not held');
  assert.equal(jumpStatus, 200);
  assert.equal(jump.disabled, true);
  jump.dispatchEvent(new window.MouseEvent('click', { bubbles: true }));
  await pause(100);
  assert.equal(requests.filter(item => item === 'POST /spacemap/world/jump').length, 1, 'a second jump was submitted');
  releaseJump();
  await until(() => window.document.getElementById('mapName').textContent === `Map ${jumpResponse.map.name}`, 'destination not rendered');
  assert.equal(jump.disabled, false);
  assert.equal(window.document.getElementById('status').textContent, `Jumped to map ${jumpResponse.map.name}`);
  assert.notEqual(jumpResponse.map.id, entered.map.id);
  console.log(JSON.stringify({ oldMap: entered.map.id, newMap: jumpResponse.map.id, jumpRequests: 1, jumpStatus, buttonReenabled: true }));
} finally {
  releaseJump?.();
  dom.window.close();
}
