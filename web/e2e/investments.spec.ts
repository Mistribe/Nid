import { expect, test } from '@playwright/test';
import { fillDate } from './controls';
import { signIn } from './session';

test('alice follows a private PEA and bob never sees it', async ({ browser }) => {
  const name = `PEA ${Date.now()}`;
  const alice = await browser.newPage();
  await signIn(alice, 'alice', '/investments/new');

  await alice.getByLabel('Nom').fill(name);
  await alice.getByLabel('Type').click();
  await alice.getByRole('option', { name: 'PEA' }).click();
  await alice.getByRole('radio', { name: 'Privé' }).check();
  await alice.getByRole('button', { name: 'Versements antérieurs' }).click();
  await fillDate(alice, 'Date des versements antérieurs', '2024-12-31');
  await alice.getByLabel('Montant des versements antérieurs').fill('20000');
  await alice.getByRole('button', { name: 'Première valeur' }).click();
  await fillDate(alice, 'Date de la première valeur', '2024-12-31');
  await alice.getByLabel('Montant de la première valeur').fill('21000');
  await alice.getByRole('button', { name: 'Créer l’enveloppe' }).click();

  await expect(alice.getByRole('heading', { name, level: 1 })).toBeVisible();
  const url = alice.url();

  await alice.getByRole('button', { name: 'Ajouter un flux' }).click();
  const flow = alice.getByRole('dialog', { name: 'Nouveau flux' });
  await flow.getByLabel('Nature').click();
  await alice.getByRole('option', { name: 'Versement', exact: true }).click();
  await fillDate(flow, 'Date', '2025-06-15');
  await flow.getByLabel('Montant').fill('2000');
  await flow.getByRole('button', { name: 'Ajouter le flux' }).click();
  await expect(alice.getByRole('status').filter({ hasText: 'Flux ajouté.' })).toBeVisible();

  await alice.getByRole('button', { name: 'Saisir une valeur' }).click();
  const value = alice.getByRole('dialog', { name: 'Nouvelle valeur' });
  await fillDate(value, 'Date', '2025-09-30');
  await value.getByLabel('Montant').fill('25000');
  await value.getByRole('button', { name: 'Enregistrer la valeur' }).click();

  const figures = alice.getByRole('region', { name: 'Valeur et gain' });
  await expect(figures).toContainText('3 000,00 €');
  await expect(figures).toContainText('13,6 %');

  // 2B2b: a shared Livret, a transfer into it, the period performance, then closing and reopening the Livret.
  const livret = `Livret ${Date.now()}`;
  await alice.goto('/investments/new');
  await alice.getByLabel('Nom').fill(livret);
  await alice.getByLabel('Type').click();
  await alice.getByRole('option', { name: 'Livret' }).click();
  await alice.getByRole('radio', { name: 'Partagé' }).check();
  await alice.getByRole('button', { name: 'Créer l’enveloppe' }).click();
  await expect(alice.getByRole('heading', { name: livret, level: 1 })).toBeVisible();
  const livretUrl = alice.url();

  await alice.goto(url);
  await alice.getByRole('button', { name: 'Autres actions' }).click();
  await alice.getByRole('menuitem', { name: 'Virer vers une autre enveloppe' }).click();
  const transfer = alice.getByRole('dialog', { name: 'Virement' });
  await transfer.getByLabel('Vers').click();
  await alice.getByRole('option', { name: livret }).click();
  await transfer.getByLabel('Montant').fill('500');
  await transfer.getByRole('button', { name: 'Virer', exact: true }).click();
  await expect(alice.getByRole('status').filter({ hasText: 'Virement enregistré.' })).toBeVisible();

  const period = alice.getByRole('region', { name: 'Performance d’une période' });
  await expect(period.getByRole('radio', { name: 'Depuis le début de l’année' })).toBeChecked();
  await expect(period.getByText('Versements nets de la période')).toBeVisible();
  await expect(period.getByText('Performance', { exact: true })).toBeVisible();
  // The last value on or before the period's start and end is the one of 30 September 2025, 25 000 €; nothing was contributed.
  await expect(period).toContainText('25 000,00 € · 30 septembre 2025');
  await expect(period.getByText('Pas de valeur au début de la période.')).toHaveCount(0);

  await alice.goto(livretUrl);
  await alice.getByRole('button', { name: 'Autres actions' }).click();
  await alice.getByRole('menuitem', { name: 'Clôturer l’enveloppe' }).click();
  const closing = alice.getByRole('dialog', { name: 'Clôturer l’enveloppe' });
  await closing.getByRole('button', { name: 'Clôturer', exact: true }).click();
  await expect(alice.getByRole('status').filter({ hasText: 'Enveloppe clôturée.' })).toBeVisible();

  await alice.goto('/investments?tab=list');
  const list = alice.getByRole('table', { name: 'Liste des enveloppes' });
  // In the household scope the private PEA is not listed and the Livret is closed: the list may be empty, so wait for
  // the loaded page (the list or its empty state) before checking the Livret is absent.
  await expect(list.or(alice.getByRole('heading', { name: 'Aucune enveloppe pour l’instant' }))).toBeVisible();
  await expect(alice.getByText(livret)).toHaveCount(0);
  await alice.getByRole('switch', { name: 'Afficher les clôturées' }).click();
  const closedRow = list.getByRole('row').filter({ hasText: livret });
  await expect(closedRow).toContainText('Clôturée');
  await closedRow.getByRole('link', { name: livret }).click();
  await alice.getByRole('button', { name: 'Rouvrir' }).click();
  await expect(alice.getByRole('status').filter({ hasText: 'Enveloppe rouverte.' })).toBeVisible();

  const bob = await browser.newPage();
  await signIn(bob, 'bob', '/investments?tab=list');
  // The household scope lists every envelope alice and bob can each see; waiting for the loaded list or the empty
  // state (rather than the always-rendered heading) is a positive signal that the query has actually settled.
  await expect(bob.getByRole('table', { name: 'Liste des enveloppes' }).or(bob.getByRole('heading', { name: 'Aucune enveloppe pour l’instant' }))).toBeVisible();
  await expect(bob.getByText(name)).toHaveCount(0);
  await bob.goto(url);
  await expect(bob.getByText('Cet élément est introuvable.')).toBeVisible();
});
