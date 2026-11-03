import { screen, waitFor, within } from '@testing-library/react';
import { beforeAll, beforeEach, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { resetFocus } from './focus';
import { proposal } from './testData';
import { serve } from './testServe';

const recurring = (over: Parameters<typeof proposal>[0] = {}) => proposal({
  kind: 'banking.create_subscription', targetItemId: null, targetName: null, subjectKey: null, sourceLabel: 'Synchronisation bancaire', createdVia: 'job',
  payload: {
    accountId: '00000000-0000-7000-8000-0000000000b2', stem: 'PRLV SEPA SALLE DE SPORT', lines: ['l1', 'l2', 'l3'],
    subscription: {
      displayName: 'PRLV SEPA SALLE DE SPORT', visibility: 'private', owners: [{ personId: 'a', shareBp: 10000 }], autoRenew: true,
      phases: [{ startsOn: '2026-08-05', price: { amount: '29.90', currency: 'EUR' }, every: { count: 3, unit: 'month' }, payer: { type: 'person', personId: 'a' } }],
    },
  },
  ...over,
});
const recurringPreview = {
  account: 'Compte courant ·1234',
  cadence: 'quarterly',
  lines: [
    { bookedOn: '2026-02-05', amount: '-29.90', currency: 'EUR' },
    { bookedOn: '2026-05-05', amount: '-29.90', currency: 'EUR' },
    { bookedOn: '2026-08-05', amount: '-29.90', currency: 'EUR' },
  ],
};

/** 5C §6.4: a recurring debit no charge explains, proposed as a new subscription; never edited. */
describe('Un prélèvement récurrent', () => {
  beforeEach(resetFocus);
  beforeAll(async () => { await import('./ProposalDetailPage'); await import('./ProposalsPage'); });

  it('the recurring proposal shows the rhythm and the paying account', async () => {
    const p = recurring();
    const sent = serve([p], { preview: recurringPreview });
    const { user, container } = await renderApp(`/proposals/${p.id}`);

    expect(await screen.findByRole('heading', { level: 1, name: 'Prélèvement récurrent' })).toBeInTheDocument();
    expect(screen.getByText('Nouvel élément')).toBeInTheDocument();
    const table = within(screen.getByRole('table', { name: 'Comparaison' }));
    expect(table.getByRole('row', { name: /^Nom/ })).toHaveTextContent('PRLV SEPA SALLE DE SPORT');
    expect(table.getByRole('row', { name: /^Prix/ })).toHaveTextContent(/29,90\s€/);
    expect(table.getByRole('row', { name: /^Rythme/ })).toHaveTextContent('trimestriel');
    expect(table.getByRole('row', { name: /^Compte payeur/ })).toHaveTextContent('Compte courant ·1234');
    const lines = within(screen.getByRole('table', { name: 'Opérations repérées' })).getAllByRole('row');
    expect(lines).toHaveLength(4);
    expect(lines[3]).toHaveTextContent(/5 août 2026.*-29,90\s€/);
    expect(screen.queryByRole('textbox')).toBeNull();
    await expectNoAxeViolations(container);

    await user.click(screen.getByRole('button', { name: 'Approuver' }));
    await waitFor(() => expect(sent.approve).toEqual({ version: 1 }));
  });

  it('is listed by the name and price proposed', async () => {
    serve([recurring()]);
    await renderApp('/proposals');
    const table = within(await screen.findByRole('table', { name: 'Liste des propositions' }));
    expect(table.getByRole('link', { name: /^PRLV SEPA SALLE DE SPORT — prélèvement récurrent de 29,90\s€$/ })).toBeInTheDocument();
  });
});
