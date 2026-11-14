import { createHmac } from 'node:crypto';
import { expect, type Browser, type Page } from '@playwright/test';
import { GUIDE_IDS } from '../session';

/** The invented household of the local journeys: passwords only these tests type. */
export const ALICE = { login: 'alice', password: 'a long walk to the lighthouse' };
export const BOB = { login: 'bob', password: 'seven green parrots at noon' };
export const BOB_RESET = 'a brand new sentence for bob';

/** The password field itself: « Afficher le mot de passe », its toggle, also holds the words. */
export const passwordField = (page: Page, label = 'Mot de passe') => page.getByLabel(label, { exact: true });

/**
 * Signs in on the /login page as a person does, then marks every guide seen so no introduction covers the journey. With
 * `code`, the code step that follows a right password when two-step verification is on.
 */
export async function signInWithPassword(page: Page, login: string, password: string, path = '/dashboard', code?: () => string): Promise<void> {
  await page.goto(`/login?returnUrl=${encodeURIComponent(path)}`);
  await page.getByLabel('Identifiant').fill(login);
  await passwordField(page).fill(password);
  await page.getByRole('button', { name: 'Se connecter' }).click();
  if (code) {
    await page.getByLabel('Code', { exact: true }).fill(code());
    await page.getByRole('button', { name: 'Valider' }).click();
  }
  await page.waitForURL((url) => url.pathname === path);
  await markGuidesSeen(page);
  // The guide of a first visit is already open: the page again, without it.
  await page.reload();
}

export async function markGuidesSeen(page: Page): Promise<void> {
  const { csrf } = (await (await page.request.get('/auth/session')).json()) as { csrf: string };
  const response = await page.request.patch('/api/v1/core/me/preferences', {
    headers: { 'X-Nid-Csrf': csrf }, data: { guidesSeen: Object.fromEntries(GUIDE_IDS.map((id) => [id, 999])) },
  });
  expect(response.ok()).toBe(true);
}

/** A fresh browser, its own cookies: another person, or the same one on another device. */
export async function newPage(browser: Browser): Promise<Page> {
  return (await browser.newContext()).newPage();
}

/** Creates a person's one-time link from their row's menu on Administration › Utilisateurs and reads it from the dialog. */
export async function createLink(admin: Page, name: string, item: 'Créer son lien d\'accès' | 'Créer un lien de mot de passe'): Promise<string> {
  await admin.goto('/admin/users');
  await admin.getByRole('button', { name: `Actions de ${name}` }).click();
  await admin.getByRole('menuitem', { name: item }).click();
  const dialog = admin.getByRole('dialog', { name: 'Lien à usage unique' });
  const url = await dialog.getByRole('textbox', { name: 'Lien à usage unique' }).inputValue();
  expect(url).toMatch(/\/password#nid_link_/);
  await dialog.getByRole('button', { name: 'Fermer' }).first().click();
  return url;
}

/** RFC 6238 as the authenticator apps compute it (SHA-1, 6 digits, 30 s), from the key the Security page shows. */
export function totpCode(base32: string, stepsAhead = 0): string {
  const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
  const bytes: number[] = [];
  let buffer = 0;
  let bits = 0;
  for (const c of base32.replace(/\s/g, '')) {
    buffer = (buffer << 5) | alphabet.indexOf(c);
    bits += 5;
    if (bits >= 8) {
      bytes.push((buffer >> (bits - 8)) & 0xff);
      bits -= 8;
    }
  }
  const counter = Buffer.alloc(8);
  counter.writeBigInt64BE(BigInt(Math.floor(Date.now() / 1000 / 30) + stepsAhead));
  const hash = createHmac('sha1', Buffer.from(bytes)).update(counter).digest();
  const offset = hash[hash.length - 1] & 0x0f;
  const value = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
  return String(value % 1_000_000).padStart(6, '0');
}
