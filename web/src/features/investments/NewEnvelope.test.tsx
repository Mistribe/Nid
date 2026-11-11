import { screen } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { pick } from '@/test/controls';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { persons } from '../subscriptions/testData';
import { todayIso } from '@/lib/today';
import { detail, PEA } from './testData';

function serveCreate(bodies: unknown[], answer: () => Response = () => HttpResponse.json({ id: PEA }, { status: 201 })) {
  server.use(
    http.get('*/api/v1/core/persons', () => HttpResponse.json(persons)),
    http.post('*/api/v1/investments/envelopes', async ({ request }) => { bodies.push(await request.json()); return answer(); }),
    http.get('*/api/v1/investments/envelopes/:id', () => HttpResponse.json(detail())),
  );
}

type User = Awaited<ReturnType<typeof renderApp>>['user'];
const next = (user: User) => user.click(screen.getByRole('button', { name: 'Continuer' }));
const submit = (user: User) => user.click(screen.getByRole('button', { name: 'Créer l’enveloppe' }));

/** Names the envelope, then passes « Chez qui ? » as it stands: the last step, « Combien y a-t-il dessus aujourd’hui ? », shows. */
async function toValueStep(user: User, name = 'PEA') {
  await user.type(await screen.findByLabelText('Nom'), name);
  await next(user);
  await next(user);
  await screen.findByLabelText('Valeur du compte');
}

