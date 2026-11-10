import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { describe, expect, it } from 'vitest';
import type { YearReview } from '@/api/reports';
import { expectNoAxeViolations } from '@/test/axe';
import { pick } from '@/test/controls';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';

const eur = (amount: string) => ({ amount, currency: 'EUR' });
const part = (key: string, amount: string, low: string, high: string) => ({ key, amount: eur(amount), low: eur(low), high: eur(high) });

function review(over: Partial<YearReview> = {}): YearReview {
  return {
    year: 2025, start: '2024-12-31', end: '2025-12-31', toDate: false, years: [2024, 2025, 2026], findings: [],
    netWorth: {
      start: eur('100000.00'), end: eur('112000.00'), change: eur('12000.00'),
      parts: [
        part('investments.paid_in', '3000.00', '100000.00', '103000.00'),
        part('investments.value', '4500.00', '103000.00', '107500.00'),
        part('real_estate.paid_in', '6000.00', '107500.00', '113500.00'),
        part('real_estate.value', '-1500.00', '112000.00', '113500.00'),
        part('real_estate.moved', '0.00', '112000.00', '112000.00'),
      ],
      previousChange: eur('8000.00'),
      previousParts: [part('investments.paid_in', '2500.00', '92000.00', '94500.00'), part('investments.value', '-1000.00', '93500.00', '94500.00')],
    },
    investments: {
      startValue: eur('40000.00'), endValue: eur('47000.00'), contributions: eur('3000.00'), withdrawals: eur('500.00'),
      gain: eur('4500.00'), periodReturnPct: 10.2, previousGain: eur('2000.00'), previousPeriodReturnPct: 5.1, findings: [],
      envelopes: [
        { id: '00000000-0000-0000-0000-000000000001', name: 'PEA Alice', kind: 'pea', startValue: eur('25000.00'), endValue: eur('30000.00'),
          netContributions: eur('2000.00'), gain: eur('3000.00'), periodReturnPct: 11.5, staleStart: false, noStartValue: false },
        { id: '00000000-0000-0000-0000-000000000002', name: 'Assurance-vie Bob', kind: 'life_insurance', startValue: eur('15000.00'),
          endValue: eur('17000.00'), netContributions: eur('500.00'), gain: eur('1500.00'), periodReturnPct: 9.8, staleStart: true, noStartValue: false },
      ],
    },
    ...over,
  };
}

function serve(body: (year: string | null) => YearReview, seen: string[] = []) {
  server.use(http.get('*/api/v1/reports/year', ({ request }) => {
    const url = new URL(request.url);
    seen.push(url.search);
    return HttpResponse.json(body(url.searchParams.get('year')));
  }));
  return seen;
}

