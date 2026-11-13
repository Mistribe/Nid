// Finishes a demo-mode instance (Nid__Demo__Enabled, deploy/README.md §14): Nid loads the two nid.data files itself on start,
// this adds what the files cannot hold, as load.mjs does on a local stack: Léa's childcare months validated, then seed.mjs
// (documents, tax notices, scenarios, the CPI). Signed in as Alice through /auth/demo/alice: no identity provider, no
// browser. Running it again changes nothing. The nightly reset of the public demo runs it (.github/workflows/demo-reset.yml).
//   NID_URL=https://demo.example.org node deploy/local/demo/finish.mjs
import { offset } from './generate.mjs';
import { seed, validateChildcare } from './seed.mjs';

const NID = (process.env.NID_URL ?? '').replace(/\/$/, '');
if (!NID.startsWith('https://')) {
  console.error('Set NID_URL to the https address of a demo-mode instance.');
  process.exit(1);
}

const fail = (message) => {
  console.error(`\n${message}`);
  process.exit(1);
};

// The session cookie the demo sign-in sets (__Host-nid), then the CSRF token every write carries.
const signIn = await fetch(`${NID}/auth/demo/alice?returnUrl=%2F`, { redirect: 'manual' });
const cookie = signIn.headers.getSetCookie().map((c) => c.split(';')[0]).find((c) => c.startsWith('__Host-nid='));
if (!cookie) fail(`${NID} did not sign in as Alice (${signIn.status}): is it in demo mode, with its household loaded?`);
const session = await (await fetch(`${NID}/auth/session`, { headers: { Cookie: cookie } })).json();
if (!session.demo || !session.authenticated) fail(`${NID} is not a demo-mode instance: nothing done.`);

/** The `call` of load.mjs, over fetch: JSON or multipart, a failure stops everything unless its status is allowed. */
async function call(method, path, data, { multipart, allow = [] } = {}) {
  const headers = { Cookie: cookie, 'X-Nid-Csrf': session.csrf, Origin: NID };
  let body;
  if (multipart) {
    body = new FormData();
    for (const [name, value] of Object.entries(multipart)) {
      if (value && typeof value === 'object' && value.buffer) body.append(name, new Blob([value.buffer], { type: value.mimeType }), value.name);
      else body.append(name, String(value));
    }
  } else if (data !== undefined) {
    body = JSON.stringify(data);
    headers['Content-Type'] = 'application/json';
  }
  const response = await fetch(NID + path, { method, headers, body });
  if (!response.ok && !allow.includes(response.status)) fail(`${method} ${path} -> ${response.status} ${await response.text()}`);
  return response.status === 204 ? null : response.json();
}

const now = new Date();
const previous = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth() - 1, 1));
const anchor = previous.toISOString().slice(0, 7);
await validateChildcare({ call }, previous);
const seeded = await seed({ call }, anchor, offset(anchor));
console.log(`Through the API: ${seeded.length ? seeded.join(', ') : 'nothing new'}.`);
