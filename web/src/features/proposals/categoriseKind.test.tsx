import { screen, waitFor, within } from '@testing-library/react';
import { beforeAll, beforeEach, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { resetFocus } from './focus';
import { proposal } from './testData';
import { serve } from './testServe';

const categorise = (over: Parameters<typeof proposal>[0] = {}) => proposal({
  kind: 'banking.categorise_transactions', targetItemId: null, targetName: null, subjectKey: null, sourceLabel: null, createdVia: 'mcp',
  payload: { lines: [
    { transactionId: 'l1', categoryId: 'c1' }, { transactionId: 'l2', categoryId: 'c1' }, { transactionId: 'l3', categoryId: 'c2' },
  ] },
  ...over,
});
const categorisePreview = {
  count: 3,
  hidden: 1,
  lines: [
    { transactionId: 'l1', bookedOn: '2026-10-05', label: 'CARREFOUR CITY', accountName: 'Courant B', amount: '-30.00', currency: 'EUR', current: null, proposed: 'Courses', applies: true },
    { transactionId: 'l2', bookedOn: '2026-10-06', label: 'DECATHLON', accountName: 'Courant A', amount: '-25.00', currency: 'EUR', current: 'Divers', proposed: 'Sport', applies: false },
  ],
};

/** Spec §Hints and the assistant: the assistant's categories for a batch of bank lines, approved as proposed or rejected. */
describe('Des catégories d’opérations bancaires', () => {
  beforeEach(resetFocus);
  beforeAll(async () => { await import('./ProposalDetailPage'); await import('./ProposalsPage'); });

  it('lists each line with its current and proposed category, and counts the hidden ones', async () => {
    const p = categorise();
    const sent = serve([p], { preview: categorisePreview });
    const { user, container } = await renderApp(`/proposals/${p.id}`);

    expect(await screen.findByRole('heading', { level: 1, name: 'Catégories d’opérations bancaires' })).toBeInTheDocument();
    expect(screen.queryByText('Nouvel élément')).toBeNull();
    expect(screen.getByText(/^3 opérations proposées\. 1 d’entre elles est sur un compte que vous ne voyez pas/)).toBeInTheDocument();
    const rows = within(screen.getByRole('table', { name: 'Comparaison' })).getAllByRole('row');
    expect(rows).toHaveLength(3);
    expect(rows[1]).toHaveTextContent(/5 octobre 2026.*CARREFOUR CITY.*Courant B.*-30,00\s€.*—.*Courses/);
    expect(rows[1]).not.toHaveTextContent('Ne sera pas modifiée');
    expect(rows[2]).toHaveTextContent(/DECATHLON.*Courant A.*-25,00\s€.*Divers.*Sport.*Ne sera pas modifiée/);
    expect(screen.queryByRole('textbox')).toBeNull();
    await expectNoAxeViolations(container);

    await user.click(screen.getByRole('button', { name: 'Approuver' }));
    await waitFor(() => expect(sent.approve).toEqual({ version: 1 }));
  });

  it('is listed by its count of lines', async () => {
    serve([categorise()]);
    await renderApp('/proposals');
    const table = within(await screen.findByRole('table', { name: 'Liste des propositions' }));
    expect(table.getByRole('link', { name: '3 opérations bancaires à catégoriser' })).toBeInTheDocument();
  });
});
