import { screen, waitFor } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { pick } from '@/test/controls';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { persons } from '../../subscriptions/testData';
import { PROPERTY, propertyDetail } from '../testData';

function serveCreate(bodies: unknown[], answer: () => Response = () => HttpResponse.json({ id: PROPERTY }, { status: 201 })) {
  server.use(
    http.get('*/api/v1/core/persons', () => HttpResponse.json(persons)),
    http.post('*/api/v1/realestate/properties', async ({ request }) => { bodies.push(await request.json()); return answer(); }),
    http.get('*/api/v1/realestate/properties/:id', () => HttpResponse.json(propertyDetail())),
  );
}

type User = Awaited<ReturnType<typeof renderApp>>['user'];
const next = (user: User) => user.click(screen.getByRole('button', { name: 'Continuer' }));
const submit = (user: User) => user.click(screen.getByRole('button', { name: 'Créer le bien' }));
const step = () => screen.getByRole('listitem', { current: 'step' });
const options = (user: User) => user.click(screen.getByRole('button', { name: 'Plus d’options' }));

/** The first step: a name and an answered visibility. */
async function essentials(user: User, visibility: string | null = 'Oui, partagé') {
  await user.type(await screen.findByLabelText('Nom'), 'Maison de Nantes');
  if (visibility) await user.click(screen.getByRole('radio', { name: visibility }));
  await next(user);
}

/** README §12's purchase: 250 000 € with 19 000 € of notary fees on 2020-06-15. */
async function purchase(user: User, date = '15/06/2020') {
  await user.type(await screen.findByLabelText('Date d’achat'), date);
  await user.type(screen.getByLabelText('Prix d’achat'), '250000');
  await user.clear(screen.getByLabelText('Frais de notaire'));
  await user.type(screen.getByLabelText('Frais de notaire'), '19000');
}

