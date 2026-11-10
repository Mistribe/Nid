import { screen, waitFor, within } from '@testing-library/react';
import { beforeAll, beforeEach, describe, expect, it } from 'vitest';
import { HttpResponse, http } from 'msw/http';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { resetFocus } from './focus';
import { proposal } from './testData';
import { serve } from './testServe';

const GOAL = '0192f0aa-0000-7000-8000-00000000a001';

describe('A proposal to delete a goal', () => {
  beforeEach(resetFocus);
  beforeAll(async () => { await import('./ProposalDetailPage'); });

  it('approving it asks for the name, then sends the preview version and leaves for the goals', async () => {
    const p = proposal({ kind: 'goals.delete_goal', targetItemId: GOAL, targetName: 'Voyage', subjectKey: null, payload: { id: GOAL, version: 1 } });
    const sent = serve([p], { preview: {} });
    server.use(http.get('*/api/v1/goals/:id/deletion', () => HttpResponse.json({ name: 'Voyage', version: 4, imported: false, removes: [], blockers: [] })));
    const { user, router } = await renderApp(`/proposals/${p.id}`);

    await user.click(await screen.findByRole('button', { name: 'Approuver' }));
    const dialog = await screen.findByRole('dialog', { name: 'Supprimer Voyage' });
    expect(sent.approve).toBeUndefined();
    await user.type(await within(dialog).findByLabelText(/Tapez/), 'Voyage');
    await user.click(within(dialog).getByRole('button', { name: 'Supprimer définitivement' }));

    await waitFor(() => expect(sent.approve).toEqual({ version: 1, editedPayload: { id: GOAL, version: 4 } }));
    await waitFor(() => expect(router.state.location.pathname).toBe('/goals'));
  });

  it('after a refused approval, the next confirm sends the proposal’s new version', async () => {
    const p = proposal({ kind: 'goals.delete_goal', targetItemId: GOAL, targetName: 'Voyage', subjectKey: null, payload: { id: GOAL, version: 1 } });
    serve([p], { preview: {} });
    let current = p;
    let refused = false;
    let proposalReads = 0;
    const approvals: unknown[] = [];
    server.use(
      http.get('*/api/v1/goals/:id/deletion', () => HttpResponse.json({ name: 'Voyage', version: refused ? 5 : 4, imported: false, removes: [], blockers: [] })),
      http.get('*/api/v1/core/proposals/:id', () => { proposalReads += 1; return HttpResponse.json({ proposal: current, preview: {}, canDecide: true }); }),
      http.post('*/api/v1/core/proposals/:id/approve', async ({ request }) => {
        approvals.push(await request.json());
        if (refused) return HttpResponse.json({ ...current, status: 'approved' });
        // The refusal leaves its warning on the proposal: a new version.
        refused = true;
        current = { ...current, version: current.version + 1, warnings: [{ code: 'apply_failed', severity: 'error', message: 'core.item_changed: Changed.' }] };
        return HttpResponse.json({ status: 409, code: 'core.item_changed', title: 'Changed.' }, { status: 409 });
      }),
    );
    const { user, router } = await renderApp(`/proposals/${p.id}`);

    await user.click(await screen.findByRole('button', { name: 'Approuver' }));
    const dialog = await screen.findByRole('dialog', { name: 'Supprimer Voyage' });
    await user.type(await within(dialog).findByLabelText(/Tapez/), 'Voyage');
    await user.click(within(dialog).getByRole('button', { name: 'Supprimer définitivement' }));
    await waitFor(() => expect(proposalReads).toBeGreaterThan(1));
    await waitFor(() => expect(within(dialog).getByLabelText(/Tapez/)).toHaveValue(''));
    await user.type(within(dialog).getByLabelText(/Tapez/), 'Voyage');
    await user.click(within(dialog).getByRole('button', { name: 'Supprimer définitivement' }));

    await waitFor(() => expect(approvals).toEqual([
      { version: p.version, editedPayload: { id: GOAL, version: 4 } },
      { version: p.version + 1, editedPayload: { id: GOAL, version: 5 } },
    ]));
    await waitFor(() => expect(router.state.location.pathname).toBe('/goals'));
  });

  it('an income source deletion opens the same dialog and approves with the preview version', async () => {
    const SOURCE = '0192f0aa-0000-7000-8000-00000000c001';
    const p = proposal({ kind: 'income.delete_income_source', targetItemId: SOURCE, targetName: 'Salaire Alice', subjectKey: null, payload: { id: SOURCE, version: 1 } });
    const sent = serve([p], { preview: {} });
    server.use(http.get('*/api/v1/income/sources/:id/deletion', () => HttpResponse.json({ name: 'Salaire Alice', version: 2, imported: false, removes: [], blockers: [] })));
    const { user, router } = await renderApp(`/proposals/${p.id}`);

    await user.click(await screen.findByRole('button', { name: 'Approuver' }));
    const dialog = await screen.findByRole('dialog', { name: 'Supprimer Salaire Alice' });
    await user.type(await within(dialog).findByLabelText(/Tapez/), 'Salaire Alice');
    await user.click(within(dialog).getByRole('button', { name: 'Supprimer définitivement' }));

    await waitFor(() => expect(sent.approve).toEqual({ version: 1, editedPayload: { id: SOURCE, version: 2 } }));
    await waitFor(() => expect(router.state.location.pathname).toBe('/income'));
  });
});
