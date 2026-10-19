import { screen, waitFor, within } from '@testing-library/react';
import { beforeAll, beforeEach, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { resetAnnouncement } from './announce';
import { proposal } from './testData';
import { serve } from './testServe';

/** Spec 4D3: a proposal that replays a web write has one read-only screen. */
describe('Une proposition d’action du web', () => {
  beforeEach(resetAnnouncement);
  beforeAll(async () => { await import('./ProposalDetailPage'); await import('./ProposalsPage'); });

  const creation = () => proposal({
    kind: 'realestate.create_property', targetItemId: null, targetName: null, subjectKey: null, sourceLabel: 'Acte de vente',
    payload: { body: { name: 'Maison', currency: 'EUR', owners: [{ personId: 'a', shareBp: 10000 }], purchasePrice: '250000' } },
  });

  it('shows the action and every field proposed, and approves it as proposed', async () => {
    const p = creation();
    const sent = serve([p], { preview: {} });
    const { user, container } = await renderApp(`/proposals/${p.id}`);

    expect(await screen.findByRole('heading', { level: 1, name: 'Nouveau bien' })).toBeInTheDocument();
    expect(screen.getByText('Nouvel élément')).toBeInTheDocument();
    expect(screen.getByRole('row', { name: /^name/ })).toHaveTextContent('Maison');
    expect(screen.getByRole('row', { name: /owners\[0\]\.shareBp/ })).toHaveTextContent('10000');
    expect(screen.queryByRole('form')).not.toBeInTheDocument();
    await expectNoAxeViolations(container);

    await user.click(screen.getByRole('button', { name: 'Approuver' }));
    await waitFor(() => expect(sent.approve).toEqual({ version: 1 }));
  });

  it('is listed by its action and the name it will have', async () => {
    serve([creation()]);
    await renderApp('/proposals');
    const table = within(await screen.findByRole('table', { name: 'Liste des propositions' }));
    expect(table.getByRole('link', { name: 'Nouveau bien — Maison' })).toBeInTheDocument();
  });

  it('links a change to the item it targets', async () => {
    const p = proposal({
      kind: 'income.add_one_off_income', targetItemId: '00000000-0000-7000-8000-0000000000e2', targetName: 'Salaire', subjectKey: null,
      payload: { id: '00000000-0000-7000-8000-0000000000e2', body: { occurredOn: '2025-06-20', amount: { amount: '1500.00', currency: 'EUR' } } },
    });
    serve([p], { preview: {} });
    await renderApp(`/proposals/${p.id}`);

    expect(await screen.findByRole('link', { name: 'Salaire' })).toHaveAttribute('href', `/income/${p.targetItemId}`);
    expect(screen.getByRole('row', { name: /^amount/ })).toHaveTextContent(/1\s500,00\s€/);
  });
});
