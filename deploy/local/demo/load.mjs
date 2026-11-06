// Fills a local stack with the demo household in one go: signs Bob and Alice in, switches the bank on, imports both files with
// their dates ending last month, and validates Léa's childcare months. Running it again changes nothing.
// Throwaway: it uses the static API key of the local Pocket ID, and refuses any other host. Never on a real deployment.
//   node deploy/local/demo/load.mjs        (the stack running: `aspire run`; Playwright installed: `pnpm --dir web install`)
//   NID_URL (default https://nid.localtest.me), NID_IDP_URL (default https://auth.localtest.me), NID_IDP_API_KEY
import { createHash } from 'node:crypto';
import { createRequire } from 'node:module';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { household } from './generate.mjs';

const NID = process.env.NID_URL ?? 'https://nid.localtest.me';
const IDP = process.env.NID_IDP_URL ?? 'https://auth.localtest.me';
const IDP_API_KEY = process.env.NID_IDP_API_KEY ?? 'nid-local-smoke-api-key-0123456789abcdef';
process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0'; // Caddy's local certificate authority, as sign-in-link.mjs
for (const url of [NID, IDP]) if (!new URL(url).hostname.endsWith('localtest.me')) throw new Error(`${url} is not a local stack: the demo only loads on *.localtest.me.`);

// Playwright comes from the web app's dependencies: Pocket ID's sign-in page runs in the browser.
const { chromium } = createRequire(join(dirname(fileURLToPath(import.meta.url)), '../../../web/package.json'))('@playwright/test');

const now = new Date();
const previous = new Date(Date.UTC(now.getFullYear(), now.getMonth() - 1, 1));
const anchor = previous.toISOString().slice(0, 7);
const files = household(anchor);

const fail = (message) => {
  console.error(`\n${message}`);
  process.exit(1);
};

/** A Pocket ID session from a one-time access token (web/e2e/global-setup.ts), then Nid's real OIDC flow in a browser. */
async function signIn(browser, username) {
  const headers = { 'X-API-KEY': IDP_API_KEY, 'Content-Type': 'application/json' };
  const idp = async (method, path, body) => {
    const response = await fetch(IDP + path, { method, headers, body: body && JSON.stringify(body) }).catch(() => null);
    if (!response?.ok) fail(`Pocket ID refused ${method} ${path} (${response?.status ?? 'no answer'}): is the stack running (aspire run)?`);
    return response.json();
  };
  const user = (await idp('GET', '/api/users?pagination[limit]=100')).data.find((u) => u.username === username);
  if (!user) fail(`${username} is not in Pocket ID: deploy/local/bootstrap-pocket-id.mjs creates alice, bob and carol.`);
  const { token } = await idp('POST', `/api/users/${user.id}/one-time-access-token`, {});

  const context = await browser.newContext({ ignoreHTTPSErrors: true, baseURL: NID });
  const exchanged = await context.request.post(`${IDP}/api/one-time-access-token/${token}`);
  if (!exchanged.ok()) fail(`Pocket ID refused ${username}'s one-time token (${exchanged.status()}): it allows about five a minute, wait and run again.`);
  const page = await context.newPage();
  await page.goto(`/auth/login?returnUrl=${encodeURIComponent('/dashboard')}`);
  await page.waitForURL((url) => url.pathname === '/dashboard', { timeout: 30_000 }).catch(() => fail(`${username} could not sign in to Nid (${page.url()}).`));
  const { csrf } = await (await context.request.get('/auth/session')).json();

  const call = async (method, path, data) => {
    const response = await context.request.fetch(path, { method, data, headers: { 'X-Nid-Csrf': csrf } });
    if (!response.ok()) fail(`${username}: ${method} ${path} -> ${response.status()} ${await response.text()}`);
    return response.status() === 204 ? null : response.json();
  };
  return { username, call };
}

/** RFC 8785 for the demo's values: strings, integers, booleans and null (web/src/lib/canonical-json.ts does the general case). */
const canonical = (value) =>
  Array.isArray(value) ? `[${value.map(canonical).join(',')}]`
    : value && typeof value === 'object' ? `{${Object.keys(value).sort().map((k) => `${JSON.stringify(k)}:${canonical(value[k])}`).join(',')}}`
      : JSON.stringify(value);

/** The import protocol of docs/import-format.md §11: a session, batches of 500, validation, commit. */
async function importFile({ username, call }, file) {
  for (const open of (await call('GET', '/api/v1/platform/imports')).filter((i) => ['open', 'validated', 'invalid'].includes(i.state)))
    await call('DELETE', `/api/v1/platform/imports/${open.id}`);
  const { id } = await call('POST', '/api/v1/platform/imports', { format: file.format, version: file.version });
  for (const [section, entities] of Object.entries(file).filter(([, v]) => Array.isArray(v))) {
    for (let n = 0; n * 500 < entities.length; n++) {
      const slice = entities.slice(n * 500, (n + 1) * 500);
      await call('PUT', `/api/v1/platform/imports/${id}/batches/${section}/${n}`, { hash: createHash('sha256').update(canonical(slice)).digest('hex'), entities: slice });
    }
  }
  const validated = await call('POST', `/api/v1/platform/imports/${id}/validate`, {});
  if (validated.state !== 'validated') {
    const errors = validated.report?.issues ?? [];
    const list = errors.slice(0, 10).map((i) => `  ${i.code} at ${i.path}: ${i.message}`).join('\n');
    const stale = errors.some((i) => i.code === 'import.conflict') ? '\nThe stack holds a demo of another month: start from an empty base (README, « Données de démonstration »).' : '';
    fail(`${username}'s import was refused:\n${list}${stale}`);
  }
  const committed = await call('POST', `/api/v1/platform/imports/${id}/commit`, {});
  const created = Object.values(committed.report?.sections ?? {}).reduce((total, s) => total + (s.created ?? 0), 0);
  console.log(`${username}: imported, ${created} new rows.`);
}

/** Every month of each arrangement up to last month, saved as the server plans it and validated: the server computes the pay. */
async function validateChildcare({ call }) {
  for (const arrangement of (await call('GET', '/api/v1/childcare')).items) {
    let validated = 0;
    for (let at = new Date(`${arrangement.start.slice(0, 7)}-01T00:00:00Z`); at <= previous; at.setUTCMonth(at.getUTCMonth() + 1)) {
      const path = `/api/v1/childcare/${arrangement.id}/statements/${at.toISOString().slice(0, 7)}`;
      const draft = await call('GET', path);
      if (draft.state === 'validated') continue;
      const days = draft.days.map(({ date, present, arrival, departure, meal, absence }) => ({ date, present, arrival, departure, meal, absence }));
      const saved = await call('PUT', path, { days, mileage: '0', cmg: draft.cmgEstimate ?? '0', version: draft.version });
      await call('POST', `${path}/validate`, { version: saved.version });
      validated++;
    }
    console.log(`${arrangement.name}: ${validated} months validated.`);
  }
}

const browser = await chromium.launch();
try {
  // Bob first: an adult exists in Nid from their first sign-in, and Alice's file shares items with him.
  const bob = await signIn(browser, 'bob');
  const alice = await signIn(browser, 'alice');
  await alice.call('PATCH', '/api/v1/admin/household', { bankingEnabled: true });
  console.log(`Demo household, dates up to ${anchor}:`);
  await importFile(alice, files.alice);
  await importFile(bob, files.bob);
  await validateChildcare(alice);
  console.log(`\nDone: ${NID}. To look around, a sign-in link: node deploy/local/demo/sign-in-link.mjs alice`);
} finally {
  await browser.close();
}
