import { expect, test, type Page } from '@playwright/test';
import { pick, toasts } from './controls';
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

/** The list shows 50 documents a page (issue #420): a journey searches its own stamp so its rows are on the first one. */
const searching = (path: string, text: string) => `${path}${path.includes('?') ? '&' : '?'}q=${encodeURIComponent(text)}`;

async function uploadAndOpen(page: Page, title: string): Promise<void> {
  await page.goto(searching('/documents?tab=inbox', title));
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
  await expect(toasts(alice)).toContainText('Lien ajouté.');
  await expect(alice.getByRole('list', { name: 'Liens du document' })).toContainText(shared);

  await alice.goto(searching('/documents', first));
  await expect(alice.getByRole('table', { name: 'Liste des documents' }).getByRole('row').filter({ hasText: first })).toContainText(shared);

  await uploadAndOpen(alice, second);
  await link(alice, secret, 'Bulletin de paie');
  await expect(toasts(alice)).toContainText('Lien ajouté.');

  const bob = await browser.newPage();
  await signIn(bob, 'bob', searching('/documents', String(stamp)));
  const table = bob.getByRole('table', { name: 'Liste des documents' });
  await expect(table.getByRole('row').filter({ hasText: first })).toBeVisible();
  await expect(bob.getByText(second)).toHaveCount(0);
});

test('alice files two uploaded documents at once from « À classer »', async ({ page }) => {
  const stamp = Date.now();
  const item = `Box classée ${stamp}`;
  const first = `Janvier ${stamp}`;
  const second = `Février ${stamp}`;
  await signIn(page, 'alice', '/documents');
  await addItem(page, item, 'private');

  await page.goto(searching('/documents?tab=inbox', String(stamp)));
  await page.getByLabel('Déposer').setInputFiles([pdf(`${first}.pdf`), pdf(`${second}.pdf`)]);
  await expect(page.getByRole('list', { name: 'Dépôts' }).getByText('Déposé')).toHaveCount(2);
  const table = page.getByRole('table', { name: 'Liste des documents' });
  await table.getByRole('checkbox', { name: `Sélectionner ${first}` }).check();
  await table.getByRole('checkbox', { name: `Sélectionner ${second}` }).check();
  await expect(page.getByText('2 documents sélectionnés')).toBeVisible();

  await page.getByRole('button', { name: 'Classer', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Classer 2 documents' });
  await pick(dialog, 'Élément', item);
  await pick(dialog, 'Rôle', 'Facture');
  await dialog.getByRole('button', { name: 'Classer', exact: true }).click();

  await expect(toasts(page)).toContainText('2 documents classés.');
  await expect(table.getByRole('link', { name: first })).toHaveCount(0);
  await expect(table.getByRole('link', { name: second })).toHaveCount(0);
  await page.getByRole('tab', { name: 'Tous' }).click();
  for (const title of [first, second]) await expect(table.getByRole('row').filter({ hasText: title })).toContainText(item);
});
