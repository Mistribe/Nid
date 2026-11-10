import { screen, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { RetirementView } from './api';

const ALICE = '11111111-1111-4111-8111-111111111111';
const BOB = '22222222-2222-4222-8222-222222222222';
const SOURCE = '33333333-3333-4333-8333-333333333333';

const view = (over: Partial<RetirementView> = {}): RetirementView => ({
  currency: 'EUR', horizonOn: '2066-11-10',
  departures: [
    { person: ALICE, name: 'Alice', birthDate: '1970-05-20', years: 62, months: 9, departure: '2033-02', pension: '1500.00', sources: [{ source: SOURCE, name: 'Pension de retraite', monthly: '1500.00', currency: 'EUR' }] },
    { person: BOB, name: 'Bob', birthDate: '1980-01-01', years: 64, months: 0, departure: '2044-01', pension: null, sources: [] },
  ],
  figures: { month: '2033-02', incomeNow: '4000.00', incomeAtRetirement: '3000.00', gap: '1000.00', capitalNeeded: '300000.00', capitalAtRetirement: '250000.00' },
  financialIndependence: '2040-06',
  independence: [],
  assumptions: {
    classRates: {}, inflation: '2', monthlySavings: null, savingsClass: 'equity', freedPaymentsToSavings: true, currency: 'EUR',
    inflationByYear: {}, classRatesByYear: {}, incomeGrowth: '0', classVolatility: {}, withdrawalRate: '4', monthlyLivingCost: '2500.00',
  },
  warnings: [],
  ...over,
});

function serve(body: RetirementView, seen: string[] = []) {
  server.use(http.get('*/api/v1/retirement', ({ request }) => { seen.push(new URL(request.url).search); return HttpResponse.json(body); }));
}

beforeAll(async () => { await import('./RetirementPage'); }, 30_000);

describe('the retirement page', () => {
  it('shows the figures, the departures and the assumptions', async () => {
    serve(view());
    await renderApp('/retirement');

    expect(await screen.findByRole('heading', { level: 1, name: 'Retraite' })).toBeInTheDocument();
    const figures = (await screen.findByText("Revenu à la retraite")).closest('dl')!;
    expect(within(figures).getByText('Écart mensuel')).toBeInTheDocument();
    expect(within(figures).getByText(/1\s000,00\s€/)).toBeInTheDocument();
    expect(within(figures).getByText('juin 2040')).toBeInTheDocument();
    expect(screen.getByText(/Chiffres indicatifs/)).toBeInTheDocument();

    const rows = within(await screen.findByRole('table', { name: 'Départs à la retraite' })).getAllByRole('row');
    expect(within(rows[1]!).getByText('62 ans et 9 mois')).toBeInTheDocument();
    expect(within(rows[1]!).getByText('février 2033')).toBeInTheDocument();
    expect(within(rows[1]!).getByRole('link', { name: 'Pension de retraite' })).toHaveAttribute('href', `/income/${SOURCE}`);
    expect(within(rows[2]!).getByText('64 ans')).toBeInTheDocument();

    expect(screen.getByText(/Taux de retrait : 4 %/)).toBeInTheDocument();
    expect(screen.getByText(/Coût de la vie mensuel : 2\s500,00\s€/)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Modifier les hypothèses' })).toHaveAttribute('href', '/settings/projection');
  });

  it('lists the adults without a figure when nobody has a birth date', async () => {
    serve(view({
      figures: null, financialIndependence: null,
      departures: [{ person: ALICE, name: 'Alice', birthDate: null, years: 64, months: 0, departure: null, pension: null, sources: [] }],
    }));
    await renderApp('/retirement');

    expect(await screen.findByText('Aucun départ dans les 40 ans : renseignez une date de naissance dans Réglages › Projection.')).toBeInTheDocument();
    const row = within(screen.getByRole('table', { name: 'Départs à la retraite' })).getAllByRole('row')[1]!;
    expect(within(row).getAllByText('—')).toHaveLength(2);
  });

  it('points to the settings when the living cost is missing, instead of saying the independence is out of reach', async () => {
    serve(view({
      financialIndependence: null, warnings: [{ code: 'living_cost_missing' }],
      assumptions: { ...view().assumptions, monthlyLivingCost: '0.00' },
    }));
    await renderApp('/retirement');

    const link = await screen.findByRole('link', { name: /Renseignez le coût de la vie mensuel/ });
    expect(link).toHaveAttribute('href', '/settings/projection');
    expect(screen.queryByText("Non atteinte avant l'horizon")).not.toBeInTheDocument();
  });

  it('says the independence is out of reach when a living cost is set', async () => {
    serve(view({ financialIndependence: null }));
    await renderApp('/retirement');

    expect(await screen.findByText("Non atteinte avant l'horizon")).toBeInTheDocument();
  });

  it('asks for the scope and shows the projection warnings', async () => {
    const seen: string[] = [];
    serve(view({ warnings: [{ code: 'fx_missing', detail: 'CHF' }] }), seen);
    await renderApp('/retirement?scope=me');

    expect(await screen.findByText(/Taux de change manquant \(CHF\)/)).toBeInTheDocument();
    expect(seen[0]).toContain('scope=me');
  });
});
