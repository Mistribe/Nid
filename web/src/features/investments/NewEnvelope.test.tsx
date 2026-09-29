import { screen } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { persons } from '../subscriptions/testData';
import { detail, PEA } from './testData';

function serveCreate(bodies: unknown[], answer: () => Response = () => HttpResponse.json({ id: PEA }, { status: 201 })) {
  server.use(
    http.get('*/api/v1/core/persons', () => HttpResponse.json(persons)),
    http.post('*/api/v1/investments/envelopes', async ({ request }) => { bodies.push(await request.json()); return answer(); }),
    http.get('*/api/v1/investments/envelopes/:id', () => HttpResponse.json(detail())),
  );
}

describe('NewEnvelope', () => {
  it('creates a private PEA with its opening and first value, then shows it', async () => {
    const bodies: unknown[] = [];
    serveCreate(bodies);
    const { user, container } = await renderApp('/investments/new');

    await user.type(await screen.findByLabelText('Nom'), 'PEA');
    await user.selectOptions(screen.getByLabelText('Type'), 'pea');
    await user.click(screen.getByRole('radio', { name: /Privé/ }));
    await user.click(screen.getByRole('checkbox', { name: 'Versements antérieurs' }));
    await user.type(screen.getByLabelText('Date des versements antérieurs'), '2024-12-31');
    await user.type(screen.getByLabelText('Montant des versements antérieurs'), '20000,00');
    await user.click(screen.getByRole('checkbox', { name: 'Première valeur' }));
    await user.clear(screen.getByLabelText('Date de la première valeur'));
    await user.type(screen.getByLabelText('Date de la première valeur'), '2024-12-31');
    await user.type(screen.getByLabelText('Montant de la première valeur'), '21000,50');
    await expectNoAxeViolations(container);
    await user.click(screen.getByRole('button', { name: 'Créer l’enveloppe' }));

    expect(await screen.findByRole('heading', { name: 'PEA', level: 1 })).toBeInTheDocument();
    expect(bodies).toEqual([expect.objectContaining({
      displayName: 'PEA', kind: 'pea', currency: 'EUR', visibility: 'private', valuationCadence: 'monthly',
      opening: { occurredOn: '2024-12-31', amount: { amount: '20000.00', currency: 'EUR' } },
      firstValuation: { asOf: '2024-12-31', amount: { amount: '21000.50', currency: 'EUR' } },
    })]);
  });

  it('sends no opening and no first value when their boxes are unchecked', async () => {
    const bodies: unknown[] = [];
    serveCreate(bodies);
    const { user } = await renderApp('/investments/new');

    await user.type(await screen.findByLabelText('Nom'), 'Livret A');
    await user.selectOptions(screen.getByLabelText('Type'), 'savings');
    await user.click(screen.getByRole('button', { name: 'Créer l’enveloppe' }));

    await screen.findByRole('heading', { level: 1 });
    expect(bodies[0]).not.toHaveProperty('opening');
    expect(bodies[0]).not.toHaveProperty('firstValuation');
  });

  it('refuses an amount with a space and an account hint of more than four characters before sending', async () => {
    const bodies: unknown[] = [];
    serveCreate(bodies);
    const { user } = await renderApp('/investments/new');

    await user.type(await screen.findByLabelText('Nom'), 'PEA');
    await user.type(screen.getByLabelText(/4 derniers caractères/), '12345');
    await user.click(screen.getByRole('checkbox', { name: 'Première valeur' }));
    await user.type(screen.getByLabelText('Montant de la première valeur'), '1 000');
    await user.click(screen.getByRole('button', { name: 'Créer l’enveloppe' }));

    expect(await screen.findByText('4 caractères au plus : jamais le numéro complet.')).toBeInTheDocument();
    expect(screen.getByText('Saisissez un montant comme 1234,56.')).toBeInTheDocument();
    expect(bodies).toEqual([]);
  });

  it('shows a server refusal under the field it names', async () => {
    serveCreate([], () => HttpResponse.json(
      { status: 422, code: 'core.date_in_future', title: 'A value or a flow is not dated after today.', errors: [{ path: 'firstValuation.asOf', code: 'core.date_in_future', message: 'x' }] },
      { status: 422 }));
    const { user } = await renderApp('/investments/new');

    await user.type(await screen.findByLabelText('Nom'), 'PEA');
    await user.click(screen.getByRole('checkbox', { name: 'Première valeur' }));
    await user.type(screen.getByLabelText('Montant de la première valeur'), '10');
    await user.click(screen.getByRole('button', { name: 'Créer l’enveloppe' }));

    expect(await screen.findByText('Cette date est dans le futur.')).toBeInTheDocument();
  });

  it('shows a refusal no field owns above the form', async () => {
    serveCreate([], () => HttpResponse.json({ status: 422, code: 'core.date_in_future', title: 'A value or a flow is not dated after today.' }, { status: 422 }));
    const { user } = await renderApp('/investments/new');

    await user.type(await screen.findByLabelText('Nom'), 'PEA');
    await user.click(screen.getByRole('button', { name: 'Créer l’enveloppe' }));

    expect(await screen.findByRole('alert')).toBeInTheDocument();
  });

  // Task 3 brief: a pathless code (the Core fact codes carry no path, 2A) is placed on the one date/amount
  // field it can concern when exactly one such field was filled; otherwise (previous test) it stays page-level.
  it('attributes a pathless refusal to the one field that was filled', async () => {
    serveCreate([], () => HttpResponse.json({ status: 422, code: 'core.date_in_future', title: 'A value or a flow is not dated after today.' }, { status: 422 }));
    const { user } = await renderApp('/investments/new');

    await user.type(await screen.findByLabelText('Nom'), 'PEA');
    await user.click(screen.getByRole('checkbox', { name: 'Première valeur' }));
    await user.type(screen.getByLabelText('Montant de la première valeur'), '10');
    await user.click(screen.getByRole('button', { name: 'Créer l’enveloppe' }));

    expect(await screen.findByText('Cette date est dans le futur.')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });
});
