import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it } from 'vitest';
import { pick } from '@/test/controls';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { list as envelopeList, summary as envelope, PEA } from '../investments/testData';
import { persons } from '../subscriptions/testData';
import { GOAL } from './testData';

function serve(bodies: unknown[], answer: () => Response = () => HttpResponse.json({ id: GOAL }, { status: 201 })) {
  server.use(
    http.get('*/api/v1/core/persons', () => HttpResponse.json(persons)),
    http.get('*/api/v1/investments/envelopes', () => HttpResponse.json(envelopeList([envelope({ displayName: 'PEA Alice' }), envelope({ id: 'other', displayName: 'Autre' }), envelope({ id: 'closed', displayName: 'Vieux PEL', closed: true })]))),
    http.post('*/api/v1/goals', async ({ request }) => { bodies.push(await request.json()); return answer(); }),
    http.get('*/api/v1/goals/:id', () => HttpResponse.json({ goal: { id: GOAL, name: 'x', kind: 'savings', owners: [], visibility: 'shared', closed: false, status: 'no_date', partial: false, flags: [] }, funding: [], entries: [], commitments: [], version: 0 })),
  );
}

type User = Awaited<ReturnType<typeof renderApp>>['user'];
const next = (user: User) => user.click(screen.getByRole('button', { name: 'Continuer' }));
const submit = (user: User) => user.click(screen.getByRole('button', { name: 'Créer l’objectif' }));
const step = () => screen.getByRole('listitem', { current: 'step' });

beforeAll(async () => { await import('./NewGoal'); }, 30_000);

describe('NewGoal', () => {
  it('creates a savings goal with one envelope at 50 %', async () => {
    const bodies: unknown[] = [];
    serve(bodies);
    const { user, router } = await renderApp('/goals/new');

    await user.type(await screen.findByLabelText('Nom'), 'Voyage');
    await user.type(screen.getByLabelText('Montant cible'), '5000,50');
    await next(user);
    expect(step()).toHaveTextContent('Financement');
    await pick(user, 'Enveloppe', 'PEA Alice');
    expect(screen.queryByRole('option', { name: 'Vieux PEL' })).not.toBeInTheDocument();
    await user.clear(screen.getByLabelText('Part (%)'));
    await user.type(screen.getByLabelText('Part (%)'), '50');
    await next(user);
    expect(step()).toHaveTextContent('Détenteurs');
    await submit(user);

    await waitFor(() => expect(router.state.location.pathname).toBe(`/goals/${GOAL}`));
    expect(bodies).toEqual([{
      name: 'Voyage', kind: 'savings', targetAmount: '5000.50', funding: [{ envelopeId: PEA, shareBp: 5000 }],
      visibility: 'shared', owners: [{ personId: '01926b9e-0000-7000-8000-00000000000a', shareBp: 10000 }],
    }]);
  });

  it('creates an emergency fund: six months by default, no target field', async () => {
    const bodies: unknown[] = [];
    serve(bodies);
    const { user } = await renderApp('/goals/new');

    await user.type(await screen.findByLabelText('Nom'), 'Précaution');
    await user.click(screen.getByRole('radio', { name: 'Épargne de précaution' }));
    expect(screen.queryByLabelText('Montant cible')).not.toBeInTheDocument();
    expect(screen.getByLabelText('Mois de dépenses')).toHaveValue('6');
    await next(user);
    await user.click(screen.getByRole('radio', { name: 'Libre' }));
    await next(user);
    await submit(user);

    await waitFor(() => expect(bodies).toHaveLength(1));
    expect(bodies[0]).toMatchObject({ name: 'Précaution', kind: 'emergency_fund', months: 6, funding: [] });
    expect(bodies[0]).not.toHaveProperty('targetAmount');
  });

  it('sends an empty funding for a free goal', async () => {
    const bodies: unknown[] = [];
    serve(bodies);
    const { user } = await renderApp('/goals/new');

    await user.type(await screen.findByLabelText('Nom'), 'Voiture');
    await user.type(screen.getByLabelText('Montant cible'), '9000');
    await next(user);
    await user.click(screen.getByRole('radio', { name: 'Libre' }));
    await next(user);
    await submit(user);

    await waitFor(() => expect(bodies).toHaveLength(1));
    expect(bodies[0]).toMatchObject({ funding: [] });
  });

  it('blocks a past target date', async () => {
    const bodies: unknown[] = [];
    serve(bodies);
    const { user } = await renderApp('/goals/new');

    await user.type(await screen.findByLabelText('Nom'), 'Voyage');
    await user.type(screen.getByLabelText('Montant cible'), '5000');
    await user.type(screen.getByLabelText('Date cible'), '01/01/2020');
    await next(user);
    expect(await screen.findByText('La date cible ne peut pas être passée.')).toBeInTheDocument();
    expect(step()).toHaveTextContent('L’objectif');
    expect(bodies).toEqual([]);
  });

  it('Shows_free_share_on_share_exceeded', async () => {
    const bodies: unknown[] = [];
    serve(bodies, () => HttpResponse.json({ status: 409, code: 'goals.share_exceeded', title: 'Over.', freeBp: 3050 }, { status: 409 }));
    const { user } = await renderApp('/goals/new');

    await user.type(await screen.findByLabelText('Nom'), 'Voyage');
    await user.type(screen.getByLabelText('Montant cible'), '5000');
    await next(user);
    await pick(user, 'Enveloppe', 'PEA Alice');
    await next(user);
    await submit(user);

    expect(await screen.findByText('Part disponible : 30,5 %')).toBeInTheDocument();
    expect(step()).toHaveTextContent('Financement');
    expect(within(screen.getByRole('group', { name: 'Enveloppe 1' })).getByLabelText('Part (%)')).toHaveAttribute('aria-invalid', 'true');
  });

  it('puts the free share on the row the server names', async () => {
    serve([], () => HttpResponse.json({ status: 409, code: 'goals.share_exceeded', title: 'Over.', freeBp: 2500, errors: [{ path: 'funding[1].shareBp', code: 'goals.share_exceeded', message: 'Over.' }] }, { status: 409 }));
    const { user } = await renderApp('/goals/new');

    await user.type(await screen.findByLabelText('Nom'), 'Voyage');
    await user.type(screen.getByLabelText('Montant cible'), '5000');
    await next(user);
    await pick(user, 'Enveloppe', 'PEA Alice');
    await user.click(screen.getByRole('button', { name: 'Ajouter une enveloppe' }));
    await user.click(within(screen.getByRole('group', { name: 'Enveloppe 2' })).getByRole('combobox', { name: 'Enveloppe' }));
    await user.click(await within(await screen.findByRole('listbox')).findByRole('option', { name: 'Autre' }));
    await next(user);
    await submit(user);

    expect(await within(screen.getByRole('group', { name: 'Enveloppe 2' })).findByText('Part disponible : 25 %')).toBeInTheDocument();
    expect(within(screen.getByRole('group', { name: 'Enveloppe 1' })).queryByText(/Part disponible/)).not.toBeInTheDocument();
  });
});
