import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { aliceSession } from '@/test/fixtures';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { RetirementView } from './api';

const ALICE = aliceSession.person.id;
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

beforeAll(async () => { await Promise.all([import('./RetirementPage'), import('./overview/OverviewTab'), import('./IndependenceChart')]); }, 30_000);

describe('the retirement page', () => {
  it('opens on the gap, with the other figures beside it, and keeps the analysis and the departures in their tabs', async () => {
    serve(view());
    const { user } = await renderApp('/retirement');

    expect(await screen.findByRole('heading', { level: 1, name: 'Retraite' })).toBeInTheDocument();
    expect(screen.getAllByRole('tab').map((tab) => tab.textContent)).toEqual(['Vue d’ensemble', 'Analyse', 'Départs']);
    const hero = await screen.findByRole('region', { name: 'Écart mensuel' });
    expect(within(hero).getByText('Au départ, février 2033')).toBeInTheDocument();
    const figures = within(hero).getByText('Revenu à la retraite').closest('dl')!;
    expect(within(figures).getByText(/300\s000,00\s€/)).toBeInTheDocument();
    expect(within(figures).getByText('juin 2040')).toBeInTheDocument();
    expect(screen.getByText(/Chiffres indicatifs/)).toBeInTheDocument();
    expect(screen.queryByRole('table', { name: 'Départs à la retraite' })).not.toBeInTheDocument();

    await user.click(screen.getByRole('tab', { name: 'Analyse' }));
    const capital = await screen.findByRole('region', { name: 'Capital au départ' });
    expect(within(capital).getByText(/250\s000,00\s€/)).toBeInTheDocument();
    expect(within(capital).getByText('au taux de retrait de 4 %')).toBeInTheDocument();
    expect(screen.getByText(/Taux de retrait : 4 %/)).toBeInTheDocument();
    expect(screen.getByText(/Coût de la vie mensuel : 2\s500,00\s€/)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Modifier les hypothèses' })).toHaveAttribute('href', '/settings/projection');

    await user.click(screen.getByRole('tab', { name: 'Départs' }));
    const rows = within(await screen.findByRole('table', { name: 'Départs à la retraite' })).getAllByRole('row');
    expect(within(rows[1]!).getByText('62 ans et 9 mois')).toBeInTheDocument();
    expect(within(rows[1]!).getByText('février 2033')).toBeInTheDocument();
    expect(within(rows[1]!).getByRole('link', { name: 'Pension de retraite' })).toHaveAttribute('href', `/income/${SOURCE}`);
    expect(within(rows[2]!).getByText('64 ans')).toBeInTheDocument();
  });

  it('lists the adults without a figure when nobody has a birth date', async () => {
    serve(view({
      figures: null, financialIndependence: null,
      departures: [{ person: ALICE, name: 'Alice', birthDate: null, years: 64, months: 0, departure: null, pension: null, sources: [] }],
    }));
    const { user } = await renderApp('/retirement');

    expect(await screen.findByRole('heading', { name: 'Aucun départ à la retraite à calculer' })).toBeInTheDocument();
    expect(screen.getByText('Aucun départ dans les 40 ans : renseignez une date de naissance dans Réglages › Personnes.')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Ouvrir Réglages › Personnes' })).toHaveAttribute('href', '/settings/persons');
    expect(screen.queryByRole('region', { name: 'Écart mensuel' })).not.toBeInTheDocument();

    await user.click(screen.getByRole('tab', { name: 'Départs' }));
    const row = within(await screen.findByRole('table', { name: 'Départs à la retraite' })).getAllByRole('row')[1]!;
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
    // The link reads under the hero, not squeezed into a figure.
    expect(link.closest('dl')).toBeNull();
    expect(within(screen.getByRole('region', { name: 'Écart mensuel' })).getByText('Non calculée')).toBeInTheDocument();
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

  it('reads the living cost as not set when it is 0, and still points to the settings with no figure', async () => {
    serve(view({
      figures: null, financialIndependence: null, warnings: [{ code: 'living_cost_missing' }],
      assumptions: { ...view().assumptions, monthlyLivingCost: '0.00' },
    }));
    const { user } = await renderApp('/retirement');

    expect(await screen.findByText(/Aucun départ dans les 40 ans/)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /Renseignez le coût de la vie mensuel/ })).toHaveAttribute('href', '/settings/projection');

    await user.click(screen.getByRole('tab', { name: 'Analyse' }));
    expect(await screen.findByText(/Coût de la vie mensuel : non renseigné/)).toBeInTheDocument();
    expect(screen.queryByRole('region', { name: 'Capital au départ' })).not.toBeInTheDocument();
  });

  describe('the row actions', () => {
    const alone = (over = {}) => view({ departures: [
      { person: ALICE, name: 'Alice', birthDate: '1970-05-20', years: 60, months: 0, departure: '2030-06', pension: null, sources: [], ...over },
      { person: BOB, name: 'Bob', birthDate: '1980-01-01', years: 64, months: 0, departure: '2044-01', pension: null, sources: [] },
    ] });
    const menuOf = async (name: string) => {
      const user = userEvent.setup();
      // Both layouts render under jsdom: the table's button is the first.
      await user.click((await screen.findAllByRole('button', { name: `Actions pour ${name}` }))[0]!);
      return user;
    };

    it('edits the departure and refetches the page', async () => {
      let reads = 0;
      let put: unknown;
      server.use(
        http.get('*/api/v1/retirement', () => { reads += 1; return HttpResponse.json(alone()); }),
        http.put('*/api/v1/core/persons/:id/retirement', async ({ request }) => { put = await request.json(); return new HttpResponse(null, { status: 204 }); }),
      );
      await renderApp('/retirement?tab=departures');
      const user = await menuOf('Bob');
      await user.click(await screen.findByRole('menuitem', { name: 'Modifier le départ' }));
      expect(await screen.findByLabelText("Âge de départ à la retraite")).toHaveValue(64);
      await user.click(screen.getByRole('button', { name: 'Enregistrer' }));

      await waitFor(() => expect(reads).toBe(2));
      expect(put).toMatchObject({ retirementAge: 64, retirementMonths: 0 });
    });

    it('enters the reader\'s own pension from the month after the departure, shared by default, and refetches', async () => {
      let reads = 0;
      const posted: unknown[] = [];
      server.use(
        http.get('*/api/v1/retirement', () => { reads += 1; return HttpResponse.json(alone()); }),
        http.post('*/api/v1/income/sources', async ({ request }) => { posted.push(await request.json()); return HttpResponse.json({ id: SOURCE }, { status: 201 }); }),
      );
      await renderApp('/retirement?tab=departures');
      const user = await menuOf('Alice');
      await user.click(await screen.findByRole('menuitem', { name: 'Saisir la pension' }));
      await user.type(await screen.findByLabelText('Pension mensuelle nette'), '1 500,50');
      await user.click(screen.getByRole('button', { name: 'Enregistrer' }));
      await waitFor(() => expect(reads).toBe(2));

      expect(posted[0]).toEqual({
        displayName: 'Pension de retraite', kind: 'pension', currency: 'EUR', visibility: 'shared',
        owners: [{ personId: ALICE, shareBp: 10000 }],
        phases: [{ label: 'Pension', startsOn: '2030-07-01', netAmount: { amount: '1500.50', currency: 'EUR' }, every: { count: 1, unit: 'month' } }],
      });
    });

    it('sends private when the checkbox is cleared, and rolls December over to January', async () => {
      const posted: { visibility?: string; phases?: { startsOn: string }[] }[] = [];
      server.use(
        http.get('*/api/v1/retirement', () => HttpResponse.json(alone({ departure: '2030-12' }))),
        http.post('*/api/v1/income/sources', async ({ request }) => { posted.push(await request.json() as never); return HttpResponse.json({ id: SOURCE }, { status: 201 }); }),
      );
      await renderApp('/retirement?tab=departures');
      const user = await menuOf('Alice');
      await user.click(await screen.findByRole('menuitem', { name: 'Saisir la pension' }));
      await user.type(await screen.findByLabelText('Pension mensuelle nette'), '1000');
      await user.click(screen.getByRole('checkbox', { name: "Visible de l'autre adulte" }));
      await user.click(screen.getByRole('button', { name: 'Enregistrer' }));

      await waitFor(() => expect(posted).toHaveLength(1));
      expect(posted[0]).toMatchObject({ visibility: 'private', phases: [{ startsOn: '2031-01-01' }] });
    });

    it('offers the pension to nobody else, nor on a row that has a source or no departure', async () => {
      serve(view());
      await renderApp('/retirement?tab=departures');
      const user = await menuOf('Alice');
      expect(screen.queryByRole('menuitem', { name: 'Saisir la pension' })).not.toBeInTheDocument();
      await user.keyboard('{Escape}');
      await menuOf('Bob');
      expect(screen.getByRole('menuitem', { name: 'Modifier le départ' })).toBeInTheDocument();
      expect(screen.queryByRole('menuitem', { name: 'Saisir la pension' })).not.toBeInTheDocument();
    });

    it('in « Foyer », does not offer a second pension when the reader has a private one, and says so', async () => {
      const seen: string[] = [];
      server.use(
        http.get('*/api/v1/retirement', () => HttpResponse.json(alone())),
        http.get('*/api/v1/income/sources', ({ request }) => {
          seen.push(new URL(request.url).search);
          return HttpResponse.json({ items: [{ id: SOURCE, displayName: 'Pension de retraite', kind: 'pension', currency: 'EUR', visibility: 'private', owners: [{ personId: ALICE, shareBp: 10000 }] }] });
        }),
      );
      await renderApp('/retirement?tab=departures');
      expect((await screen.findAllByText('Votre pension est privée : elle ne compte que dans « Moi ».')).length).toBeGreaterThan(0);
      await menuOf('Alice');
      expect(screen.queryByRole('menuitem', { name: 'Saisir la pension' })).not.toBeInTheDocument();
      expect(seen[0]).toContain('scope=me');
      expect(seen[0]).toContain('kind=pension');
    });

    it('does not offer the pension when the reader has no departure month', async () => {
      serve(alone({ departure: null, birthDate: null }));
      await renderApp('/retirement?tab=departures');
      await menuOf('Alice');
      expect(screen.queryByRole('menuitem', { name: 'Saisir la pension' })).not.toBeInTheDocument();
    });
  });
});

describe('the independence curve', () => {
  const points = [{ on: '2040-01-01', capital: '100000.00', covered: '300.00', expenses: '2500.00' }, { on: '2040-06-01', capital: '750000.00', covered: '2500.00', expenses: '2500.00' }];

  it('summarises the independence month', async () => {
    serve(view({ independence: points }));
    await renderApp('/retirement');
    expect(await screen.findByRole('img', { name: /Indépendance financière : juin 2040/ })).toBeInTheDocument();
    expect(screen.getByText('Revenus couverts')).toBeInTheDocument();
  });

  it('does not say unreached when the living cost is missing', async () => {
    serve(view({ independence: points, financialIndependence: null, warnings: [{ code: 'living_cost_missing', detail: null }] }));
    await renderApp('/retirement');
    expect(await screen.findByRole('img', { name: /non calculée \(coût de la vie non renseigné\)/ })).toBeInTheDocument();
  });

  it('offers the year-end table', async () => {
    serve(view({ independence: points }));
    const { user } = await renderApp('/retirement');
    await user.click(await screen.findByRole('button', { name: 'Afficher en tableau' }));
    expect(within(await screen.findByRole('table', { name: /en tableau/ })).getByText('juin 2040')).toBeInTheDocument();
  });

  it('says when it is not reached', async () => {
    serve(view({ independence: points, financialIndependence: null }));
    await renderApp('/retirement');
    expect(await screen.findByRole('img', { name: /Non atteinte avant l'horizon/ })).toBeInTheDocument();
  });
});
