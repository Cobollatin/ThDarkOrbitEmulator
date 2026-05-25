import { spawn } from 'node:child_process';

/** Aspire sets PORT; fallback for standalone `npm start`. */
const port = process.env.PORT ?? '4300';

const child = spawn('npx', ['ng', 'serve', '--port', port], {
  stdio: 'inherit',
  shell: true,
  env: process.env
});

child.on('exit', (code, signal) => {
  if (signal) {
    process.kill(process.pid, signal);
    return;
  }

  process.exit(code ?? 0);
});
