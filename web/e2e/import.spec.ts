import { expect, test } from '@playwright/test';
import { cancelActiveImports, signIn } from './session';

test('imports a small nid.data file and finds its subscription', async ({ page }) => {
  // Unique ids per run: a second run creates, rather than finds everything unchanged.
  const stamp = Date.now().toString(36);
  const name = `Journal E2E ${stamp}`;
  const file = {
    format: 'nid.data',
    version: 1,
    persons: [{ ref: 'moi', kind: 'adult', display_name: 'Alice', email: 'alice@example.test' }],
    subscriptions: [{ id: `e2e-${stamp}`, provider_key: 'spotify', name, ownership: [{ person: 'moi', share_bp: 10000 }], auto_renew: true }],
    billing_phases: [{
      id: `e2e-${stamp}-ph`, subscription_id: `e2e-${stamp}`, starts_on: '2026-01-01', ends_on: null,
      price: { amount: 9.90, currency: 'EUR' }, every: { count: 1, unit: 'month' }, payer: { type: 'household' },
    }],
  };

  await signIn(page, 'alice', '/import');
  await cancelActiveImports(page);
  await page.reload();

  await page.getByLabel('Choisir un fichier').setInputFiles({
    name: 'e2e.nid.json', mimeType: 'application/json', buffer: Buffer.from(JSON.stringify(file, null, 2)),
  });
  await expect(page.getByText('Lots à envoyer : 3.')).toBeVisible();
  await expect(page.getByLabel('Alice (moi)')).not.toHaveValue('');

  await page.getByRole('button', { name: "Commencer l'import" }).click();
  await expect(page.getByRole('heading', { name: 'Rapport de validation' })).toBeVisible();
  await expect(page.getByRole('row', { name: /Abonnements/ })).toContainText('1');

  await page.getByRole('button', { name: 'Importer' }).click();
  await expect(page.getByRole('heading', { name: 'Import terminé' })).toBeVisible();

  await page.goto('/subscriptions?scope=me');
  await expect(page.getByText(name)).toBeVisible();
});

test('re-imports a renamed subscription with « Remplacer » and finds the new name', async ({ page }) => {
  const stamp = Date.now().toString(36);
  const file = (name: string) => ({
    format: 'nid.data',
    version: 1,
    persons: [{ ref: 'moi', kind: 'adult', display_name: 'Alice', email: 'alice@example.test' }],
    subscriptions: [{ id: `e2e-r-${stamp}`, provider_key: 'spotify', name, ownership: [{ person: 'moi', share_bp: 10000 }], auto_renew: true }],
    billing_phases: [{
      id: `e2e-r-${stamp}-ph`, subscription_id: `e2e-r-${stamp}`, starts_on: '2026-01-01', ends_on: null,
      price: { amount: 9.90, currency: 'EUR' }, every: { count: 1, unit: 'month' }, payer: { type: 'household' },
    }],
  });
  const importFile = async (name: string) => {
    await page.goto('/import');
    await page.getByLabel('Choisir un fichier').setInputFiles({
      name: 'e2e.nid.json', mimeType: 'application/json', buffer: Buffer.from(JSON.stringify(file(name))),
    });
    await page.getByRole('button', { name: "Commencer l'import" }).click();
  };

  await signIn(page, 'alice', '/import');
  await cancelActiveImports(page);
  await importFile(`Avant ${stamp}`);
  await page.getByRole('button', { name: 'Importer' }).click();
  await expect(page.getByRole('heading', { name: 'Import terminé' })).toBeVisible();

  await importFile(`Après ${stamp}`);
  await page.getByRole('checkbox', { name: 'Remplacer ces éléments par le contenu du fichier' }).check();
  await expect(page.getByRole('columnheader', { name: 'Remplacés' })).toBeVisible();
  await page.getByRole('button', { name: 'Importer' }).click();
  await expect(page.getByText('Remplacement : oui')).toBeVisible();

  await page.goto('/subscriptions');
  await expect(page.getByRole('link', { name: new RegExp(`Après ${stamp}`) })).toBeVisible();
  await expect(page.getByText(`Avant ${stamp}`)).toHaveCount(0);
});
