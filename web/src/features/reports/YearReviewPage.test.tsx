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
    income: {
      income: eur('50000.00'), engagements: eur('30000.00'), margin: eur('20000.00'),
      previousIncome: eur('48000.00'), previousEngagements: eur('29000.00'), previousMargin: eur('19000.00'),
      sources: [
        { id: '00000000-0000-0000-0000-0000000000a1', key: 'a1', name: 'Salaire Alice', kind: 'salary', amount: eur('40000.00') },
        { key: 'one_off', amount: eur('10000.00') },
        { id: '00000000-0000-0000-0000-0000000000a2', key: 'a2', kind: 'rent', amount: eur('5000.00') },
        { id: '00000000-0000-0000-0000-0000000000a3', key: 'a3', kind: 'pension', amount: null },
      ],
      engagementSources: [
        { source: 'loans', amount: eur('18000.00'), estimated: false },
        { source: 'tax', amount: eur('12000.00'), estimated: true },
        { source: 'childcare', amount: eur('3000.00'), estimated: true },
      ],
    },
    subscriptions: {
      cost: eur('1200.00'), previousCost: eur('1100.00'),
      started: [{ id: '00000000-0000-0000-0000-0000000000b1', name: 'Musique', provider: 'Ondes', on: '2025-03-01' }],
      ended: [{ id: '00000000-0000-0000-0000-0000000000b2', name: 'Vidéo', on: '2025-06-30' }],
      priceChanges: [{ id: '00000000-0000-0000-0000-0000000000b3', name: 'Box internet', on: '2025-09-01',
        before: eur('20.00'), beforeEvery: { count: 1, unit: 'month' }, after: eur('35.00'), afterEvery: { count: 1, unit: 'month' }, promoEnded: true }],
    },
    loans: {
      interest: eur('2000.00'), insurance: eur('300.00'), fees: eur('0.00'), capitalRepaid: eur('6000.00'),
      startBalance: eur('150000.00'), endBalance: eur('144000.00'), previousInterestAndInsurance: eur('2400.00'), previousCapitalRepaid: eur('5800.00'),
      loans: [
        { id: '00000000-0000-0000-0000-0000000000c1', name: 'Prêt maison', interest: eur('2000.00'), insurance: eur('300.00'), fees: eur('0.00'),
          capitalRepaid: eur('6000.00'), startBalance: eur('150000.00'), endBalance: eur('144000.00') },
        { id: '00000000-0000-0000-0000-0000000000c2', name: 'Prêt auto', interest: eur('0.00'), insurance: eur('0.00'), fees: eur('0.00'),
          capitalRepaid: eur('0.00'), startBalance: eur('0.00'), endBalance: eur('0.00'), archivedOn: '2025-02-15' },
      ],
    },
    properties: {
      costs: eur('2500.00'), rents: eur('9000.00'), previousCosts: eur('2300.00'), previousRents: eur('8800.00'),
      properties: [
        { id: '00000000-0000-0000-0000-0000000000d1', name: 'Studio Lyon', costs: eur('1500.00'), rents: eur('9000.00'), grossYieldPct: 4.4, netYieldPct: 3.6,
          byCategory: [{ category: 'property_tax', amount: eur('900.00') }, { category: 'condo_fees', amount: eur('600.00') }] },
        { id: '00000000-0000-0000-0000-0000000000d2', name: 'Maison', costs: eur('1000.00'), rents: eur('0.00'), grossYieldPct: null, netYieldPct: null,
          byCategory: [{ category: 'works', amount: eur('1000.00') }] },
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

  it('shows the income, engagements and margin with the year before, and their sources', async () => {
    serve(() => review());
    await renderApp('/reports/year?year=2025');

    const section = await screen.findByRole('region', { name: 'Revenus et marge' });
    for (const text of ['2024 : 48 000,00 €', '2024 : 29 000,00 €', '2024 : +19 000,00 €']) expect(within(section).getByText(text)).toBeInTheDocument();
    const sources = within(within(section).getByRole('table', { name: 'Revenus par source' })).getAllByRole('row').slice(1);
    expect(sources[0]).toHaveTextContent('Salaire Alice');
    expect(sources[1]).toHaveTextContent('Exceptionnels');
    const engagements = within(within(section).getByRole('table', { name: 'Engagements par poste' })).getAllByRole('row').slice(1);
    expect(engagements[0]).not.toHaveTextContent('estimé');
    expect(engagements[1]).toHaveTextContent(/^Impôts estimés[^é]*12/);
    expect(engagements[1]).not.toHaveTextContent('estimé estimé');
    expect(engagements[2]).toHaveTextContent(/^Garde estimé/);
    expect(sources[2]).toHaveTextContent('Loyers');
    expect(sources[3]).toHaveTextContent(/^PensionInconnu/);
  });

  it('says unknown for a margin and an amount the server could not give', async () => {
    const base = review().income!;
    serve(() => review({ income: { ...base, margin: null, previousMargin: null } }));
    await renderApp('/reports/year?year=2025');

    const section = await screen.findByRole('region', { name: 'Revenus et marge' });
    const margin = within(section).getByText('Marge avant dépenses courantes').closest('div')!;
    expect(within(margin).getByText('Inconnu')).toBeInTheDocument();
    expect(margin).not.toHaveTextContent('2024');
  });

  it('shows no income section without income', async () => {
    serve(() => review({ income: null }));
    await renderApp('/reports/year?year=2025');

    await screen.findByRole('region', { name: 'Patrimoine net' });
    expect(screen.queryByRole('region', { name: 'Revenus et marge' })).not.toBeInTheDocument();
  });

  it('leaves out a part the server does not give', async () => {
    serve(() => review({ investments: null }));
    await renderApp('/reports/year?year=2025');

    await screen.findByRole('region', { name: 'Patrimoine net' });
    expect(screen.queryByRole('region', { name: 'Placements' })).not.toBeInTheDocument();
  });

  it('shows the subscriptions cost with the year before, those started and ended, and a price change before → after', async () => {
    serve(() => review());
    await renderApp('/reports/year?year=2025');

    const section = await screen.findByRole('region', { name: 'Abonnements' });
    expect(within(section).getByText('1 200,00 €')).toBeInTheDocument();
    expect(within(section).getByText('2024 : 1 100,00 €')).toBeInTheDocument();
    const started = within(within(section).getByRole('table', { name: 'Abonnements commencés' })).getAllByRole('row').slice(1);
    expect(started[0]).toHaveTextContent('Musique1 mars 2025');
    expect(within(started[0]!).getByRole('link', { name: 'Musique' })).toHaveAttribute('href', '/subscriptions/00000000-0000-0000-0000-0000000000b1');
    expect(within(within(section).getByRole('table', { name: 'Abonnements terminés' })).getAllByRole('row')[1]).toHaveTextContent('Vidéo30 juin 2025');
    const change = within(within(section).getByRole('table', { name: 'Changements de prix' })).getAllByRole('row')[1];
    expect(change).toHaveTextContent('Box internet fin de promotion1 septembre 2025');
    expect(change).toHaveTextContent('20,00 € pour 1 mois → 35,00 € pour 1 mois');
  });

  it('shows the loans figures with the year before, one row per loan, an archived one dated', async () => {
    serve(() => review());
    await renderApp('/reports/year?year=2025');

    const section = await screen.findByRole('region', { name: 'Crédits' });
    for (const label of ['Intérêts', 'Assurance', 'Frais', 'Capital remboursé', 'Restant dû au début', 'Restant dû à la fin'])
      expect(within(section).getAllByText(label).length).toBeGreaterThan(0);
    expect(within(section).getByText('2024, assurance comprise : 2 400,00 €')).toBeInTheDocument();
    expect(within(section).getByText('2024 : 5 800,00 €')).toBeInTheDocument();
    const rows = within(within(section).getByRole('table', { name: 'Crédits de la période' })).getAllByRole('row').slice(1);
    expect(rows[0]).toHaveTextContent(/^Prêt maison2 000,00 €300,00 €0,00 €6 000,00 €150 000,00 €144 000,00 €$/);
    expect(rows[1]).toHaveTextContent(/^Prêt auto archivé le 15 février 2025/);
    expect(within(rows[0]!).getByRole('link', { name: 'Prêt maison' })).toHaveAttribute('href', '/loans/00000000-0000-0000-0000-0000000000c1');
  });

  it('shows the properties costs and rents with the year before, their yields, « non significatif » without one, and the costs by category', async () => {
    serve(() => review());
    await renderApp('/reports/year?year=2025');

    const section = await screen.findByRole('region', { name: 'Biens' });
    expect(within(section).getByText('2024 : 2 300,00 €')).toBeInTheDocument();
    expect(within(section).getByText('2024 : 8 800,00 €')).toBeInTheDocument();
    const rows = within(within(section).getByRole('table', { name: 'Biens de la période' })).getAllByRole('row').slice(1);
    expect(rows[0]).toHaveTextContent(/^Studio Lyon1 500,00 €9 000,00 €4,4 %3,6 %$/);
    expect(rows[1]).toHaveTextContent(/^Maison1 000,00 €0,00 €Non significatifNon significatif$/);
    expect(within(rows[0]!).getByRole('link', { name: 'Studio Lyon' })).toHaveAttribute('href', '/properties/00000000-0000-0000-0000-0000000000d1');
    const costs = within(within(section).getByRole('table', { name: 'Charges par catégorie' })).getAllByRole('row').slice(1);
    expect(costs).toHaveLength(3);
    ['Studio LyonTaxe foncière900,00 €', 'Studio LyonCharges de copropriété600,00 €', 'MaisonTravaux1 000,00 €']
      .forEach((text, i) => expect(costs[i]).toHaveTextContent(text));
  });

  it('leaves out the subscriptions, loans and properties the server does not give', async () => {
    serve(() => review({ subscriptions: null, loans: null, properties: null }));
    await renderApp('/reports/year?year=2025');

    await screen.findByRole('region', { name: 'Patrimoine net' });
    for (const name of ['Abonnements', 'Crédits', 'Biens']) expect(screen.queryByRole('region', { name })).not.toBeInTheDocument();
  });

  it('puts the sections in order: net worth, investments, income, subscriptions, loans, properties', async () => {
    serve(() => review());
    await renderApp('/reports/year?year=2025');

    await screen.findByRole('region', { name: 'Biens' });
    expect(within(screen.getByRole('main')).getAllByRole('heading', { level: 2 }).map((h) => h.textContent)).toEqual(['Patrimoine net', 'Placements', 'Revenus et marge', 'Abonnements', 'Crédits', 'Biens']);
  });

  it('says when there is nothing to review', async () => {
    serve(() => review({ netWorth: null, investments: null, income: null, subscriptions: null, loans: null, properties: null }));
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
