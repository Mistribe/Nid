import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { persons } from '../../testData';

const NEW_ID = '0192f0aa-0000-7000-8000-0000000000c1';
const OTHER = { id: '0192f0aa-0000-7000-8000-0000000000d2', displayName: 'Disney+' };

function serve() {
  const bodies: unknown[] = [];
  let answer: () => Response = () => HttpResponse.json({ id: NEW_ID }, { status: 201 });
  server.use(
    http.get('*/api/v1/core/persons', () => HttpResponse.json(persons)),
    http.get('*/api/v1/subscriptions/providers', () => HttpResponse.json([])),
    http.get('*/api/v1/subscriptions', () => HttpResponse.json({
      items: [{ ...OTHER, provider: null, autoRenew: true, cashCost: [], amortizedCost: [], findings: [] }],
      householdCashCost: [], householdAmortizedCost: [], householdCashCostConverted: null, householdAmortizedCostConverted: null,
      householdConvertedIsEstimate: false, sharedPurchaseIds: [], findings: [],
    })),
    http.post('*/api/v1/subscriptions', async ({ request }) => { bodies.push(await request.json()); return answer(); }),
    http.get(`*/api/v1/subscriptions/${NEW_ID}`, () => HttpResponse.json({ status: 404 }, { status: 404 })),
  );
  return { bodies, refuse: (errors: object[]) => { answer = () => HttpResponse.json({ status: 422, code: 'request.invalid', title: 'Refusé', errors }, { status: 422 }); } };
}

async function begin(offer: RegExp) {
  const utils = await renderApp('/subscriptions/new');
  await utils.user.click(await screen.findByRole('radio', { name: offer }));
  await utils.user.click(screen.getByRole('button', { name: 'Continuer' }));
  await utils.user.type(await screen.findByLabelText('Nom'), 'Offre');
  await utils.user.click(screen.getByRole('button', { name: 'Continuer' }));
  return utils;
}
const next = (user: Awaited<ReturnType<typeof begin>>['user']) => user.click(screen.getByRole('button', { name: 'Continuer' }));

