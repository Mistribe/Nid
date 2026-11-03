import { screen, waitFor, within } from '@testing-library/react';
import { beforeAll, beforeEach, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { resetFocus } from './focus';
import { proposal } from './testData';
import { serve } from './testServe';

const line = '00000000-0000-7000-8000-0000000000f1';
const link = (over: Parameters<typeof proposal>[0] = {}) => proposal({
  kind: 'banking.link_transaction', targetName: 'Netflix', sourceLabel: 'Synchronisation bancaire', subjectKey: line, createdVia: 'job',
  payload: { transactionId: line, module: 'subscriptions', itemId: '00000000-0000-7000-8000-0000000000e1', key: '2026-10-02' }, ...over,
});
const linkPreview = {
  transaction: { bookedOn: '2026-10-03', label: 'PRLV SEPA NETFLIX 0310', amount: '-13.49', currency: 'EUR' },
  charge: { itemName: 'Netflix', dueOn: '2026-10-02', amount: '13.49', currency: 'EUR' },
  gap: '0.00',
};

/** 5C §6.3 step 4: a bank line that pays one expected charge, linked once approved; never edited. */
describe('Une opération bancaire à relier', () => {
  beforeEach(resetFocus);
  beforeAll(async () => { await import('./ProposalDetailPage'); await import('./ProposalsPage'); });

  it('the link proposal shows the line, the charge and the gap', async () => {
    const p = link();
    const sent = serve([p], { preview: linkPreview });
    const { user, container } = await renderApp(`/proposals/${p.id}`);

    expect(await screen.findByRole('heading', { level: 1, name: 'Opération bancaire à relier' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Netflix' })).toHaveAttribute('href', `/subscriptions/${p.targetItemId}`);
    const table = within(screen.getByRole('table', { name: 'Comparaison' }));
    expect(table.getByRole('row', { name: /^Opération/ })).toHaveTextContent(/3 octobre 2026.*PRLV SEPA NETFLIX 0310.*-13,49\s€/);
    expect(table.getByRole('row', { name: /^Charge attendue/ })).toHaveTextContent(/Netflix.*2 octobre 2026.*13,49\s€/);
    expect(table.getByRole('row', { name: /^Écart/ })).toHaveTextContent(/0,00\s€/);
    expect(screen.queryByRole('textbox')).toBeNull();
    await expectNoAxeViolations(container);

    await user.click(screen.getByRole('button', { name: 'Approuver' }));
    await waitFor(() => expect(sent.approve).toEqual({ version: 1 }));
  });

  it('says when no charge is found any more, and links a loan instalment to its loan', async () => {
    const p = link({ payload: { transactionId: line, module: 'realestate', itemId: '00000000-0000-7000-8000-0000000000e1', key: '2026-10-05' } });
    serve([p], { preview: { transaction: linkPreview.transaction } });
    await renderApp(`/proposals/${p.id}`);

    expect(await screen.findByRole('row', { name: /^Charge attendue/ })).toHaveTextContent('—');
    expect(screen.getByRole('row', { name: /^Écart/ })).toHaveTextContent('—');
    expect(screen.getByRole('link', { name: 'Netflix' })).toHaveAttribute('href', `/loans/${p.targetItemId}`);
  });

  it('is listed by the item it would pay', async () => {
    serve([link()]);
    await renderApp('/proposals');
    const table = within(await screen.findByRole('table', { name: 'Liste des propositions' }));
    expect(table.getByRole('link', { name: 'Netflix — opération bancaire à relier' })).toBeInTheDocument();
  });
});
