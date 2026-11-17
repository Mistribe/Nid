import { screen, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { jointAssessment } from '../assessments/testData';
import { ADULT, estimate } from '../taxTestData';

const GAPS = (id: string) => ({
  assessment: id,
  lines: [
    { code: 'balance', notice: '-312.00', nid: '200.00', gap: null },
    { code: 'net_tax', notice: '2688.00', nid: '2500.00', gap: '188.00' },
  ],
  declarants: [{ person: ADULT, notice: '46800.00', nid: '46000.00', gap: '800.00' }],
});

describe('AssessmentTab', () => {
  beforeAll(async () => { await import('../TaxPage'); }, 30_000);

  const open = async (withGaps: boolean) => {
    const a = jointAssessment({ year: 2025 });
    const loaded = estimate();
    loaded.adults = [];
    loaded.gaps = withGaps ? GAPS(a.id) : null;
    server.use(
      http.get('*/api/v1/tax/assessments', () => HttpResponse.json({ items: [a] })),
      http.get('*/api/v1/tax', () => HttpResponse.json(loaded)),
      http.get('*/api/v1/core/persons', () => HttpResponse.json([{ id: ADULT, kind: 'adult', displayName: 'Alice', email: null, retirementAge: 64, retirementMonths: 0, signedIn: true }])),
    );
    return renderApp('/tax?tab=assessment&year=2025');
  };

  it('Shows_the_gaps_in_a_card_above_their_notice', async () => {
    await open(true);
    const gaps = await screen.findByRole('region', { name: 'Écarts avec l’estimation' });
    const notice = screen.getByRole('region', { name: 'Avis 2025' });
    expect(gaps.compareDocumentPosition(notice) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    const rows = within(gaps).getAllByRole('row').slice(1);
    expect(rows.map((r) => r.firstChild?.textContent)).toEqual(['Impôt net', 'Solde', 'Salaires nets — Alice']);
    expect(rows[0]).toHaveTextContent('+188,00 €');
    expect(rows[1]).toHaveTextContent(/remboursé/);
    expect(rows[1]).toHaveTextContent(/à payer/);
    expect(rows[1]!.textContent).not.toMatch(/^-|\s-\d/);
    expect(rows[2]).toHaveTextContent('+800,00 €');
    expect(within(gaps).getByText('Écart = avis − estimation de Nid')).toBeInTheDocument();
    expect(within(gaps).getByText(/prélèvement forfaitaire de 12,8 %/)).toBeInTheDocument();
  });

  it('No_gap_card_without_gaps', async () => {
    await open(false);
    await screen.findByRole('region', { name: 'Avis 2025' });
    expect(screen.queryByRole('region', { name: 'Écarts avec l’estimation' })).not.toBeInTheDocument();
  });

  it('opens on its question, with cards and no bare section', async () => {
    const { container } = await open(true);
    await screen.findByRole('region', { name: 'Avis 2025' });
    const panel = screen.getByRole('tabpanel');
    expect(within(panel).getByRole('heading', { name: 'Ce que l’administration a calculé, et l’écart avec Nid' })).toBeInTheDocument();
    expect(panel.querySelectorAll('section')).toHaveLength(0);
    await expectNoAxeViolations(container);
  });
});
