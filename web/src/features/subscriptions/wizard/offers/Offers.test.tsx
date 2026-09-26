import { screen, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
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
  it('records a prepaid term with free months, followed by a phase', async () => {
    const fake = serve();
    const { user, container } = await begin(/Prépayé/);
    await user.type(screen.getByLabelText("Date d'achat"), '2025-01-01');
    await user.type(screen.getByLabelText('Prix payé'), '179,86');
    await user.clear(screen.getByLabelText('Durée payée'));
    await user.type(screen.getByLabelText('Durée payée'), '24');
    await user.type(screen.getByLabelText('Mois offerts'), '3');
    await expectNoAxeViolations(container);
    await next(user);
    expect(screen.getByRole('radio', { name: 'Un abonnement récurrent' })).toBeChecked();
    await user.type(screen.getByLabelText('Montant'), '9,99');
    await expectNoAxeViolations(container);
    await next(user);
    await user.click(screen.getByRole('button', { name: "Ajouter l'abonnement" }));

    await screen.findByText(/introuvable/i);
    expect(fake.bodies[0]).toMatchObject({
      purchases: [{ kind: 'prepaid_term', grants: [{ source: 'paid', duration: { count: 24 } }, { source: 'bonus', duration: { count: 3 } }] }],
      phases: [{ startsOn: '2025-01-01', price: { amount: '9.99' } }],
    });
  });

  it('records a launch price with its normal price, the next start computed', async () => {
    const fake = serve();
    const { user } = await begin(/Essai ou offre de lancement/);
    await user.click(screen.getByRole('radio', { name: 'Prix de lancement' }));
    await user.type(screen.getByLabelText('Début'), '2025-01-31');
    await user.type(screen.getByLabelText('Prix réduit'), '0,99');
    await user.type(screen.getByLabelText('Nombre de cycles'), '1');
    await next(user);
    expect(screen.getByLabelText('Début du prix normal')).toHaveValue('2025-02-28');
    await user.type(screen.getByLabelText('Montant'), '9,99');
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
});
