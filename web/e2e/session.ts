import { expect, type Page } from '@playwright/test';

// The e2e project cannot import src/ (the « @/ » alias is not resolved here), so ids are listed with version 999: seen whatever the guide's version.
const GUIDE_IDS = ['dashboard', 'subscriptions', 'investments', 'income', 'properties', 'loans', 'routine', 'import', 'export'];
const PASSWORD = 'Nid-Dev-User-1!'; // deploy/dev/setup.sh: throwaway development users

/** Signs in through the real OIDC flow and Authelia's portal, then lands on `path` with every guide marked seen, so no introduction covers the journeys. */
export async function signIn(page: Page, user: 'alice' | 'bob', path = '/'): Promise<void> {
  await page.goto(`/auth/login?returnUrl=${encodeURIComponent(path)}`);
  await page.waitForURL(/auth\.localtest\.me/);
  await page.locator('#username-textfield').fill(user);
  await page.locator('#password-textfield').fill(PASSWORD);
  await page.locator('#sign-in-button').click();
  await page.waitForURL((url) => url.hostname === 'nid.localtest.me' && url.pathname + url.search === path);
  const { csrf } = (await (await page.request.get('/auth/session')).json()) as { csrf: string };
  const response = await page.request.patch('/api/v1/core/me/preferences', { headers: { 'X-Nid-Csrf': csrf }, data: { guidesSeen: Object.fromEntries(GUIDE_IDS.map((id) => [id, 999])) } });
  expect(response.ok()).toBe(true);
  await page.reload();
  await page.waitForURL((url) => url.pathname + url.search === path);
}

/** One active import per person: a previous failed run must not block this one. */
export async function cancelActiveImports(page: Page): Promise<void> {
  const session = (await (await page.request.get('/auth/session')).json()) as { csrf: string };
  const imports = (await (await page.request.get('/api/v1/platform/imports')).json()) as Array<{ id: string; state: string }>;
  for (const active of imports.filter((i) => ['open', 'validated', 'invalid'].includes(i.state))) {
    const response = await page.request.delete(`/api/v1/platform/imports/${active.id}`, { headers: { 'X-Nid-Csrf': session.csrf } });
    expect(response.ok()).toBe(true);
  }
}
