// Register the captured login identity with the local emulator before Redirect AutoLogin.
// Credentials stay in memory and in the POST body; only an HTTP status is reported.
export async function provisionLocalAccount(username, password, gatewayUrl = 'http://127.0.0.1:60953') {
  const endpoint = new URL('/api/v1/identity/register', gatewayUrl);
  if (!['localhost', '127.0.0.1', '[::1]'].includes(endpoint.hostname)) {
    throw new Error('local account provisioning requires a loopback gateway');
  }
  const response = await fetch(endpoint, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ username, password }),
    signal: AbortSignal.timeout(15000),
  });
  // The registration handler refreshes Keycloak credentials before returning 409 for an existing account.
  if (response.status === 200 || response.status === 409) return response.status;
  throw new Error(`local account provisioning failed (HTTP ${response.status})`);
}
