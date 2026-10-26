// Fetch only the public, versioned command center categories asset.
import { writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { resolve } from 'node:path';

const repo = resolve(fileURLToPath(new URL('../..', import.meta.url)));
const destination = resolve(repo, '_reversing/Requests/command-center-categories.xml');
const url = 'https://darkorbit-22.bpsecure.com/resources/command-center/categories.xml?__cv=cda88151fed5847635e9b06abc645100';
const response = await fetch(url, { signal: AbortSignal.timeout(10000) });
if (!response.ok) throw new Error(`Asset request returned HTTP ${response.status}`);
const body = Buffer.from(await response.arrayBuffer());
if (body.length === 0 || body.length > 1_000_000 || !body.toString('utf8').trimStart().startsWith('<'))
  throw new Error('Response is not a bounded XML asset');
await writeFile(destination, body);
process.stdout.write(`Saved ${body.length} bytes to ${destination}\n`);
