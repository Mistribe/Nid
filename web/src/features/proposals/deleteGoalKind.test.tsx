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

  it('an envelope deletion opens the same dialog and approves with the preview version', async () => {
    const ENVELOPE = '0192f0aa-0000-7000-8000-00000000e001';
    const p = proposal({ kind: 'investments.delete_envelope', targetItemId: ENVELOPE, targetName: 'PEA Alice', subjectKey: null, payload: { id: ENVELOPE, version: 1 } });
    const sent = serve([p], { preview: {} });
    server.use(http.get('*/api/v1/investments/envelopes/:id/deletion', () => HttpResponse.json({ name: 'PEA Alice', version: 3, imported: false, removes: [], blockers: [] })));
    const { user, router } = await renderApp(`/proposals/${p.id}`);

    await user.click(await screen.findByRole('button', { name: 'Approuver' }));
    const dialog = await screen.findByRole('dialog', { name: 'Supprimer PEA Alice' });
    await user.type(await within(dialog).findByLabelText(/Tapez/), 'PEA Alice');
    await user.click(within(dialog).getByRole('button', { name: 'Supprimer définitivement' }));

    await waitFor(() => expect(sent.approve).toEqual({ version: 1, editedPayload: { id: ENVELOPE, version: 3 } }));
    await waitFor(() => expect(router.state.location.pathname).toBe('/investments'));
  });

  it('a property deletion opens the same dialog and approves with the preview version', async () => {
    const ITEM = '0192f0aa-0000-7000-8000-00000000f001';
    const p = proposal({ kind: 'realestate.delete_property', targetItemId: ITEM, targetName: 'Maison de Nantes', subjectKey: null, payload: { id: ITEM, version: 1 } });
    const sent = serve([p], { preview: {} });
    server.use(http.get('*/api/v1/realestate/properties/:id/deletion', () => HttpResponse.json({ name: 'Maison de Nantes', version: 2, imported: false, removes: [], blockers: [] })));
    const { user, router } = await renderApp(`/proposals/${p.id}`);

    await user.click(await screen.findByRole('button', { name: 'Approuver' }));
    const dialog = await screen.findByRole('dialog', { name: 'Supprimer Maison de Nantes' });
    await user.type(await within(dialog).findByLabelText(/Tapez/), 'Maison de Nantes');
    await user.click(within(dialog).getByRole('button', { name: 'Supprimer définitivement' }));

    await waitFor(() => expect(sent.approve).toEqual({ version: 1, editedPayload: { id: ITEM, version: 2 } }));
    await waitFor(() => expect(router.state.location.pathname).toBe('/properties'));
  });

  it('a loan deletion opens the same dialog and approves with the preview version', async () => {
    const ITEM = '0192f0aa-0000-7000-8000-00000000f002';
    const p = proposal({ kind: 'realestate.delete_loan', targetItemId: ITEM, targetName: 'Prêt maison', subjectKey: null, payload: { id: ITEM, version: 1 } });
    const sent = serve([p], { preview: {} });
    server.use(http.get('*/api/v1/realestate/loans/:id/deletion', () => HttpResponse.json({ name: 'Prêt maison', version: 2, imported: false, removes: [], blockers: [] })));
    const { user, router } = await renderApp(`/proposals/${p.id}`);

    await user.click(await screen.findByRole('button', { name: 'Approuver' }));
    const dialog = await screen.findByRole('dialog', { name: 'Supprimer Prêt maison' });
    await user.type(await within(dialog).findByLabelText(/Tapez/), 'Prêt maison');
    await user.click(within(dialog).getByRole('button', { name: 'Supprimer définitivement' }));

    await waitFor(() => expect(sent.approve).toEqual({ version: 1, editedPayload: { id: ITEM, version: 2 } }));
    await waitFor(() => expect(router.state.location.pathname).toBe('/loans'));
  });

  it('a subscription deletion opens the same dialog and approves with the preview version', async () => {
    const ITEM = '0192f0aa-0000-7000-8000-00000000f003';
    const p = proposal({ kind: 'subscriptions.delete_subscription', targetItemId: ITEM, targetName: 'Netflix', subjectKey: null, payload: { id: ITEM, version: 1 } });
    const sent = serve([p], { preview: {} });
    server.use(http.get('*/api/v1/subscriptions/:id/deletion', () => HttpResponse.json({ name: 'Netflix', version: 2, imported: false, removes: [], blockers: [] })));
    const { user, router } = await renderApp(`/proposals/${p.id}`);

    await user.click(await screen.findByRole('button', { name: 'Approuver' }));
    const dialog = await screen.findByRole('dialog', { name: 'Supprimer Netflix' });
    await user.type(await within(dialog).findByLabelText(/Tapez/), 'Netflix');
    await user.click(within(dialog).getByRole('button', { name: 'Supprimer définitivement' }));

    await waitFor(() => expect(sent.approve).toEqual({ version: 1, editedPayload: { id: ITEM, version: 2 } }));
    await waitFor(() => expect(router.state.location.pathname).toBe('/subscriptions'));
  });

  it('a childcare deletion opens the same dialog and approves with the preview version', async () => {
    const ITEM = '0192f0aa-0000-7000-8000-00000000f004';
    const p = proposal({ kind: 'childcare.delete_childcare', targetItemId: ITEM, targetName: 'Garde de Léa', subjectKey: null, payload: { id: ITEM, version: 1 } });
    const sent = serve([p], { preview: {} });
    server.use(http.get('*/api/v1/childcare/:id/deletion', () => HttpResponse.json({ name: 'Garde de Léa', version: 2, imported: false, removes: [], blockers: [] })));
    const { user, router } = await renderApp(`/proposals/${p.id}`);

    await user.click(await screen.findByRole('button', { name: 'Approuver' }));
    const dialog = await screen.findByRole('dialog', { name: 'Supprimer Garde de Léa' });
    await user.type(await within(dialog).findByLabelText(/Tapez/), 'Garde de Léa');
    await user.click(within(dialog).getByRole('button', { name: 'Supprimer définitivement' }));

    await waitFor(() => expect(sent.approve).toEqual({ version: 1, editedPayload: { id: ITEM, version: 2 } }));
    await waitFor(() => expect(router.state.location.pathname).toBe('/childcare'));
  });
});

