import { expect, test } from '@playwright/test';
import { signIn } from './session';

test('alice follows a private PEA and bob never sees it', async ({ browser }) => {
  const name = `PEA ${Date.now()}`;
  const alice = await browser.newPage();
  await signIn(alice, 'alice', '/investments/new');

  await alice.getByLabel('Nom').fill(name);
  await alice.getByLabel('Type').selectOption('pea');
  await alice.getByRole('radio', { name: /Privé/ }).check();
  await alice.getByRole('checkbox', { name: 'Versements antérieurs' }).check();
  await alice.getByLabel('Date des versements antérieurs').fill('2024-12-31');
  await alice.getByLabel('Montant des versements antérieurs').fill('20000');
  await alice.getByRole('checkbox', { name: 'Première valeur' }).check();
  await alice.getByLabel('Date de la première valeur').fill('2024-12-31');
  await alice.getByLabel('Montant de la première valeur').fill('21000');
  await alice.getByRole('button', { name: 'Créer l’enveloppe' }).click();

  await expect(alice.getByRole('heading', { name, level: 1 })).toBeVisible();
  const url = alice.url();

  const flow = alice.getByRole('form', { name: 'Nouveau flux' });
  await flow.getByLabel('Nature').selectOption('contribution');
  await flow.getByLabel('Date').fill('2025-06-15');
  await flow.getByLabel('Montant').fill('2000');
  await flow.getByRole('button', { name: 'Ajouter le flux' }).click();
  await expect(flow.getByRole('status')).toHaveText('Flux ajouté.');

  const value = alice.getByRole('form', { name: 'Nouvelle valeur' });
  await value.getByLabel('Date').fill('2025-09-30');
  await value.getByLabel('Montant').fill('25000');
  await value.getByRole('button', { name: 'Enregistrer la valeur' }).click();

  const figures = alice.getByRole('region', { name: 'Valeur et gain' });
  await expect(figures).toContainText('3 000,00 €');
  await expect(figures).toContainText('13,6 %');

  // 2B2b: a shared Livret, a transfer into it, the period performance, then closing and reopening the Livret.
  const livret = `Livret ${Date.now()}`;
  await alice.goto('/investments/new');
  await alice.getByLabel('Nom').fill(livret);
  await alice.getByLabel('Type').selectOption('savings');
  await alice.getByRole('radio', { name: /Partagé/ }).check();
  await alice.getByRole('button', { name: 'Créer l’enveloppe' }).click();
  await expect(alice.getByRole('heading', { name: livret, level: 1 })).toBeVisible();
  const livretUrl = alice.url();

  await alice.goto(url);
  await alice.getByRole('button', { name: 'Virer vers une autre enveloppe' }).click();
  const transfer = alice.getByRole('dialog', { name: 'Virement' });
  await transfer.getByLabel('Vers').selectOption({ label: livret });
  await transfer.getByLabel('Montant').fill('500');
  await transfer.getByRole('button', { name: 'Virer', exact: true }).click();
  await expect(alice.getByRole('region', { name: 'Flux', exact: true }).getByRole('status')).toHaveText('Virement enregistré.');

  const period = alice.getByRole('region', { name: 'Performance d’une période' });
  await expect(period.getByRole('radio', { name: 'Depuis le début de l’année' })).toBeChecked();
  await expect(period.getByText('Versements nets de la période')).toBeVisible();
  await expect(period.getByText('Performance', { exact: true })).toBeVisible();

  await alice.goto(livretUrl);
  await alice.getByRole('button', { name: 'Clôturer l’enveloppe' }).click();
  const closing = alice.getByRole('dialog', { name: 'Clôturer l’enveloppe' });
  await closing.getByRole('button', { name: 'Clôturer', exact: true }).click();
  await expect(alice.getByRole('status').filter({ hasText: 'Enveloppe clôturée.' })).toBeVisible();

  await alice.goto('/investments');
  const list = alice.getByRole('list', { name: 'Liste des enveloppes' });
  await expect(list).toBeVisible();
  await expect(list.getByText(livret)).toHaveCount(0);
  await alice.getByRole('checkbox', { name: 'Afficher les clôturées' }).check();
  const closedRow = list.getByRole('listitem').filter({ hasText: livret });
  await expect(closedRow).toContainText('Clôturée');
  await closedRow.getByRole('link', { name: livret }).click();
  await alice.getByRole('button', { name: 'Rouvrir' }).click();
  await expect(alice.getByRole('status').filter({ hasText: 'Enveloppe rouverte.' })).toBeVisible();

  const bob = await browser.newPage();
  await signIn(bob, 'bob', '/investments');
  // The household scope lists every envelope alice and bob can each see; waiting for the loaded list or the empty
  // state (rather than the always-rendered heading) is a positive signal that the query has actually settled.
  await expect(bob.getByRole('list', { name: 'Liste des enveloppes' }).or(bob.getByRole('heading', { name: 'Aucune enveloppe pour l’instant' }))).toBeVisible();
  await expect(bob.getByText(name)).toHaveCount(0);
  await bob.goto(url);
  await expect(bob.getByText('Cet élément est introuvable.')).toBeVisible();
});
