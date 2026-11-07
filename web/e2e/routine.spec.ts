import { expect, test } from '@playwright/test';
import { pick } from './controls';
import { signIn } from './session';

test('alice does her routine on a new envelope, reads the gain, then cancels', async ({ browser }) => {
  const name = `Routine ${Date.now()}`;
  const alice = await browser.newPage();
  await signIn(alice, 'alice', '/investments/new');
  await alice.getByLabel('Nom').fill(name);
  await pick(alice, 'Type', 'Livret');
  await alice.getByRole('radio', { name: /Partagé/ }).check();
  await alice.getByRole('button', { name: 'Créer l’enveloppe' }).click();
  await expect(alice.getByRole('heading', { name, level: 1 })).toBeVisible();

  await alice.goto('/routine');
  const due = alice.getByRole('region', { name: 'À relever' });
  await due.getByLabel(`Valeur de ${name}`).fill('1000');
  await due.getByLabel(`Versements du mois sur ${name}`).fill('100');
  // Other journeys add envelopes of alice's: skip every other due line so only this one is recorded.
  // The visible label is « Passer » alone; the envelope's name is in the checkbox's aria-label.
  for (const skip of await due.getByRole('checkbox').all()) {
    if ((await skip.getAttribute('aria-label')) !== `Passer ${name}`) await skip.check();
  }
  await alice.getByRole('button', { name: 'Enregistrer les placements' }).click();

  const recap = alice.getByRole('table', { name: 'Récapitulatif de la routine' });
  await expect(recap.getByRole('row', { name: new RegExp(name) })).toContainText('Inconnu'); // a first value: no gain yet
  await alice.getByRole('button', { name: 'Annuler' }).click();
  await expect(alice.getByText('Routine annulée.')).toBeVisible();
});
