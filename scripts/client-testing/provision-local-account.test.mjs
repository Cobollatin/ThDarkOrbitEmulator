import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { test } from 'node:test';
import { provisionLocalAccount } from './provision-local-account.mjs';

test('provisions through the versioned gateway route and accepts existing accounts', async () => {
  const requests = [];
  const server = createServer(async (request, response) => {
    let body = '';
    for await (const chunk of request) body += chunk;
    requests.push({ method: request.method, url: request.url, body: JSON.parse(body) });
    response.writeHead(requests.length === 1 ? 200 : 409).end();
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  try {
    const url = `http://127.0.0.1:${server.address().port}`;
    assert.equal(await provisionLocalAccount('synthetic-user', 'synthetic-password', url), 200);
    assert.equal(await provisionLocalAccount('synthetic-user', 'synthetic-password', url), 409);
    assert.deepEqual(requests, [200, 409].map(() => ({
      method: 'POST', url: '/api/v1/identity/register',
      body: { username: 'synthetic-user', password: 'synthetic-password' },
    })));
  } finally {
    server.closeAllConnections();
    await new Promise(resolve => server.close(resolve));
  }
});

test('rejects non-loopback gateway and unsuccessful registration', async () => {
  await assert.rejects(provisionLocalAccount('synthetic-user', 'synthetic-password', 'https://example.com'), /loopback/);
  const server = createServer((_request, response) => response.writeHead(503).end());
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  try {
    await assert.rejects(
      provisionLocalAccount('synthetic-user', 'synthetic-password', `http://127.0.0.1:${server.address().port}`),
      /HTTP 503/,
    );
  } finally {
    server.closeAllConnections();
    await new Promise(resolve => server.close(resolve));
  }
});