describe('NewEnvelope', () => {
  // The detail page (with Recharts) is a lazy chunk: loaded cold inside the test it raced findByRole's 1 s.
  beforeAll(async () => { await import('./EnvelopeDetailPage'); });

  it('creates a private PEA with its opening and first value in three plain steps, then shows it', async () => {
    const bodies: unknown[] = [];
    serveCreate(bodies);
    const { user, container } = await renderApp('/investments/new');

    expect(await screen.findByText('Quel type de compte ?')).toBeInTheDocument();
    await user.type(await screen.findByLabelText('Nom'), 'PEA');
    await pick(user, 'Type', 'PEA');
    expect(screen.getByText('Plan d’épargne en actions : des actions et fonds européens, avec moins d’impôt après 5 ans.')).toBeInTheDocument();
    await next(user);
    await user.click(await screen.findByRole('radio', { name: 'Privé' }));
    await next(user);
    await user.type(await screen.findByLabelText('Valeur du compte'), '21000,50');
    await user.clear(screen.getByLabelText('À la date du'));
    await user.type(screen.getByLabelText('À la date du'), '31/12/2024');
    await user.click(screen.getByRole('button', { name: 'Versements antérieurs' }));
    await user.type(screen.getByLabelText('Date des versements antérieurs'), '31/12/2024');
    await user.type(screen.getByLabelText('Montant des versements antérieurs'), '20000,00');
    await expectNoAxeViolations(container);
    await submit(user);

    expect(await screen.findByRole('heading', { name: 'PEA', level: 1 })).toBeInTheDocument();
    expect(bodies).toEqual([expect.objectContaining({
      displayName: 'PEA', kind: 'pea', currency: 'EUR', visibility: 'private', valuationCadence: 'monthly',
      opening: { occurredOn: '2024-12-31', amount: { amount: '20000.00', currency: 'EUR' } },
      firstValuation: { asOf: '2024-12-31', amount: { amount: '21000.50', currency: 'EUR' } },
    })]);
  });

  it('sends no opening and no first value when they are left empty', async () => {
    const bodies: unknown[] = [];
    serveCreate(bodies);
    const { user } = await renderApp('/investments/new');

    await pick(user, 'Type', 'Livret');
    await toValueStep(user, 'Livret A');
    await submit(user);

    await screen.findByRole('heading', { level: 1 });
    expect(bodies[0]).not.toHaveProperty('opening');
    expect(bodies[0]).not.toHaveProperty('firstValuation');
  });

  it('keeps « Chez qui ? » until the account hint fits, and refuses an amount with a space before sending', async () => {
    const bodies: unknown[] = [];
    serveCreate(bodies);
    const { user } = await renderApp('/investments/new');

    await user.type(await screen.findByLabelText('Nom'), 'PEA');
    await next(user);
    await user.type(await screen.findByLabelText(/4 derniers caractères/), '12345');
    await next(user);
    expect(await screen.findByText('4 caractères au plus : jamais le numéro complet.')).toBeInTheDocument();
    await user.type(screen.getByLabelText(/4 derniers caractères/), '{Backspace}');
    await next(user);
    await user.type(await screen.findByLabelText('Valeur du compte'), '1 000');
    await submit(user);

    expect(await screen.findByText('Saisissez un montant comme 1234,56.')).toBeInTheDocument();
    expect(bodies).toEqual([]);
  });

  it('shows a server refusal under the field it names', async () => {
    serveCreate([], () => HttpResponse.json(
      { status: 422, code: 'core.date_in_future', title: 'A value or a flow is not dated after today.', errors: [{ path: 'firstValuation.asOf', code: 'core.date_in_future', message: 'x' }] },
      { status: 422 }));
    const { user } = await renderApp('/investments/new');

    await toValueStep(user);
    await user.type(screen.getByLabelText('Valeur du compte'), '10');
    await submit(user);

    expect(await screen.findByText('Cette date est dans le futur.')).toBeInTheDocument();
  });

  it('goes back to the step of the field a server refusal names', async () => {
    serveCreate([], () => HttpResponse.json(
      { status: 422, code: 'investments.invalid_envelope', title: 'Invalid', errors: [{ path: 'displayName', code: 'investments.invalid_envelope', message: 'x' }] },
      { status: 422 }));
    const { user } = await renderApp('/investments/new');

    await toValueStep(user);
    await submit(user);

    expect(await screen.findByLabelText('Nom')).toBeInvalid();
  });

  it('shows a refusal no field owns above the form', async () => {
    serveCreate([], () => HttpResponse.json({ status: 422, code: 'core.date_in_future', title: 'A value or a flow is not dated after today.' }, { status: 422 }));
    const { user } = await renderApp('/investments/new');

    await toValueStep(user);
    await submit(user);

    expect(await screen.findByRole('alert')).toBeInTheDocument();
  });

  it.each(['0', '0,00'])('refuses a zero opening amount (%s)', async (zero) => {
    const bodies: unknown[] = [];
    serveCreate(bodies);
    const { user } = await renderApp('/investments/new');

    await toValueStep(user);
    await user.click(screen.getByRole('button', { name: 'Versements antérieurs' }));
    await user.type(screen.getByLabelText('Date des versements antérieurs'), '31/12/2024');
    await user.type(screen.getByLabelText('Montant des versements antérieurs'), zero);
    await submit(user);

    expect(await screen.findByText('Saisissez un montant positif comme 1234,56.')).toBeInTheDocument();
    expect(bodies).toEqual([]);
  });

  it('accepts a negative first value (an overdrawn current account), unlike the opening', async () => {
    const bodies: unknown[] = [];
    serveCreate(bodies);
    const { user } = await renderApp('/investments/new');

    await pick(user, 'Type', 'Compte courant');
    await toValueStep(user, 'Compte courant');
    await user.type(screen.getByLabelText('Valeur du compte'), '-120,50');
    await submit(user);

    await screen.findByRole('heading', { level: 1 });
    expect(bodies).toEqual([expect.objectContaining({
      firstValuation: { asOf: todayIso(), amount: { amount: '-120.50', currency: 'EUR' } },
    })]);
  });

  it('refuses a future date before sending', async () => {
    const bodies: unknown[] = [];
    serveCreate(bodies);
    const { user } = await renderApp('/investments/new');

    await toValueStep(user);
    await user.clear(screen.getByLabelText('À la date du'));
    await user.type(screen.getByLabelText('À la date du'), '01/01/2999');
    await user.type(screen.getByLabelText('Valeur du compte'), '10');
    await submit(user);

    expect(await screen.findByText('Cette date est dans le futur.')).toBeInTheDocument();
    expect(screen.getByLabelText('À la date du')).toBeInvalid();
    expect(bodies).toEqual([]);
  });

  it('folds the opening, the cadence and the currency away, their defaults shown once open', async () => {
    serveCreate([]);
    const { user } = await renderApp('/investments/new');

    await toValueStep(user);
    for (const label of ['Date des versements antérieurs', 'Mise à jour de la valeur', 'Devise']) expect(screen.queryByLabelText(label)).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Versements antérieurs' }));
    expect(screen.getByLabelText('Date des versements antérieurs')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Personnaliser' }));
    expect(screen.getByLabelText('Mise à jour de la valeur')).toHaveTextContent('Chaque mois');
    expect(screen.getByLabelText('Devise')).toHaveTextContent('EUR — euro');
  });

  it('sends the picked currency, for the envelope and its first value', async () => {
    const bodies: unknown[] = [];
    serveCreate(bodies);
    const { user } = await renderApp('/investments/new');

    await toValueStep(user);
    await user.click(screen.getByRole('button', { name: 'Personnaliser' }));
    await pick(user, 'Devise', /^USD/);
    await user.type(screen.getByLabelText('Valeur du compte'), '10');
    await submit(user);

    await screen.findByRole('heading', { level: 1 });
    expect(bodies).toEqual([expect.objectContaining({
      currency: 'USD', firstValuation: { asOf: todayIso(), amount: { amount: '10', currency: 'USD' } },
    })]);
  });

  // Task 3 brief: a pathless code (the Core fact codes carry no path, 2A) is placed on the one date/amount
  // field it can concern when exactly one such field was filled; otherwise (above) it stays page-level.
  it('attributes a pathless refusal to the one field that was filled', async () => {
    serveCreate([], () => HttpResponse.json({ status: 422, code: 'core.date_in_future', title: 'A value or a flow is not dated after today.' }, { status: 422 }));
    const { user } = await renderApp('/investments/new');

    await toValueStep(user);
    await user.type(screen.getByLabelText('Valeur du compte'), '10');
    await submit(user);

    expect(await screen.findByText('Cette date est dans le futur.')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('lays the visibility out as a segmented control on « Chez qui ? »', async () => {
    serveCreate([]);
    const { user } = await renderApp('/investments/new');

    await user.type(await screen.findByLabelText('Nom'), 'PEA');
    await next(user);
    expect(await screen.findByRole('radiogroup', { name: 'Visibilité' })).toBeInTheDocument();
    expect(screen.getByRole('radio', { name: 'Partagé' })).toBeChecked();
    expect(screen.getByRole('radiogroup', { name: 'Visibilité' })).toHaveAccessibleDescription('L’autre adulte y a accès, et le total du foyer en tient compte.');
    await user.click(screen.getByRole('radio', { name: 'Privé' }));
    expect(screen.getByRole('radiogroup', { name: 'Visibilité' })).toHaveAccessibleDescription('Visible de vous seulement, avec un seul propriétaire.');
  });
});
