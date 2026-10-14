import { screen, waitFor } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
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
const submit = (user: User) => user.click(screen.getByRole('button', { name: 'Créer le bien' }));

/** README §12's purchase: 250 000 € with 19 000 € of notary fees on 2020-06-15. */
async function fill(user: User) {
  await user.type(await screen.findByLabelText('Nom'), 'Maison de Nantes');
  await user.type(screen.getByLabelText('Date d’achat'), '2020-06-15');
  await user.type(screen.getByLabelText('Prix d’achat'), '250000');
  await user.clear(screen.getByLabelText('Frais de notaire'));
  await user.type(screen.getByLabelText('Frais de notaire'), '19000');
}

describe('NewProperty', () => {
  beforeAll(async () => { await import('./NewProperty'); }, 30_000);

  it('creates a property in one request, its amounts as typed with a dot, then shows it', async () => {
    const bodies: unknown[] = [];
    serveCreate(bodies);
    const { user, router, container } = await renderApp('/properties/new');

    await fill(user);
    await user.click(screen.getByRole('radio', { name: 'Oui, partagé' }));
    await user.selectOptions(screen.getByLabelText('Type de bien'), 'rental');
    await user.type(screen.getByLabelText('Surface (m²)'), '85,5');
    await user.type(screen.getByLabelText('Commune ou repère'), 'Nantes');
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

  it('asks the visibility question with neither answer checked, and refuses to send until it is answered', async () => {
    const bodies: unknown[] = [];
    serveCreate(bodies);
    const { user } = await renderApp('/properties/new');

    await fill(user);
    expect(screen.getByRole('group', { name: 'Ce bien est-il visible de l’autre adulte ?' })).toBeInTheDocument();
    expect(screen.getByRole('radio', { name: 'Oui, partagé' })).not.toBeChecked();
    expect(screen.getByRole('radio', { name: 'Non, privé' })).not.toBeChecked();
    await submit(user);

    expect(await screen.findByText('Répondez à cette question.')).toBeInTheDocument();
    expect(bodies).toEqual([]);
  });

  it('refuses before sending a zero price, a fee with three decimals, a purchase after today and a threshold of 0 days', async () => {
    const bodies: unknown[] = [];
    serveCreate(bodies);
    const { user } = await renderApp('/properties/new');

    await fill(user);
    await user.click(screen.getByRole('radio', { name: 'Non, privé' }));
    await user.clear(screen.getByLabelText('Prix d’achat'));
    await user.type(screen.getByLabelText('Prix d’achat'), '0');
    await user.clear(screen.getByLabelText('Frais de notaire'));
    await user.type(screen.getByLabelText('Frais de notaire'), '19000,123');
    await user.clear(screen.getByLabelText('Date d’achat'));
    await user.type(screen.getByLabelText('Date d’achat'), '2999-01-01');
    await user.click(screen.getByRole('button', { name: 'Plus d’options' }));
    await user.type(screen.getByLabelText('Périmée après (jours)'), '0');
    await submit(user);

    expect(await screen.findByText('Un montant positif, avec deux décimales au plus, comme 1500,50.')).toBeInTheDocument();
    expect(screen.getByText('Un montant positif ou nul, avec deux décimales au plus, comme 1500,50.')).toBeInTheDocument();
    expect(screen.getByText('Cette date est dans le futur.')).toBeInTheDocument();
    expect(screen.getByText('Un nombre entier de jours, de 1 à 3 650.')).toBeInTheDocument();
    expect(bodies).toEqual([]);
  });

  it('folds the estimate rhythm and its threshold, and sends what is chosen there', async () => {
    const bodies: unknown[] = [];
    serveCreate(bodies);
    const { user, container } = await renderApp('/properties/new');

    await fill(user);
    await user.click(screen.getByRole('radio', { name: 'Oui, partagé' }));
    const options = screen.getByRole('button', { name: 'Plus d’options' });
    expect(options).toHaveAttribute('aria-expanded', 'false');
    expect(screen.queryByLabelText('Rythme des estimations')).not.toBeInTheDocument();
    await user.click(options);
    await user.selectOptions(screen.getByLabelText('Rythme des estimations'), 'manual');
    await user.type(screen.getByLabelText('Périmée après (jours)'), '730');
    await expectNoAxeViolations(container);
    await submit(user);

    await waitFor(() => expect(bodies).toHaveLength(1));
    expect(bodies[0]).toMatchObject({ valuationCadence: 'manual', staleAfterDays: 730 });
  });

  it('shows a server refusal under the field it names, opens the options holding one, and keeps the visibility answered', async () => {
    serveCreate([], () => HttpResponse.json({
      status: 422, code: 'realestate.invalid_property_field', title: 'x',
      errors: [
        { path: 'locationLabel', code: 'realestate.invalid_property_field', message: 'x' },
        { path: 'staleAfterDays', code: 'realestate.invalid_property_field', message: 'x' },
      ],
    }, { status: 422 }));
    const { user } = await renderApp('/properties/new');

    await fill(user);
    await user.click(screen.getByRole('radio', { name: 'Non, privé' }));
    await user.type(screen.getByLabelText('Commune ou repère'), 'Nantes');
    await submit(user);

    expect(await screen.findAllByText('Cette valeur n’est pas acceptée.')).toHaveLength(2);
    expect(screen.getByLabelText('Commune ou repère')).toHaveAttribute('aria-invalid', 'true');
    expect(screen.getByRole('button', { name: 'Plus d’options' })).toHaveAttribute('aria-expanded', 'true');
    expect(screen.getByLabelText('Périmée après (jours)')).toHaveAttribute('aria-invalid', 'true');
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(screen.getByRole('radio', { name: 'Non, privé' })).toBeChecked();
  });

  it('shows a refusal no field owns above the form', async () => {
    serveCreate([], () => HttpResponse.json(
      { status: 422, code: 'realestate.invalid_property_field', title: 'x', errors: [{ path: 'rentalStartsOn', code: 'realestate.invalid_property_field', message: 'x' }] },
      { status: 422 }));
    const { user } = await renderApp('/properties/new');

    await fill(user);
    await user.click(screen.getByRole('radio', { name: 'Oui, partagé' }));
    await submit(user);

    expect(await screen.findByRole('alert')).toBeInTheDocument();
  });
});
