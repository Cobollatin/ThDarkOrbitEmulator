#!/usr/bin/env node
// Prints only action names from game-host indexInternal URLs in saved sessions.
import fs from 'node:fs';
for (const file of process.argv.slice(2)) {
  const har = JSON.parse(fs.readFileSync(file, 'utf8'));
  const actions = new Map();
  for (const entry of har.log?.entries || []) {
    let url; try { url = new URL(entry.request?.url); } catch { continue; }
    if (!/^gbl\d+\.darkorbit\.com$/i.test(url.hostname) || url.pathname !== '/indexInternal.es') continue;
    const action = url.searchParams.get('action');
    if (!/^internal[A-Za-z0-9]+$/.test(action || '')) continue;
    const key = `${entry.request.method} ${action}`;
    const item = actions.get(key) || { action:key, count:0, statuses:{}, redirectActions:{} };
    item.count++;
    const status = String(entry.response?.status ?? 'unknown');
    item.statuses[status] = (item.statuses[status] || 0) + 1;
    const location = entry.response?.headers?.find(h => h.name?.toLowerCase() === 'location')?.value;
    if (location) {
      try {
        const next = new URL(location, url).searchParams.get('action');
        if (/^internal[A-Za-z0-9]+$/.test(next || '')) item.redirectActions[next] = (item.redirectActions[next] || 0) + 1;
      } catch {}
    }
    actions.set(key, item);
  }
  console.log(JSON.stringify({session:file.replace(/\\/g,'/').split('/').at(-2), actions:[...actions.values()]}));
}
