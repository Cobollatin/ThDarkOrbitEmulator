import { readFileSync } from 'node:fs';
import { Script } from 'node:vm';

const page = readFileSync(new URL('../../ThDarkOrbitEmulator.ApiService/Features/World/LocalSpacemapPage.cs', import.meta.url), 'utf8');
const source = page.match(/<script>([\s\S]*?)<\/script>/)?.[1];
if (!source) throw new Error('Spacemap script was not found.');
new Script(source, { filename: 'LocalSpacemapPage' });
console.log('Spacemap JavaScript parses.');
