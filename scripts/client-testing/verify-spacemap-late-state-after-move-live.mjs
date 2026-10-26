// A state poll completed before a newer map click must not repaint its destination.
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
let releaseState, oldState, moveState;
const requests = [];
window.fetch = async (path, options = {}) => {
  const target = new URL(path, baseUrl);
  requests.push(`${options.method ?? 'GET'} ${target.pathname}`);
  const response = await fetch(target, { ...options, headers: { ...(options.headers ?? {}), cookie: `dosid=${dosid}` } });
  if (target.pathname.endsWith('/state') && !oldState) {
    oldState = await response.clone().json();
    await new Promise(resolve => { releaseState = resolve; });
  }
  if (target.pathname.endsWith('/move')) moveState = await response.clone().json();
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
  await until(() => releaseState, 'first state response was not held');
  const canvas = window.document.getElementById('spaceCanvas');
  canvas.dispatchEvent(new window.MouseEvent('click', { bubbles: true, clientX: 720, clientY: 470 }));
  await until(() => moveState && window.document.getElementById('destination').textContent === `${moveState.pilot.targetX} / ${moveState.pilot.targetY}`, 'move did not render');
  const destination = window.document.getElementById('destination').textContent;
  const position = window.document.getElementById('position').textContent;
  assert.notEqual(`${oldState.pilot.targetX} / ${oldState.pilot.targetY}`, destination);
  releaseState();
  await pause(100);
  assert.equal(window.document.getElementById('destination').textContent, destination);
  assert.equal(window.document.getElementById('position').textContent, position);
  assert.equal(requests.filter(item => item === 'GET /spacemap/world/state').length, 1);
  assert.equal(requests.filter(item => item === 'POST /spacemap/world/move').length, 1);
  console.log(JSON.stringify({ destination, staleStateDiscarded: true }));
} finally {
  releaseState?.();
  dom.window.close();
}