describe('Abonnements › ajout › offres', () => {
  // The lazy route chunk loaded cold inside the first test races its timeouts under load.
  beforeAll(async () => { await import('@/features/subscriptions/SubscriptionDetailPage'); }, 30_000);

  it('offers every type with its example, clean for assistive technology', async () => {
    serve();
    const { container } = await renderApp('/subscriptions/new');
    const types = await screen.findByRole('group', { name: "Type d'offre" });
    expect(within(types).getAllByRole('radio')).toHaveLength(5);
    expect(within(types).getByRole('radio', { name: /Récurrent/ })).toBeChecked();
    await expectNoAxeViolations(container);
  });

  it('records a prepaid term with free months, followed by a phase', async () => {
    const fake = serve();
    const { user, container } = await begin(/Prépayé/);
    await user.type(screen.getByLabelText("Date d'achat"), '2025-01-01');
    await user.type(screen.getByLabelText('Prix payé'), '179,86');
    await user.clear(screen.getByLabelText('Durée payée'));
    await user.type(screen.getByLabelText('Durée payée'), '24');
    // The bonus duration's unit can be days or weeks, not just months: its label must not name one (final
    // review, finding 4).
    await user.type(screen.getByLabelText('Durée offerte'), '3');
    await expectNoAxeViolations(container);
    await next(user);
    expect(screen.getByRole('radio', { name: 'Un abonnement récurrent' })).toBeChecked();
    // The hint names the covered period, not an "offered" one: a bought or launch-priced term is covered
    // too, not offered (final review, finding 4).
    expect(screen.getByText('La facturation commence quand la période couverte se termine.')).toBeInTheDocument();
    await user.type(screen.getByLabelText('Montant'), '9,99');
    await expectNoAxeViolations(container);
    await next(user);
    // The gender-neutral wording must hold for any unit ("mois" masculine, "semaine(s)" feminine) — final
    // review, finding 4.
    const summary = await screen.findByRole('region', { name: 'Récapitulatif' });
    expect(summary).toHaveTextContent('Payé : 24 mois ; offert : 3 mois');
    await user.click(screen.getByRole('button', { name: "Ajouter l'abonnement" }));

    await screen.findByText(/introuvable/i);
    expect(fake.bodies[0]).toMatchObject({
      purchases: [{ kind: 'prepaid_term', grants: [{ source: 'paid', duration: { count: 24 } }, { source: 'bonus', duration: { count: 3 } }] }],
      // The covered term runs to 2027-04-01 (24 paid + 3 bonus months from 2025-01-01): the normal price
      // must not start before then (final review, finding 2).
      phases: [{ startsOn: '2027-04-01', price: { amount: '9.99' } }],
    });
  });

  // Final review, finding 2: the following phase's start (and the summary's first charge) must follow the
  // purchase date even after it is changed once "Et ensuite ?" already computed a (now stale) value.
  it('keeps the following phase\'s start in step with a purchase date changed after "Et ensuite ?"', async () => {
    const fake = serve();
    const { user } = await begin(/Prépayé/);
    await user.type(screen.getByLabelText("Date d'achat"), '2025-01-01');
    await user.type(screen.getByLabelText('Prix payé'), '179,86'); // paidCount defaults to 12 months, no bonus
    await next(user); // -> "Et ensuite ?": computes startsOn from the 2025-01-01 purchase
    expect(screen.getByRole('radio', { name: 'Un abonnement récurrent' })).toBeChecked();
    await user.type(screen.getByLabelText('Montant'), '9,99');
    await user.click(screen.getByRole('button', { name: 'Retour' })); // -> offer, unvalidated
    await user.clear(screen.getByLabelText("Date d'achat"));
    await user.type(screen.getByLabelText("Date d'achat"), '2025-06-01');
    await next(user); // -> "Et ensuite ?" again: startsOn must follow the new date, not the stale one
    await next(user); // -> summary

    const summary = await screen.findByRole('region', { name: 'Récapitulatif' });
    expect(summary).toHaveTextContent('1 juin 2026'); // 2025-06-01 + 12 paid months
    await user.click(screen.getByRole('button', { name: "Ajouter l'abonnement" }));

    await screen.findByText(/introuvable/i);
    expect(fake.bodies[0]).toMatchObject({ phases: [{ startsOn: '2026-06-01' }] });
  });

  it('records a launch price with its normal price, the next start computed', async () => {
    const fake = serve();
    const { user, container } = await begin(/Essai ou offre de lancement/);
    await user.click(screen.getByRole('radio', { name: 'Prix de lancement' }));
    await user.type(screen.getByLabelText('Début'), '2025-01-31');
    await user.type(screen.getByLabelText('Prix réduit'), '0,99');
    await user.type(screen.getByLabelText('Nombre de cycles'), '1');
    await expectNoAxeViolations(container);
    await next(user);
    expect(screen.getByLabelText('Début du prix normal')).toHaveValue('2025-02-28');
    await user.type(screen.getByLabelText('Montant'), '9,99');
    await expectNoAxeViolations(container);
    await next(user);
    await user.click(screen.getByRole('button', { name: "Ajouter l'abonnement" }));

    await screen.findByText(/introuvable/i);
    expect(fake.bodies[0]).toMatchObject({ phases: [{ startsOn: '2025-01-31', endsOn: '2025-02-28', maxCycles: 1 }, { startsOn: '2025-02-28' }] });
  });

  it('records a received gift card without a following phase by default', async () => {
    const fake = serve();
    const { user } = await begin(/Carte cadeau/);
    await user.type(screen.getByLabelText('Date'), '2025-01-01');
    await next(user);
    expect(screen.getByRole('radio', { name: "Rien pour l'instant" })).toBeChecked();
    await next(user);
    await user.click(screen.getByRole('button', { name: "Ajouter l'abonnement" }));

    await screen.findByText(/introuvable/i);
    expect(fake.bodies[0]).toMatchObject({ phases: [], purchases: [{ kind: 'gift', payer: { type: 'external' }, price: { amount: '0' } }] });
  });

  it('records a bought gift card in the currency typed next to its price', async () => {
    const fake = serve();
    const { user, container } = await begin(/Carte cadeau/);
    await user.type(screen.getByLabelText('Date'), '2025-01-01');
    await expectNoAxeViolations(container); // received
    await user.click(screen.getByRole('radio', { name: 'Achetée' }));
    await user.type(screen.getByLabelText('Prix'), '30');
    await user.clear(screen.getByLabelText('Devise'));
    await user.type(screen.getByLabelText('Devise'), 'usd');
    await expectNoAxeViolations(container); // bought
    await next(user);
    await next(user);
    await user.click(screen.getByRole('button', { name: "Ajouter l'abonnement" }));

    await screen.findByText(/introuvable/i);
    expect(fake.bodies[0]).toMatchObject({ phases: [], purchases: [{ kind: 'gift', price: { amount: '30', currency: 'USD' } }] });
  });

  it('records a free trial, its steps clean for assistive technology', async () => {
    const fake = serve();
    const { user, container } = await begin(/Essai ou offre de lancement/);
    await user.type(screen.getByLabelText('Début'), '2025-01-01');
    await expectNoAxeViolations(container);
    await next(user);
    await user.click(screen.getByRole('radio', { name: "Rien pour l'instant" }));
    await expectNoAxeViolations(container);
    await next(user);
    await user.click(screen.getByRole('button', { name: "Ajouter l'abonnement" }));

    await screen.findByText(/introuvable/i);
    expect(fake.bodies[0]).toMatchObject({ phases: [], purchases: [{ label: 'Essai', grants: [{ source: 'trial', startsOn: '2025-01-01' }] }] });
  });

  // Review Focus 4: a refused second other row brings the user back to the offer step, on that row.
  it('brings a refused bundle target back to its row on the offer step', async () => {
    const fake = serve();
    const { user, container } = await begin(/Bundle/);
    await user.type(screen.getByLabelText("Date d'achat"), '2025-01-01');
    await user.type(screen.getByLabelText('Prix total'), '88,99');
    await user.click(screen.getByRole('button', { name: 'Ajouter un abonnement couvert' }));
    await user.selectOptions(screen.getByLabelText('Abonnement couvert 1'), OTHER.id);
    await user.click(screen.getByRole('button', { name: 'Ajouter un abonnement couvert' }));
    await user.selectOptions(screen.getByLabelText('Abonnement couvert 2'), OTHER.id);
    await expectNoAxeViolations(container);
    await next(user);
    await next(user);
    fake.refuse([{ path: 'purchases[0].grants[2].subscriptionId', code: 'subscriptions.not_found', message: 'No subscription with this id can be written.' }]);
    await user.click(screen.getByRole('button', { name: "Ajouter l'abonnement" }));

    const row = await screen.findByRole('group', { name: 'Abonnement couvert 2' });
    expect(within(row).getByText('Cet abonnement est introuvable.')).toBeInTheDocument();
    expect(screen.getByLabelText("Date d'achat")).toBeInTheDocument(); // back on the offer step
  });

  it('brings a refused normal price back to "Et ensuite ?"', async () => {
    const fake = serve();
    const { user } = await begin(/Essai ou offre de lancement/);
    await user.click(screen.getByRole('radio', { name: 'Prix de lancement' }));
    await user.type(screen.getByLabelText('Début'), '2025-01-01');
    await user.type(screen.getByLabelText('Prix réduit'), '0,99');
    await user.type(screen.getByLabelText('Nombre de cycles'), '3');
    await next(user);
    await user.type(screen.getByLabelText('Montant'), '9,99');
    await next(user);
    fake.refuse([{ path: 'phases[1].price.amount', code: 'request.amount', message: 'bad' }]);
    await user.click(screen.getByRole('button', { name: "Ajouter l'abonnement" }));

    expect(await screen.findByLabelText('Montant')).toHaveAccessibleDescription(/montant/i);
  });

  // Final review, finding 1 (scenario A): a hidden payerPerson, left over from turning the following phase on
  // then off again, must not block "Continuer".
  it('does not block "Continuer" on a hidden payer left empty after turning the following phase off', async () => {
    serve();
    const { user } = await begin(/Prépayé/);
    await user.type(screen.getByLabelText("Date d'achat"), '2025-01-01');
    await user.type(screen.getByLabelText('Prix payé'), '179,86');
    await next(user); // -> "Et ensuite ?": prepaid defaults it on
    expect(screen.getByRole('radio', { name: 'Un abonnement récurrent' })).toBeChecked();
    await user.click(screen.getByRole('radio', { name: 'Une personne' })); // payerPerson left on "Aucun"
    await user.click(screen.getByRole('radio', { name: "Rien pour l'instant" })); // then off: the payer is now hidden
    await next(user);

    await screen.findByRole('region', { name: 'Récapitulatif' });
  });

  // Final review, finding 1 (scenario B): a hidden offerPayerPerson, left over from "Achetée", must not block
  // the offer step once the gift is "Reçue" again.
  it('does not block "Continuer" on a hidden gift payer left empty after switching back to "Reçue"', async () => {
    serve();
    const { user } = await begin(/Carte cadeau/);
    await user.type(screen.getByLabelText('Date'), '2025-01-01');
    await user.click(screen.getByRole('radio', { name: 'Achetée' }));
    await user.click(screen.getByRole('radio', { name: 'Une personne' })); // offerPayerPerson left on "Aucun"
    await user.click(screen.getByRole('radio', { name: 'Reçue' })); // the payer fields are now hidden
    await next(user);

    expect(screen.getByRole('group', { name: 'Et ensuite ?' })).toBeInTheDocument();
  });

  // Final review, finding 1 (scenario C): a stale offerPayerPerson from a prepaid offer, kept after an
  // unvalidated "Retour" to the type step, must not silently block the final submit for a recurring offer.
  it('submits a recurring offer after a stale hidden payer was left from a prepaid offer', async () => {
    const fake = serve();
    const { user } = await begin(/Prépayé/);
    await user.type(screen.getByLabelText("Date d'achat"), '2025-01-01');
    await user.type(screen.getByLabelText('Prix payé'), '179,86');
    await user.click(screen.getByRole('radio', { name: 'Une personne' })); // offerPayerPerson left on "Aucun"
    await user.click(screen.getByRole('button', { name: 'Retour' })); // -> common, unvalidated
    await user.click(screen.getByRole('button', { name: 'Retour' })); // -> type, unvalidated
    await user.click(screen.getByRole('radio', { name: /Récurrent/ }));
    await next(user); // -> common
    await next(user); // -> offer (recurring)
    await user.type(await screen.findByLabelText('Début'), '2025-01-01');
    await user.type(screen.getByLabelText('Montant'), '9,99');
    await next(user); // -> summary
    await user.click(screen.getByRole('button', { name: "Ajouter l'abonnement" }));

    await waitFor(() => expect(fake.bodies).toHaveLength(1));
  });
});
