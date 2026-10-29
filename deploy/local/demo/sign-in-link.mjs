// Prints a one-time sign-in link for a development user (alice, bob or carol), who has no passkey yet: open it, then
// https://nid.localtest.me signs in through Pocket ID without a prompt. Works against the Aspire stack (`aspire run`).
// Throwaway: it uses the static API key of the local Pocket ID. Never on a real deployment.
//   node deploy/local/demo/sign-in-link.mjs alice
//   POCKET_ID_URL (default http://localhost:1411), POCKET_ID_API_KEY, POCKET_ID_PUBLIC_URL (default https://auth.localtest.me)
const username = process.argv[2] ?? 'alice';
const base = process.env.POCKET_ID_URL ?? 'http://localhost:1411';
const key = process.env.POCKET_ID_API_KEY ?? 'nid-local-smoke-api-key-0123456789abcdef';
const publicUrl = process.env.POCKET_ID_PUBLIC_URL ?? 'https://auth.localtest.me';
process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0';

async function api(method, path, body) {
  const response = await fetch(base + path, { method, headers: { 'X-API-KEY': key, 'Content-Type': 'application/json' }, body: body && JSON.stringify(body) });
  if (!response.ok) throw new Error(`${method} ${path} -> ${response.status} ${await response.text()} (is the stack running?)`);
  return response.json();
}

const users = await api('GET', '/api/users?pagination[limit]=100');
const user = (users.data ?? users).find((u) => u.username === username);
if (!user) throw new Error(`No user ${username} in Pocket ID (deploy/local/bootstrap-pocket-id.mjs creates alice, bob and carol).`);
// An hour, not the default 15 minutes: Pocket ID makes a token that short-lived 6 characters long, and its sign-in page asks for
// the 12 of a longer-lived one unless email sign-in is on.
const { token } = await api('POST', `/api/users/${user.id}/one-time-access-token`, { ttl: '1h' });
console.log(`${publicUrl}/lc/${token}`);
