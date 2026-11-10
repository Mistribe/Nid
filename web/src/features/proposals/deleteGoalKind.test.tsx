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
});
