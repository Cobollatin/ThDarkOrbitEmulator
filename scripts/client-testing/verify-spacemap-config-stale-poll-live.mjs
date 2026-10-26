// Hold a state poll started before a configuration switch, then release it after the switch completes.
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
const polls = [];
window.setInterval = callback => { polls.push(callback); return polls.length; };
let holdState = false, staleState, releaseStale, configStatus, statesAfterConfig = 0;
window.fetch = async (path, options = {}) => {
  const url = new URL(path, baseUrl);
  const isState = url.pathname.endsWith('/state');
  const response = await fetch(url, {
    ...options, headers: { ...(options.headers ?? {}), cookie: `dosid=${dosid}` }
  });
  if (url.pathname.endsWith('/config') && options.method === 'POST') configStatus = response.status;
  else if (isState && configStatus) statesAfterConfig++;
  if (isState && holdState) {
    holdState = false;
    staleState = await response.clone().json();
    await new Promise(resolve => { releaseStale = resolve; });
  }
  return response;
};
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
const pressed = id => window.document.getElementById(id).getAttribute('aria-pressed');
const until = async (predicate, label) => {
  for (let i = 0; i < 100; i++) {
    if (predicate()) return;
    await pause(50);
  }
  throw new Error(`${label}: config=${configStatus}, statesAfterConfig=${statesAfterConfig}`);
};
try {
  window.eval(html.match(/<script>([\s\S]*?)<\/script>/)?.[1]);
  await until(() => window.document.getElementById('position').textContent.includes('/'), 'entry');
  assert.equal(pressed('config1'), 'true');
  holdState = true;
  polls.forEach(poll => poll());
  await until(() => releaseStale, 'held poll');
  assert.equal(staleState.pilot.configuration ?? staleState.configuration ?? 1, 1, 'held poll is not stale');
  window.document.getElementById('config2').click();
  await until(() => configStatus, 'configuration request');
  assert.equal(configStatus, 200);
  await pause(100);
  releaseStale();
  await until(() => statesAfterConfig > 0, 'follow-up state poll');
  await pause(150);
  assert.equal(pressed('config2'), 'true', 'stale poll restored configuration 1');
  assert.equal(pressed('config1'), 'false');
  console.log(JSON.stringify({ configStatus, staleConfiguration: 1, statesAfterConfig,
    rendered: { config1: pressed('config1'), config2: pressed('config2') },
    status: window.document.getElementById('status').textContent }));
} finally { window.close(); }
