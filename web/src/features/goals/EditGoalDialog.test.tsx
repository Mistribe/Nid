import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it } from 'vitest';
import { list as envelopeList, summary as envelope } from '../investments/testData';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { GoalDetail } from './api';
import { detail, ENVELOPE, GOAL } from './testData';

function serve(body: GoalDetail, puts: unknown[], answer: () => Response = () => HttpResponse.json(body)) {
  server.use(
    http.get('*/api/v1/goals/:id', () => HttpResponse.json(body)),
    http.get('*/api/v1/investments/envelopes', () => HttpResponse.json(envelopeList([envelope({ id: ENVELOPE, displayName: 'PEA Alice' })]))),
    http.put('*/api/v1/goals/:id', async ({ request }) => { puts.push(await request.json()); return answer(); }),
  );
}
const open = async (user: Awaited<ReturnType<typeof renderApp>>['user']) => {
  await user.click(await screen.findByRole('button', { name: 'Modifier' }));
  return screen.findByRole('dialog', { name: 'Modifier l’objectif' });
};

beforeAll(async () => { await import('./GoalDetailPage'); await import('./EditGoalDialog'); }, 30_000);

describe('EditGoalDialog', () => {
  it('renames and keeps the funding, sending the version', async () => {
    const puts: unknown[] = [];
    serve(detail({ version: 7 }), puts);
    const { user } = await renderApp(`/goals/${GOAL}`);
    await open(user);

    const name = screen.getByLabelText('Nom');
    expect(name).toHaveValue('Voyage');
    expect(screen.getByLabelText('Montant cible')).toHaveValue('5000.0000');
    await user.clear(name);
    await user.type(name, 'Grand voyage');
    await user.click(screen.getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(puts).toHaveLength(1));
    expect(puts[0]).toEqual({
      name: 'Grand voyage', targetAmount: '5000.0000', targetDate: '2027-06-01', funding: [{ envelopeId: ENVELOPE, shareBp: 5000 }], version: 7,
    });
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
  });

  it('says the goal changed on a stale version', async () => {
    const puts: unknown[] = [];
    serve(detail({ version: 7 }), puts, () => HttpResponse.json({ status: 409, code: 'goals.conflict', title: 'Changed.' }, { status: 409 }));
    const { user } = await renderApp(`/goals/${GOAL}`);
    await open(user);
    await user.click(screen.getByRole('button', { name: 'Enregistrer' }));

    expect(await screen.findByText('Cet objectif a changé entre-temps')).toBeInTheDocument();
    expect(screen.getByRole('dialog')).toBeInTheDocument();
  });

  it('has no owner or visibility field and no kind', async () => {
    serve(detail(), []);
    const { user } = await renderApp(`/goals/${GOAL}`);
    await open(user);

    expect(screen.queryByText('Propriétaires')).not.toBeInTheDocument();
    expect(screen.queryByText('Visibilité')).not.toBeInTheDocument();
    expect(screen.queryByRole('radio', { name: 'Projet' })).not.toBeInTheDocument();
  });

  it('is not offered on a closed goal', async () => {
    serve(detail({}, { closed: true }), []);
    await renderApp(`/goals/${GOAL}`);
    await screen.findByRole('heading', { name: 'Voyage' });
    expect(screen.queryByRole('button', { name: 'Modifier' })).not.toBeInTheDocument();
  });

  it('reloads the form with the fresh goal after a conflict, and a retry sends it with the new version', async () => {
    const puts: unknown[] = [];
    let current = detail({ version: 7 });
    server.use(
      http.get('*/api/v1/goals/:id', () => HttpResponse.json(current)),
      http.get('*/api/v1/investments/envelopes', () => HttpResponse.json(envelopeList([envelope({ id: ENVELOPE, displayName: 'PEA Alice' })]))),
      http.put('*/api/v1/goals/:id', async ({ request }) => {
        puts.push(await request.json());
        if (puts.length > 1) return HttpResponse.json(current);
        current = detail({ version: 8 }, { name: 'Renommé ailleurs' });
        return HttpResponse.json({ status: 409, code: 'goals.conflict', title: 'Changed.' }, { status: 409 });
      }),
    );
    const { user } = await renderApp(`/goals/${GOAL}`);
    await open(user);
    await user.click(screen.getByRole('button', { name: 'Enregistrer' }));

    expect(await screen.findByText('Cet objectif a changé entre-temps')).toBeInTheDocument();
    await waitFor(() => expect(screen.getByLabelText('Nom')).toHaveValue('Renommé ailleurs'));
    await user.click(screen.getByRole('button', { name: 'Enregistrer' }));
    await waitFor(() => expect(puts).toHaveLength(2));
    expect(puts[1]).toMatchObject({ name: 'Renommé ailleurs', version: 8 });
  });

  it('edits the goal as read in scope me, so a link hidden in household stays in the body', async () => {
    const puts: unknown[] = [];
    const scopes: (string | null)[] = [];
    server.use(
      http.get('*/api/v1/goals/:id', ({ request }) => {
        const scope = new URL(request.url).searchParams.get('scope');
        scopes.push(scope);
        return HttpResponse.json(scope === 'me' ? detail({ version: 4 }) : detail({ version: 4, funding: [] }, { partial: true, flags: ['partial'] }));
      }),
      http.get('*/api/v1/investments/envelopes', () => HttpResponse.json(envelopeList([envelope({ id: ENVELOPE, displayName: 'PEA Alice' })]))),
      http.put('*/api/v1/goals/:id', async ({ request }) => { puts.push(await request.json()); return HttpResponse.json(detail()); }),
    );
    const { user } = await renderApp(`/goals/${GOAL}`);
    await open(user);
    await waitFor(() => expect(screen.getByLabelText('Nom')).toHaveValue('Voyage'));
    await user.click(screen.getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(puts).toHaveLength(1));
    expect(scopes).toContain('me');
    expect(puts[0]).toMatchObject({ funding: [{ envelopeId: ENVELOPE, shareBp: 5000 }], version: 4 });
    expect(await screen.findByRole('status')).toHaveTextContent('Objectif modifié.');
  });

  it('keeps a partial goal funded: envelopes mode, no rows, and saves without adding one', async () => {
    const puts: unknown[] = [];
    serve(detail({ version: 2, funding: [] }, { partial: true, flags: ['partial'] }), puts);
    const { user } = await renderApp(`/goals/${GOAL}`);
    const dialog = await open(user);
    expect(within(dialog).getByRole('radio', { name: 'Enveloppes' })).toBeChecked();
    expect(within(dialog).queryByRole('group', { name: 'Enveloppe 1' })).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Enregistrer' }));
    await waitFor(() => expect(puts).toHaveLength(1));
    expect(puts[0]).toMatchObject({ funding: [], version: 2 });
  });

  it('omits an emptied note and target date', async () => {
    const puts: unknown[] = [];
    serve(detail({ version: 3, note: 'À vider' }), puts);
    const { user } = await renderApp(`/goals/${GOAL}`);
    await open(user);
    await user.click(screen.getByRole('button', { name: 'Plus d’options' }));
    await user.clear(screen.getByLabelText('Note'));
    await user.clear(screen.getByLabelText('Date cible'));
    await user.click(screen.getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(puts).toHaveLength(1));
    expect(puts[0]).not.toHaveProperty('note');
    expect(puts[0]).not.toHaveProperty('targetDate');
  });
});
