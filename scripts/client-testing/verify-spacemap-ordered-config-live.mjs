// Hold the first configuration command before HTTP delivery, then click the other configuration.
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
let releaseFirst, configRequests = 0;
const outcomes = [];
window.fetch = async (path, options = {}) => {
  const url = new URL(path, baseUrl);
  if (url.pathname.endsWith('/config') && options.method === 'POST') {
    configRequests++;
    if (configRequests === 1) await new Promise(resolve => { releaseFirst = resolve; });
  }
  const response = await fetch(url, {
    ...options, headers: { ...(options.headers ?? {}), cookie: `dosid=${dosid}` }
  });
  if (url.pathname.endsWith('/config') && options.method === 'POST')
    outcomes.push({ selected: JSON.parse(options.body).configuration, status: response.status });
  return response;
};
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
const until = async (predicate, label) => {
  for (let i = 0; i < 100; i++) {
    if (predicate()) return;
    await pause(50);
  }
  throw new Error(`${label}: requests=${configRequests}, outcomes=${JSON.stringify(outcomes)}`);
};
try {
  window.eval(html.match(/<script>([\s\S]*?)<\/script>/)?.[1]);
  await until(() => window.document.getElementById('position').textContent.includes('/'), 'entry');
  assert.equal(window.document.getElementById('config1').getAttribute('aria-pressed'), 'true');
  window.document.getElementById('config2').click();
  await until(() => releaseFirst, 'first configuration request');
  window.document.getElementById('config1').click();
  await pause(100);
  assert.equal(configRequests, 1, 'later click overtook the pending configuration');
  releaseFirst();
  await until(() => outcomes.length === 2, 'queued configuration result');
  assert.deepEqual(outcomes, [{ selected: 2, status: 200 }, { selected: 1, status: 409 }]);
  await until(() => window.document.getElementById('config2').getAttribute('aria-pressed') === 'true', 'authoritative configuration');
  assert.match(window.document.getElementById('status').textContent, /Configuration ready in/);
  const response = await window.fetch('/spacemap/world/state');
  assert.equal((await response.json()).pilot.configuration, 2);
  console.log(JSON.stringify({ configRequests, outcomes,
    renderedConfig: 2, status: window.document.getElementById('status').textContent }));
} finally {
  releaseFirst?.();
  window.close();
}
