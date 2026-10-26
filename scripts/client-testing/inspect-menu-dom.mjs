#!/usr/bin/env node
// Inspect every CDP page/iframe target and frame without reading form values.
// Output contains structure and navigation labels only; URLs lose query/fragment data.
import fs from 'node:fs';

const args = process.argv.slice(2);
const output = args[args.indexOf('--out') + 1];
if (!args.includes('--out') || !output) throw new Error('Pass --out <session-file>');
const cleanUrl = (raw) => { try { const u = new URL(raw); return `${u.origin}${u.pathname}`; } catch { return ''; } };
const cleanAction = (raw) => { try { const a = new URL(raw).searchParams.get('action'); return /^internal[A-Za-z0-9]+$/.test(a || '') ? a : null; } catch { return null; } };
const targets = await (await fetch('http://127.0.0.1:9222/json/list')).json();
const result = { capturedAt: new Date().toISOString(), targets: [] };
const expression = `(() => {
  const clean = raw => { try { const u = new URL(raw, document.URL); return u.origin + u.pathname; } catch { return ''; } };
  const visible = e => { const r = e.getBoundingClientRect(), s = getComputedStyle(e); return r.width > 0 && r.height > 0 && s.display !== 'none' && s.visibility !== 'hidden'; };
  const label = e => (e.innerText || e.getAttribute('aria-label') || e.getAttribute('title') || '').trim().replace(/\\s+/g, ' ').slice(0, 100);
  return {
    path: clean(document.URL), action: (() => { const a = new URL(document.URL).searchParams.get('action'); return /^internal[A-Za-z0-9]+$/.test(a || '') ? a : null; })(), title: document.title,
    bodyTag: document.body?.tagName || null,
    elementCount: document.querySelectorAll('*').length,
    elements: [...document.querySelectorAll('body *')].slice(0, 1000).map(e => ({
      tag:e.tagName.toLowerCase(), id:e.id, classes:String(e.className).slice(0,120),
      role:e.getAttribute('role') || undefined, visible:visible(e),
      text:visible(e) && !/^(input|textarea|select|option)$/i.test(e.tagName)
        ? [...e.childNodes].filter(n => n.nodeType === Node.TEXT_NODE).map(n => n.textContent).join(' ').trim().replace(/\\s+/g,' ').slice(0,80) : undefined
    })),
    structure: [...document.querySelectorAll('body, iframe, frame, nav, canvas, main, header, aside, form, [role=menu], [role=menubar]')].slice(0, 200).map(e => ({tag:e.tagName.toLowerCase(), id:e.id, classes:String(e.className).slice(0,120), src:e.hasAttribute('src')?clean(e.getAttribute('src')):undefined, visible:visible(e)})),
    navigation: [...document.querySelectorAll('a,button,[role=button],[onclick],nav li')].filter(visible).slice(0, 500).map(e => ({tag:e.tagName.toLowerCase(), id:e.id, classes:String(e.className).slice(0,120), text:label(e), href:e.hasAttribute('href')?clean(e.getAttribute('href')):undefined})).filter(e => e.text || e.id || e.href)
  };
})()`;
for (const target of targets.filter(t => ['page', 'iframe'].includes(t.type))) {
  const record = { type: target.type, path: cleanUrl(target.url), action: cleanAction(target.url), frames: [], errors: [] };
  result.targets.push(record);
  if (!target.webSocketDebuggerUrl) { record.errors.push('no debugger URL'); continue; }
  const ws = new WebSocket(target.webSocketDebuggerUrl);
  try {
    await new Promise((resolve, reject) => { ws.onopen = resolve; ws.onerror = reject; });
    let nextId = 0; const pending = new Map();
    ws.onmessage = event => { const msg = JSON.parse(event.data); const p = pending.get(msg.id); if (p) { pending.delete(msg.id); msg.error ? p.reject(Error(msg.error.message)) : p.resolve(msg.result); } };
    const send = (method, params = {}) => new Promise((resolve, reject) => {
      const id = ++nextId; pending.set(id,{resolve,reject}); ws.send(JSON.stringify({id,method,params}));
      setTimeout(() => { if (pending.delete(id)) reject(Error(`${method} timeout`)); }, 5000);
    });
    await send('Runtime.enable'); await send('Page.enable');
    const tree = await send('Page.getFrameTree');
    const walk = node => { const frame = { id:node.frame.id, parentId:node.frame.parentId, path:cleanUrl(node.frame.url), action:cleanAction(node.frame.url) }; record.frames.push(frame); for (const child of node.childFrames || []) walk(child); };
    walk(tree.frameTree);
    for (const frame of record.frames) {
      try {
        const world = await send('Page.createIsolatedWorld',{frameId:frame.id,worldName:'menu-inspection'});
        const evalResult = await send('Runtime.evaluate',{expression,contextId:world.executionContextId,returnByValue:true});
        frame.dom = evalResult.result?.value || null;
        if (evalResult.exceptionDetails) frame.error = evalResult.exceptionDetails.text;
      } catch (e) { frame.error = e.message; }
    }
  } catch (e) { record.errors.push(e.message); } finally { ws.close(); }
}
fs.writeFileSync(output, JSON.stringify(result, null, 2));
console.log(JSON.stringify({output,targets:result.targets.length,frames:result.targets.reduce((n,t)=>n+t.frames.length,0)}));