describe('YearReviewPage', () => {
  it('titles a past year « Bilan 2025 » and the current one « à ce jour »', async () => {
    serve(() => review());
    const { container, unmount } = await renderApp('/reports/year?year=2025');
    expect(await screen.findByRole('heading', { level: 1, name: 'Bilan 2025' })).toBeInTheDocument();
    await expectNoAxeViolations(container);
    unmount();

    const seen = serve(() => review({ year: 2026, end: '2026-11-10', toDate: true }));
    await renderApp('/reports/year');
    expect(await screen.findByRole('heading', { level: 1, name: 'Bilan 2026 — à ce jour' })).toBeInTheDocument();
    expect(seen).toEqual(['?scope=household']);
  });

  it('puts the picked year in the address and asks for it', async () => {
    const seen = serve((year) => review({ year: Number(year ?? 2025) }));
    const { router, user } = await renderApp('/reports/year?year=2025&scope=me');
    await screen.findByRole('heading', { level: 1, name: 'Bilan 2025' });

    await pick(user, 'Année du bilan', '2024');
    expect(router.state.location.search).toEqual({ scope: 'me', year: 2024 });
    expect(await screen.findByRole('heading', { level: 1, name: 'Bilan 2024' })).toBeInTheDocument();
    await waitFor(() => expect(seen).toContain('?scope=me&year=2024'));

    // The server's current year, the latest it offers, stays out of the address.
    await pick(user, 'Année du bilan', '2026');
    expect(router.state.location.search).toEqual({ scope: 'me' });
  });

  it('lists the parts of the net worth change and the year before', async () => {
    serve(() => review());
    await renderApp('/reports/year?year=2025');

    const section = await screen.findByRole('region', { name: 'Patrimoine net' });
    expect(within(section).getByText('2024 : +8 000,00 €')).toBeInTheDocument();
    const parts = within(section).getByRole('table', { name: 'Composantes de la variation du patrimoine' });
    expect(within(parts).getAllByRole('columnheader').map((c) => c.textContent)).toEqual(['Composante', '2025', '2024']);
    for (const [label, amount, before] of [['Versements', '+3 000,00 €', '+2 500,00 €'], ['Gains des marchés', '+4 500,00 €', '-1 000,00 €'],
      ['Capital remboursé', '+6 000,00 €', '—'], ['Réévaluation des biens', '-1 500,00 €', '—'], ['Achats et ventes', '0,00 €', '—']])
      expect(within(parts).getByRole('rowheader', { name: label }).closest('tr')).toHaveTextContent(`${label}${amount}${before}`);
  });

  it('lists a part of the year before alone, so its column adds up to its change', async () => {
    const base = review().netWorth!;
    // A sale last year, nothing moved this year.
    serve(() => review({ netWorth: { ...base, parts: base.parts.filter((p) => p.key !== 'real_estate.moved'),
      previousParts: [...base.previousParts, part('real_estate.moved', '-3000.00', '91000.00', '94000.00')] } }));
    await renderApp('/reports/year?year=2025');

    const parts = within(await screen.findByRole('region', { name: 'Patrimoine net' }))
      .getByRole('table', { name: 'Composantes de la variation du patrimoine' });
    expect(within(parts).getAllByRole('rowheader').map((r) => r.textContent)).toEqual(
      ['Début', 'Versements', 'Gains des marchés', 'Capital remboursé', 'Réévaluation des biens', 'Achats et ventes', 'Fin']);
    expect(within(parts).getByRole('rowheader', { name: 'Achats et ventes' }).closest('tr')).toHaveTextContent('Achats et ventes—-3 000,00 €');
  });

  it('shows one row per envelope with its return, its old start value marked', async () => {
    serve(() => review());
    await renderApp('/reports/year?year=2025');

    const section = await screen.findByRole('region', { name: 'Placements' });
    expect(within(section).getByText('2024 : +2 000,00 €')).toBeInTheDocument();
    const table = within(section).getByRole('table');
    const rows = within(table).getAllByRole('row').slice(1);
    expect(rows).toHaveLength(2);
    expect(rows[0]).toHaveTextContent(/^PEA Alice.*\+11,5 %$/);
    expect(rows[1]).toHaveTextContent(/^Assurance-vie Bob valeur de départ ancienne.*\+9,8 %$/);
    expect(within(rows[0]!).getByRole('link', { name: 'PEA Alice' })).toHaveAttribute('href', '/investments/00000000-0000-0000-0000-000000000001');
  });

  it('marks an envelope without a value at the start, counted at its amount paid in', async () => {
    const base = review().investments!;
    serve(() => review({ investments: { ...base, envelopes: base.envelopes.map((e, i) => (i === 0 ? { ...e, noStartValue: true } : e)) } }));
    await renderApp('/reports/year?year=2025');

    const rows = within(within(await screen.findByRole('region', { name: 'Placements' })).getByRole('table')).getAllByRole('row').slice(1);
    expect(rows[0]).toHaveTextContent(/^PEA Alice sans valeur au début : compté au montant versé.*\+11,5 %$/);
    expect(rows[1]).not.toHaveTextContent('sans valeur au début');
  });

  it('leaves out a part the server does not give', async () => {
    serve(() => review({ investments: null }));
    await renderApp('/reports/year?year=2025');

    await screen.findByRole('region', { name: 'Patrimoine net' });
    expect(screen.queryByRole('region', { name: 'Placements' })).not.toBeInTheDocument();
  });

  it('says when there is nothing to review', async () => {
    serve(() => review({ netWorth: null, investments: null }));
    await renderApp('/reports/year?year=2025');

    expect(await screen.findByText('Rien à montrer pour cette année dans ce périmètre.')).toBeInTheDocument();
  });

  it('says a missing rate keeps the net worth from being reviewed', async () => {
    serve(() => review({ netWorth: null, findings: ['core.fx_rate_missing'] }));
    await renderApp('/reports/year?year=2025');

    expect(await screen.findByText('Un taux de change manque : le patrimoine de l\'année ne peut pas être calculé.')).toBeInTheDocument();
    expect(screen.queryByRole('region', { name: 'Patrimoine net' })).not.toBeInTheDocument();
    expect(screen.queryByText('Rien à montrer pour cette année dans ce périmètre.')).not.toBeInTheDocument();
  });
});