describe('NewProperty', () => {
  beforeAll(async () => { await import('./NewProperty'); }, 30_000);

  it('creates the same property body as before, through two steps, its amounts as typed with a dot, then shows it', async () => {
    const bodies: unknown[] = [];
    serveCreate(bodies);
    const { user, router, container } = await renderApp('/properties/new');

    await user.type(await screen.findByLabelText('Nom'), 'Maison de Nantes');
    await pick(user, 'Type de bien', 'Bien locatif');
    await user.type(screen.getByLabelText('Commune ou repère'), 'Nantes');
    await user.click(screen.getByRole('radio', { name: 'Oui, partagé' }));
    await next(user);
    expect(step()).toHaveTextContent('L’achat');
    await purchase(user);
    await user.type(screen.getByLabelText('Surface (m²)'), '85,5');
    expect(screen.getByLabelText('Frais d’agence')).toHaveValue('0');
    expect(screen.getByLabelText('Autres frais d’acquisition')).toHaveValue('0');
    await expectNoAxeViolations(container);
    await submit(user);

    await waitFor(() => expect(router.state.location.pathname).toBe(`/properties/${PROPERTY}`));
    expect(bodies).toEqual([{
      name: 'Maison de Nantes', currency: 'EUR', visibility: 'shared',
      owners: [{ personId: '01926b9e-0000-7000-8000-00000000000a', shareBp: 10000 }],
      type: 'rental', purchaseDate: '2020-06-15', purchasePrice: '250000', notaryFees: '19000', agencyFees: '0',
      otherAcquisitionCosts: '0', valuationCadence: 'yearly', surfaceM2: '85.5', locationLabel: 'Nantes',
    }]);
  });

  it('bought with a loan (issue #17), opens the new loan already linked to the property and sends nothing more', async () => {
    const bodies: unknown[] = [];
    serveCreate(bodies);
    const { user, router } = await renderApp('/properties/new');

    await essentials(user);
    await purchase(user);
    expect(screen.queryByText(/vous saisissez le crédit/)).not.toBeInTheDocument();
    await user.click(screen.getByRole('checkbox', { name: 'Achat avec un crédit' }));
    expect(screen.getByText(/vous saisissez le crédit, déjà rattaché à ce bien/)).toBeInTheDocument();
    await submit(user);

    await waitFor(() => expect(router.state.location.pathname).toBe('/loans/new'));
    expect(router.state.location.search).toEqual({ property: PROPERTY });
    expect(bodies[0]).not.toHaveProperty('withLoan');
  });

  it('stays on the first step until the visibility question is answered, with neither answer checked', async () => {
    const bodies: unknown[] = [];
    serveCreate(bodies);
    const { user } = await renderApp('/properties/new');

    await essentials(user, null);
    expect(step()).toHaveTextContent('Le bien');
    expect(screen.getByRole('radiogroup', { name: 'Ce bien est-il visible de l’autre adulte ?' })).toBeInTheDocument();
    expect(screen.getByRole('radio', { name: 'Oui, partagé' })).not.toBeChecked();
    expect(screen.getByRole('radio', { name: 'Non, privé' })).not.toBeChecked();
    expect(await screen.findByText('Répondez à cette question.')).toBeInTheDocument();
    expect(bodies).toEqual([]);
  });

  it('stays on the purchase step with an invalid field: a zero price, three decimals, a date after today, a 0-day threshold', async () => {
    const bodies: unknown[] = [];
    serveCreate(bodies);
    const { user } = await renderApp('/properties/new');

    await essentials(user, 'Non, privé');
    await purchase(user, '01/01/2999');
    await user.clear(screen.getByLabelText('Prix d’achat'));
    await user.type(screen.getByLabelText('Prix d’achat'), '0');
    await user.clear(screen.getByLabelText('Frais de notaire'));
    await user.type(screen.getByLabelText('Frais de notaire'), '19000,123');
    await options(user);
    await user.type(screen.getByLabelText('Périmée après (jours)'), '0');
    await submit(user);

    expect(await screen.findByText('Un montant positif, avec deux décimales au plus, comme 1500,50.')).toBeInTheDocument();
    expect(screen.getByText('Un montant positif ou nul, avec deux décimales au plus, comme 1500,50.')).toBeInTheDocument();
    expect(screen.getByText('Cette date est dans le futur.')).toBeInTheDocument();
    expect(screen.getByText('Un nombre entier de jours, de 1 à 3 650.')).toBeInTheDocument();
    expect(step()).toHaveTextContent('L’achat');
    expect(bodies).toEqual([]);
  });

  it('folds the estimate rhythm and its threshold, and sends what is chosen there', async () => {
    const bodies: unknown[] = [];
    serveCreate(bodies);
    const { user, container } = await renderApp('/properties/new');

    await essentials(user);
    await purchase(user);
    expect(screen.getByRole('button', { name: 'Plus d’options' })).toHaveAttribute('aria-expanded', 'false');
    expect(screen.queryByLabelText('Rythme des estimations')).not.toBeInTheDocument();
    await options(user);
    await pick(user, 'Rythme des estimations', 'Manuel');
    await user.type(screen.getByLabelText('Périmée après (jours)'), '730');
    await expectNoAxeViolations(container);
    await submit(user);

    await waitFor(() => expect(bodies).toHaveLength(1));
    expect(bodies[0]).toMatchObject({ valuationCadence: 'manual', staleAfterDays: 730 });
  });

  it('sends the person back to the step of a field the server refuses, which opens the folded options holding one', async () => {
    serveCreate([], () => HttpResponse.json({
      status: 422, code: 'realestate.invalid_property_field', title: 'x',
      errors: [{ path: 'locationLabel', code: 'realestate.invalid_property_field', message: 'x' }],
    }, { status: 422 }));
    const { user } = await renderApp('/properties/new');

    await essentials(user, 'Non, privé');
    await purchase(user);
    await submit(user);

    await waitFor(() => expect(step()).toHaveTextContent('Le bien'));
    expect(await screen.findByText('Cette valeur n’est pas acceptée.')).toBeInTheDocument();
    expect(screen.getByLabelText('Commune ou repère')).toHaveAttribute('aria-invalid', 'true');
    expect(screen.getByRole('radio', { name: 'Non, privé' })).toBeChecked();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('opens the options holding a refused field of the purchase step', async () => {
    serveCreate([], () => HttpResponse.json({
      status: 422, code: 'realestate.invalid_property_field', title: 'x',
      errors: [{ path: 'staleAfterDays', code: 'realestate.invalid_property_field', message: 'x' }],
    }, { status: 422 }));
    const { user } = await renderApp('/properties/new');

    await essentials(user);
    await purchase(user);
    await submit(user);

    expect(await screen.findByText('Cette valeur n’est pas acceptée.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Plus d’options' })).toHaveAttribute('aria-expanded', 'true');
    expect(screen.getByLabelText('Périmée après (jours)')).toHaveAttribute('aria-invalid', 'true');
  });

  it('shows a refusal no field owns above the form', async () => {
    serveCreate([], () => HttpResponse.json(
      { status: 422, code: 'realestate.invalid_property_field', title: 'x', errors: [{ path: 'rentalStartsOn', code: 'realestate.invalid_property_field', message: 'x' }] },
      { status: 422 }));
    const { user } = await renderApp('/properties/new');

    await essentials(user);
    await purchase(user);
    await submit(user);

    expect(await screen.findByRole('alert')).toBeInTheDocument();
  });
});
