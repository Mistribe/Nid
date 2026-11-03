import { screen, waitFor, within } from '@testing-library/react';
import { beforeAll, beforeEach, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { resetFocus } from './focus';
import { proposal } from './testData';
import { serve } from './testServe';

const eur = (amount: string) => ({ amount, currency: 'EUR' });
const payload = {
  asOf: '2025-09-30', sourceLabel: 'Information trimestrielle', recordValuation: true,
  lines: [
    { name: 'Fonds euros', value: '1400.00' },
    { name: 'Fonds A', isin: 'FR0000120271', units: '10.5', unitPrice: '100', value: '1050.00' },
    { name: '<b>Nouveau fonds</b>', kind: 'etf', value: '500.00' },
  ],
  movements: [{ holding: 'FR0000120271', on: '2025-09-15', kind: 'fee', units: '-0.1', amount: '10.00', label: 'Frais de gestion' }],
};
const holdingsPreview = {
  currency: 'EUR', total: eur('2950.00'), recordValuation: true,
  valuation: { current: null, previous: { asOf: '2025-06-30', amount: eur('2000.0000') }, proposed: { asOf: '2025-09-30', amount: eur('2950.00') } },
  lines: [
    { index: 0, name: 'Fonds euros', isin: null, kind: 'euro_fund', isNew: false, units: null, unitPrice: null, value: eur('1400.00'),
      costBasisPerUnit: null, previous: { asOf: '2025-06-30', value: eur('1000.0000') }, largeChange: true },
    { index: 1, name: 'Fonds A', isin: 'FR0000120271', kind: 'fund', isNew: false, units: '10.5', unitPrice: '100', value: eur('1050.00'),
      costBasisPerUnit: null, previous: { asOf: '2025-06-30', value: eur('1000.0000') }, largeChange: false },
    { index: 2, name: '<b>Nouveau fonds</b>', isin: null, kind: 'etf', isNew: true, units: null, unitPrice: null, value: eur('500.00'),
      costBasisPerUnit: null, previous: null, largeChange: false },
  ],
  movements: [{ index: 0, holding: 'Fonds A', on: '2025-09-15', kind: 'fee', units: '-0.1', unitPrice: null, amount: eur('10.00'), label: 'Frais de gestion', recorded: true }],
};
const holdings = () => proposal({
  kind: 'investments.record_holdings_snapshot', payload, subjectKey: '2025-09-30',
  warnings: [{ code: 'large_change', severity: 'warning', message: 'Line 1 moves more than 30 %.', field: 'payload.lines[0].value' }],
});

describe('Un relevé des supports proposé', () => {
  beforeEach(resetFocus);
  beforeAll(async () => { await import('./ProposalDetailPage'); await import('./ProposalsPage'); });

  it('shows the lines read only, the new holding and the large change marked', async () => {
    const p = holdings();
    serve([p], { preview: holdingsPreview });
    const { container } = await renderApp(`/proposals/${p.id}`);

    expect(await screen.findByRole('heading', { level: 1, name: 'Relevé des supports d’une enveloppe' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'PEA' })).toHaveAttribute('href', `/investments/${p.targetItemId}`);
    const lines = within(screen.getByRole('table', { name: 'Supports du relevé' })).getAllByRole('row');
    expect(lines[1]).toHaveTextContent('Fonds euros');
    expect(lines[1]).toHaveTextContent('Variation de plus de 30 %');
    expect(lines[2]).toHaveTextContent('10,5');
    expect(lines[2]).not.toHaveTextContent('Nouveau support');
    expect(lines[3]).toHaveTextContent('<b>Nouveau fonds</b>');
    expect(lines[3]).toHaveTextContent('Nouveau support');
    expect(container.querySelector('b')).toBeNull();
    expect(screen.getByText(/Total des lignes : 2\s950,00\s€/)).toBeInTheDocument();
    expect(screen.getByRole('row', { name: /Valeur proposée/ })).toHaveTextContent(/2\s950,00\s€/);
    expect(within(screen.getByRole('table', { name: 'Mouvements sur les supports' })).getAllByRole('row')[1]).toHaveTextContent('Déjà enregistré');
    expect(screen.queryByRole('textbox')).toBeNull();
    expect(screen.queryByRole('checkbox')).toBeNull();
    await expectNoAxeViolations(container);
  });

  it('is approved as proposed', async () => {
    const p = holdings();
    const sent = serve([p], { preview: holdingsPreview });
    const { user } = await renderApp(`/proposals/${p.id}`);

    await user.click(await screen.findByRole('button', { name: 'Approuver' }));

    await waitFor(() => expect(sent.approve).toEqual({ version: 1 }));
  });

  it('is listed with its date and line count', async () => {
    serve([holdings()], { preview: holdingsPreview });
    await renderApp('/proposals');

    expect((await screen.findAllByText(/PEA — supports au .+ \(3 lignes\)/))[0]).toBeInTheDocument();
  });
});
