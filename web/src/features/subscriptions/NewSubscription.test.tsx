import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { ALICE, persons } from './testData';

const NEW_ID = '0192f0aa-0000-7000-8000-0000000000c1';

function serve(create: Parameters<typeof http.post>[1]) {
  server.use(
    http.get('*/api/v1/core/persons', () => HttpResponse.json(persons)),
    http.get('*/api/v1/subscriptions/providers', () =>
      HttpResponse.json([{ key: 'netflix', name: 'Netflix', origin: 'catalog', labels: [], website: null, archived: false, iconUrl: null }])),
    http.post('*/api/v1/subscriptions', create),
  );
}

async function start() {
  const utils = await renderApp('/subscriptions/new');
  await screen.findByRole('heading', { level: 1, name: 'Ajouter un abonnement' });
  return utils;
}

async function fillCommon(user: Awaited<ReturnType<typeof start>>['user']) {
  await user.type(screen.getByLabelText('Nom'), 'Netflix');
  await user.click(screen.getByRole('button', { name: 'Continuer' }));
}

async function fillOffer(user: Awaited<ReturnType<typeof start>>['user']) {
  await user.type(await screen.findByLabelText('Début'), '2025-01-01');
  await user.type(screen.getByLabelText('Montant'), '13,49');
  await user.click(screen.getByRole('button', { name: 'Continuer' }));
}

describe('Abonnements › ajout', () => {
  it('defaults the owner to the signed-in person at 100 %', async () => {
    serve(() => HttpResponse.json({ id: NEW_ID }, { status: 201 }));
    const { container } = await start();

    const owners = screen.getByRole('group', { name: 'Propriétaires' });
    expect(await within(owners).findByDisplayValue('Alice Dev')).toBeInTheDocument();
    expect(within(owners).getByLabelText('Part (%)')).toHaveValue('100');
    await expectNoAxeViolations(container);
  });

  it('fills the name from the chosen provider', async () => {
    serve(() => HttpResponse.json({ id: NEW_ID }, { status: 201 }));
    const { user } = await start();

    await user.type(screen.getByLabelText('Fournisseur'), 'net');
    await user.click(await screen.findByRole('button', { name: 'Choisir Netflix' }));

    expect(screen.getByLabelText('Nom')).toHaveValue('Netflix');
  });

  it('does not go on while the step is not valid', async () => {
    serve(() => HttpResponse.json({ id: NEW_ID }, { status: 201 }));
    const { user } = await start();

    await user.click(screen.getByRole('button', { name: 'Continuer' }));

    expect(screen.getByLabelText('Nom')).toHaveAttribute('aria-invalid', 'true');
    expect(screen.queryByLabelText('Début')).not.toBeInTheDocument();
  });

  it('sends one request with the phase, then opens the new subscription', async () => {
    const sent: unknown[] = [];
    serve(async ({ request }) => {
      sent.push(await request.json());
      return HttpResponse.json({ id: NEW_ID }, { status: 201 });
    });
    server.use(http.get('*/api/v1/subscriptions/:id', () => HttpResponse.json({ status: 404 }, { status: 404 })));
    const { user, router, container } = await start();

    await fillCommon(user);
    await expectNoAxeViolations(container);
    await fillOffer(user);
    const summary = await screen.findByRole('region', { name: 'Récapitulatif' });
    expect(summary).toHaveTextContent('Netflix');
    expect(summary).toHaveTextContent('13,49');
    await expectNoAxeViolations(container);
    await user.click(screen.getByRole('button', { name: "Ajouter l'abonnement" }));

    await waitFor(() => expect(router.state.location.pathname).toBe(`/subscriptions/${NEW_ID}`));
    expect(sent).toEqual([{
      displayName: 'Netflix', visibility: 'shared', owners: [{ personId: ALICE, shareBp: 10000 }], providerKey: null,
      autoRenew: true, users: [], note: null,
      phase: {
        startsOn: '2025-01-01', endsOn: null, price: { amount: '13.49', currency: 'EUR' }, every: { count: 1, unit: 'month' },
        covers: null, maxCycles: null, payer: { type: 'household', personId: null },
      },
    }]);
  });

  it('brings the user back to the step where the server found an error', async () => {
    serve(() => HttpResponse.json({
      status: 422, code: 'subscriptions.unknown_provider', title: 'This provider does not exist or cannot be linked.',
      errors: [{ path: 'providerKey', code: 'subscriptions.unknown_provider', message: 'This provider does not exist or cannot be linked.' }],
    }, { status: 422 }));
    const { user } = await start();

    await user.type(screen.getByLabelText('Fournisseur'), 'net');
    await user.click(await screen.findByRole('button', { name: 'Choisir Netflix' }));
    await user.click(screen.getByRole('button', { name: 'Continuer' }));
    await fillOffer(user);
    await user.click(await screen.findByRole('button', { name: "Ajouter l'abonnement" }));

    const provider = await screen.findByLabelText('Fournisseur');
    await waitFor(() => expect(provider).toHaveAccessibleDescription(/n'existe pas ou ne peut pas être choisi/));
  });

  it('brings the user back to the offer step for an amount refused by the server', async () => {
    serve(() => HttpResponse.json({
      status: 422, code: 'request.invalid', title: 'x',
      errors: [{ path: 'phase.price.amount', code: 'request.amount', message: 'x' }],
    }, { status: 422 }));
    const { user } = await start();

    await fillCommon(user);
    await fillOffer(user);
    await user.click(await screen.findByRole('button', { name: "Ajouter l'abonnement" }));

    const amount = await screen.findByLabelText('Montant');
    await waitFor(() => expect(amount).toHaveAttribute('aria-invalid', 'true'));
  });
});
