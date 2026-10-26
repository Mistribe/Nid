import { expect, test, type Page } from '@playwright/test';
import { pick } from './controls';
import { signIn } from './session';

const pdf = (name: string) => ({ name, mimeType: 'application/pdf', buffer: Buffer.from('%PDF-1.4\n1 0 obj<<>>endobj\ntrailer<<>>\n%%EOF\n') });

/** A subscription is a Core item: the page links to it. */
async function addItem(page: Page, displayName: string, visibility: 'shared' | 'private'): Promise<void> {
  const session = (await (await page.request.get('/auth/session')).json()) as { csrf: string; person: { id: string } };
  const response = await page.request.post('/api/v1/subscriptions', {
    headers: { 'X-Nid-Csrf': session.csrf },
    data: { displayName, visibility, owners: [{ personId: session.person.id, shareBp: 10000 }], autoRenew: true },
  });
  expect(response.status()).toBe(201);
}

async function uploadAndOpen(page: Page, title: string): Promise<void> {
  await page.goto('/documents?tab=inbox');
  await page.getByLabel('Déposer').setInputFiles(pdf(`${title}.pdf`));
  await expect(page.getByRole('list', { name: 'Dépôts' })).toContainText('Déposé');
  await page.getByRole('table', { name: 'Liste des documents' }).getByRole('link', { name: title }).click();
  await expect(page.getByRole('heading', { level: 1, name: title })).toBeVisible();
}

async function link(page: Page, item: string, role: string): Promise<void> {
  await page.getByRole('button', { name: 'Ajouter un lien' }).click();
  const dialog = page.getByRole('dialog', { name: 'Ajouter un lien' });
  await pick(dialog, 'Élément', item);
  await pick(dialog, 'Rôle', role);
  await dialog.getByRole('button', { name: 'Ajouter', exact: true }).click();
}

test('alice files a document under a shared item and bob sees it; a private link stays hidden from bob', async ({ browser }) => {
  const stamp = Date.now();
  const shared = `Box partagée ${stamp}`;
  const secret = `Coffre privé ${stamp}`;
  const first = `Facture ${stamp}`;
  const second = `Bulletin ${stamp}`;
  const alice = await browser.newPage();
  await signIn(alice, 'alice', '/documents');
  await addItem(alice, shared, 'shared');
  await addItem(alice, secret, 'private');

  await uploadAndOpen(alice, first);
  await expect(alice.getByText('Ce document n’est relié à rien')).toBeVisible();
  await link(alice, shared, 'Facture');
  await expect(alice.getByText(/pourra lire ce document/)).toContainText('Bob');
  await alice.getByRole('button', { name: 'Ajouter quand même' }).click();
  await expect(alice.getByRole('status').filter({ hasText: /\S/ })).toHaveText('Lien ajouté.');
  await expect(alice.getByRole('list', { name: 'Liens du document' })).toContainText(shared);

  await alice.goto('/documents');
  await expect(alice.getByRole('table', { name: 'Liste des documents' }).getByRole('row').filter({ hasText: first })).toContainText(shared);

  await uploadAndOpen(alice, second);
  await link(alice, secret, 'Bulletin de paie');
  await expect(alice.getByRole('status').filter({ hasText: /\S/ })).toHaveText('Lien ajouté.');

  const bob = await browser.newPage();
  await signIn(bob, 'bob', '/documents');
  const table = bob.getByRole('table', { name: 'Liste des documents' });
  await expect(table.getByRole('row').filter({ hasText: first })).toBeVisible();
  await expect(bob.getByText(second)).toHaveCount(0);
});
