import { screen, waitFor, within } from '@testing-library/react';
import { beforeAll, beforeEach, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { resetFocus } from './focus';
import { proposal } from './testData';
import { serve } from './testServe';

const payload = {
  isin: 'FR0000120271', asOf: '2025-09-30',
  regions: [{ code: 'north_america', pct: 62.5 }, { code: 'europe_ex_france', pct: 37.5 }],
  sectors: [{ code: 'information_technology', pct: 100 }],
};
const breakdownPreview = {
  holdingName: 'Fonds A',
  proposed: payload,
  current: { asOf: '2025-01-15', regions: [{ code: 'north_america', pct: 100 }], sectors: null },
};
const breakdown = () => proposal({ kind: 'investments.record_breakdown', payload, subjectKey: 'FR0000120271:2025-09-30' });

describe('Une répartition proposée', () => {
  beforeEach(resetFocus);
  beforeAll(async () => { await import('./ProposalDetailPage'); await import('./ProposalsPage'); });

  it('shows the proposed shares next to the current ones', async () => {
    const p = breakdown();
    serve([p], { preview: breakdownPreview });
    const { container } = await renderApp(`/proposals/${p.id}`);

    expect(await screen.findByRole('heading', { level: 1, name: 'Répartition d’un support' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Répartition proposée de Fonds A' })).toBeInTheDocument();
    const regions = within(screen.getByRole('table', { name: 'Régions' })).getAllByRole('row');
    expect(regions[1]).toHaveTextContent(/Amérique du Nord\s*62,5\s*100,0/);
    expect(regions[2]).toHaveTextContent(/Europe hors France\s*37,5\s*—/);
    const sectors = within(screen.getByRole('table', { name: 'Secteurs' })).getAllByRole('row');
    expect(sectors[1]).toHaveTextContent(/100,0\s*—/);
    expect(screen.queryByRole('textbox')).toBeNull();
    await expectNoAxeViolations(container);
  });

  it('is approved as proposed', async () => {
    const p = breakdown();
    const sent = serve([p], { preview: breakdownPreview });
    const { user } = await renderApp(`/proposals/${p.id}`);

    await user.click(await screen.findByRole('button', { name: 'Approuver' }));

    await waitFor(() => expect(sent.approve).toEqual({ version: 1 }));
  });

  it('is listed with its ISIN and date', async () => {
    serve([breakdown()], { preview: breakdownPreview });
    await renderApp('/proposals');

    expect((await screen.findAllByText(/PEA — répartition de FR0000120271 au /))[0]).toBeInTheDocument();
  });
});
