// Throwaway (smoke, CI, local trial): sets up, in a Pocket ID started with STATIC_API_KEY (deploy/compose.local.yaml),
// what Nid needs: the groups nid-users and nid-admins, three users (alice in both groups, bob in nid-users, carol in
// none) and Nid's client, restricted to nid-users. Prints `<client id> <client secret>` for deploy/setup.sh and, when
// NID_CLIENT_SECRET_FILE is set, writes the secret there (the Aspire dev stack reads it as a key-per-file secret).
// Safe to rerun: what exists is kept (a user without its fixed id is made again), and a new client secret is added each time. Never run it on a real deployment.
//   POCKET_ID_URL (default https://auth.localtest.me), POCKET_ID_API_KEY, NID_URL (default https://nid.localtest.me),
//   NID_CLIENT_SECRET_FILE (optional)
const base = process.env.POCKET_ID_URL ?? 'https://auth.localtest.me';
const nid = process.env.NID_URL ?? 'https://nid.localtest.me';
const key = process.env.POCKET_ID_API_KEY ?? 'nid-local-smoke-api-key-0123456789abcdef';
// The local proxy's certificate comes from its own authority.
process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0';
const CLIENT_ID = 'nid';

// A container just started answers a few seconds late.
for (let attempt = 0; ; attempt += 1) {
  try {
    if ((await fetch(`${base}/.well-known/openid-configuration`)).ok) break;
  } catch { /* not listening yet */ }
  if (attempt >= 60) throw new Error(`Pocket ID does not answer at ${base}`);
  await new Promise((resolve) => setTimeout(resolve, 2000));
}

async function api(method, path, body, { allow = [] } = {}) {
  const response = await fetch(base + path, {
    method,
    headers: { 'X-API-KEY': key, 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const text = await response.text();
  if (!response.ok && !allow.includes(response.status)) throw new Error(`${method} ${path} -> ${response.status} ${text}`);
  try { return text ? JSON.parse(text) : null; } catch { return text; }
}
const items = (page) => (Array.isArray(page) ? page : page?.data ?? []);

// The first user of a fresh Pocket ID is created through the setup route; later runs find it done.
await api('POST', '/api/signup/setup', { username: 'root', email: 'root@example.test', firstName: 'Root', lastName: 'Admin' }, { allow: [400, 403, 409] });

async function ensureGroup(name) {
  const existing = items(await api('GET', `/api/user-groups?pagination[limit]=100`)).find((g) => g.name === name);
  return existing ?? api('POST', '/api/user-groups', { name, friendlyName: name });
}
// Fixed ids: Nid binds a person to the `sub` of their first sign-in, and its database outlives Pocket ID's (two volumes). With
// random ids a fresh Pocket ID gives alice a new `sub`, and Nid refuses her (core.email_taken). The ids are the `sub` claims.
const USER_IDS = {
  alice: '00000000-0000-4000-8000-0000000a11ce',
  bob: '00000000-0000-4000-8000-00000000b0b0',
  carol: '00000000-0000-4000-8000-00000000ca01',
};
async function ensureUser(username, first) {
  const id = USER_IDS[username];
  const existing = items(await api('GET', `/api/users?pagination[limit]=100`)).find((u) => u.username === username);
  if (existing?.id === id) return existing;
  // A user made before the fixed ids has a random one, which cannot be changed: it is made again (its passkeys go with it).
  if (existing) await api('DELETE', `/api/users/${existing.id}`);
  return api('POST', '/api/users', { id, username, email: `${username}@example.test`, firstName: first, lastName: 'Dev', displayName: `${first} Dev`, isAdmin: false });
}

const users = await ensureGroup('nid-users');
const admins = await ensureGroup('nid-admins');
const alice = await ensureUser('alice', 'Alice');
const bob = await ensureUser('bob', 'Bob');
await ensureUser('carol', 'Carol');
await api('PUT', `/api/user-groups/${users.id}/users`, { userIds: [alice.id, bob.id] });
await api('PUT', `/api/user-groups/${admins.id}/users`, { userIds: [alice.id] });

const client = {
  id: CLIENT_ID,
  name: 'Nid',
  callbackURLs: [`${nid}/auth/callback`],
  logoutCallbackURLs: [`${nid}/signed-out`],
  isPublic: false,
  pkceEnabled: true,
  isGroupRestricted: true,
  requiresReauthentication: false,
  // The household's own application: no consent screen at every first sign-in.
  skipConsent: true,
};
const existingClient = items(await api('GET', '/api/oidc/clients?pagination[limit]=100')).find((c) => c.id === CLIENT_ID);
if (existingClient) await api('PUT', `/api/oidc/clients/${CLIENT_ID}`, { ...existingClient, ...client });
else await api('POST', '/api/oidc/clients', client);
await api('PUT', `/api/oidc/clients/${CLIENT_ID}/allowed-user-groups`, { userGroupIds: [users.id] });
const secret = (await api('POST', `/api/oidc/clients/${CLIENT_ID}/secrets`)).secret;

if (process.env.NID_CLIENT_SECRET_FILE) {
  const { mkdirSync, writeFileSync } = await import('node:fs');
  const { dirname } = await import('node:path');
  mkdirSync(dirname(process.env.NID_CLIENT_SECRET_FILE), { recursive: true });
  writeFileSync(process.env.NID_CLIENT_SECRET_FILE, secret);
}
console.log(`${CLIENT_ID} ${secret}`);
