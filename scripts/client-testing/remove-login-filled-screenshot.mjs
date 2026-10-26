#!/usr/bin/env node
// Remove the old post-fill screenshot without opening it; the username may be visible.
import fs from 'node:fs';
import path from 'node:path';

const root = path.resolve('_reversing/Requests/sessions');
for (const session of process.argv.slice(2)) {
  const dir = path.resolve(session);
  if (!dir.startsWith(root + path.sep)) throw new Error('session must be inside the repository capture directory');
  const file = path.join(dir, 'screenshots', '02-login-filled.png');
  if (fs.existsSync(file)) { fs.unlinkSync(file); console.log(`removed login-filled screenshot from ${path.basename(dir)}`); }
}
