import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { chromium, request, type FullConfig } from '@playwright/test';

// Pocket ID limits the exchange of one-time access tokens to about five a minute, and the journeys sign in dozens of times: the
// session of each user is opened once here, and signIn (session.ts) puts its cookies in each browser context. They live an hour
// at Pocket ID; the cache file below is reused for half of that, so several playwright runs in a row (deploy/smoke.sh) do not
// each exchange tokens again.
const IDP = process.env.NID_IDP_URL ?? 'https://auth.localtest.me';
const IDP_API_KEY = process.env.NID_IDP_API_KEY ?? 'nid-local-smoke-api-key-0123456789abcdef';
const USERS = ['alice', 'bob'];
const MAX_AGE_MS = 30 * 60 * 1000;
const CACHE = join(tmpdir(), `nid-e2e-idp-sessions-${Buffer.from(IDP).toString('hex').slice(0, 24)}.json`);

type Cookie = { name: string; value: string; domain: string; path: string; expires: number; httpOnly: boolean; secure: boolean; sameSite: 'Strict' | 'Lax' | 'None' };
type Cache = { at: number; sessions: Record<string, Cookie[]> };

function cached(): Record<string, Cookie[]> | null {
  if (!existsSync(CACHE)) return null;
  try {
    const cache = JSON.parse(readFileSync(CACHE, 'utf8')) as Cache;
    return Date.now() - cache.at < MAX_AGE_MS && USERS.every((u) => cache.sessions[u]?.length) ? cache.sessions : null;
  } catch {
    return null;
  }
}

export default async function globalSetup(config: FullConfig): Promise<void> {
  let sessions = cached();
  if (!sessions) {
    sessions = {};
    const headers = { 'X-API-KEY': IDP_API_KEY };
    const api = await request.newContext({ ignoreHTTPSErrors: true });
    const list = await api.get(`${IDP}/api/users?pagination[limit]=100`, { headers });
    if (!list.ok()) throw new Error(`The identity provider does not answer at ${IDP} (${list.status()}): start the stack first.`);
    const users = ((await list.json()) as { data: Array<{ id: string; username: string }> }).data;
    for (const username of USERS) {
      const id = users.find((u) => u.username === username)?.id;
      if (!id) throw new Error(`${username} does not exist in the identity provider (deploy/local/bootstrap-pocket-id.mjs creates alice, bob and carol).`);
      const token = await api.post(`${IDP}/api/users/${id}/one-time-access-token`, { headers, data: {} });
      if (!token.ok()) throw new Error(`No one-time access token for ${username}: ${token.status()}`);
      const user = await request.newContext({ ignoreHTTPSErrors: true });
      const exchanged = await user.post(`${IDP}/api/one-time-access-token/${((await token.json()) as { token: string }).token}`);
      if (!exchanged.ok()) throw new Error(`The one-time access token of ${username} was refused: ${exchanged.status()} (rate limit: wait a minute).`);
      sessions[username] = (await user.storageState()).cookies;
      await user.dispose();
    }
    await api.dispose();
    writeFileSync(CACHE, JSON.stringify({ at: Date.now(), sessions } satisfies Cache));
  }
  process.env.NID_IDP_SESSIONS = JSON.stringify(sessions);
  await signEveryoneIn(config, sessions);
}

/**
 * Nid creates an adult at their first sign-in, and the journeys name both adults (alice shares an envelope with bob): both
 * sign in once here, through a real browser and the real OIDC flow, so each exists whatever the order the journeys run in.
 */
async function signEveryoneIn(config: FullConfig, sessions: Record<string, Cookie[]>): Promise<void> {
  const { baseURL, channel } = config.projects[0].use;
  const browser = await chromium.launch({ channel });
  try {
    for (const [username, cookies] of Object.entries(sessions)) {
      const context = await browser.newContext({ ignoreHTTPSErrors: true });
      await context.addCookies(cookies);
      const page = await context.newPage();
      await page.goto(`${baseURL}/auth/login?returnUrl=${encodeURIComponent('/dashboard')}`);
      await page.waitForURL((url) => url.pathname === '/dashboard', { timeout: 30_000 }).catch(() => {
        throw new Error(`${username} could not sign in to Nid (${page.url()}): is the identity provider's client set up for Nid, and ${username} in nid-users?`);
      });
      await context.close();
    }
  } finally {
    await browser.close();
  }
}
