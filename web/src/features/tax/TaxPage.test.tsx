import { screen, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { Rental2044 } from './api';
import { ADULT, estimate, result } from './taxTestData';

describe('TaxPage', () => {
  beforeAll(async () => { await import('./TaxPage'); }, 30_000);

  it('shows the server’s figures, the notes and the steps', async () => {
    server.use(http.get('*/api/v1/tax', () => HttpResponse.json(estimate())));
    const { container, user } = await renderApp('/tax');

    expect(await screen.findByText('Net fiscal')).toBeInTheDocument();
    expect(screen.getAllByText('64 407,00 €').length).toBeGreaterThan(0);
    expect(screen.getAllByText('5 729,42 €').length).toBeGreaterThan(0);
    expect(screen.getAllByText('11 %').length).toBeGreaterThan(0);
    expect(screen.getByText(/Prime : pas de brut renseigné, le net versé \(1 000,00 €\)/)).toBeInTheDocument();
    expect(screen.getByText(/celui des revenus 2025 est repris/)).toBeInTheDocument();
    const table = screen.getByRole('table', { name: 'Calcul de l’impôt étape par étape' });
    expect(within(table).getByRole('row', { name: /Crédit garde hors du domicile/ })).toHaveTextContent('3 000,00 €');
    const people = screen.getByRole('table', { name: 'Revenus par déclarant' });
    expect(within(people).getByRole('columnheader', { name: 'Alice' })).toBeInTheDocument();
    expect(within(people).getByRole('row', { name: /Abattement de 10 %/ })).toHaveTextContent('4 042,46 €');
    expect(within(people).queryByRole('row', { name: /PER/ })).not.toBeInTheDocument();
    expect(within(table).queryByRole('row', { name: /Alice/ })).not.toBeInTheDocument();
    expect(screen.getByText('2 973,39 €')).toBeInTheDocument();
    expect(screen.getByText('remboursés')).toBeInTheDocument();
    expect(screen.getByRole('img', { name: /^Reste .+, Impôt sur le revenu 247,00/ })).toBeInTheDocument();
    expect(within(table).queryByRole('row', { name: /Réduction Pinel/ })).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: /Afficher les \d+ lignes à 0 €/ }));
    expect(within(table).getByRole('row', { name: /Réduction Pinel/ })).toHaveTextContent('0,00 €');
    await expectNoAxeViolations(container);
  });

  it('lists what to type on impots.gouv.fr and the rate that should follow', async () => {
    server.use(http.get('*/api/v1/tax', () => HttpResponse.json(estimate())));
    const { container } = await renderApp('/tax?tab=withholding');

    const declared = await screen.findByRole('table', { name: 'Revenus estimés à déclarer' });
    expect(within(declared).getByRole('row', { name: /Alice : salaires nets imposables/ })).toHaveTextContent('40 424,63 €');
    expect(within(declared).queryByText(/PER/)).not.toBeInTheDocument();
    const expected = screen.getByRole('table', { name: 'Ce que le taux devrait donner' });
    expect(within(expected).getByRole('row', { name: /Taux du foyer/ })).toHaveTextContent('4,5 %');
    expect(within(expected).getByRole('row', { name: /Alice : prélevé chaque mois/ })).toHaveTextContent('151,59 €');
    expect(within(expected).getByRole('row', { name: /Solde à la déclaration/ })).toHaveTextContent('-2 973,39 €');
    await expectNoAxeViolations(container);
  });

  it('simulates edited inputs beside the estimate', async () => {
    const sent: unknown[] = [];
    server.use(
      http.get('*/api/v1/tax', () => HttpResponse.json(estimate())),
      http.post('*/api/v1/tax/simulate', async ({ request }) => {
        sent.push(await request.json());
        return HttpResponse.json(result({ incomeTax: '1500.00', netAfterTax: '67500.00' }));
      }),
    );
    const { user } = await renderApp('/tax?tab=simulation');

    const gross = await screen.findByLabelText('Salaire brut annuel');
    await user.clear(gross);
    await user.type(gross, '60000,50');
    await user.click(screen.getByRole('button', { name: 'Calculer' }));

    const table = await screen.findByRole('table', { name: 'Estimation et simulation' });
    expect(within(table).getByRole('row', { name: /^Impôt sur le revenu/ })).toHaveTextContent('247,00 €1 500,00 €');
    expect(sent[0]).toMatchObject({ declarants: [{ salaryGross: '60000.50', status: 'non_executive' }], childcareOutsideHome: '6000.00' });
  });

  it('saves the profile for the year', async () => {
    const sent: unknown[] = [];
    server.use(
      http.get('*/api/v1/tax', () => HttpResponse.json(estimate())),
      http.put('*/api/v1/tax/profile/:year', async ({ request, params }) => {
        sent.push({ year: params.year, body: await request.json() });
        return HttpResponse.json(estimate());
      }),
    );
    const { user } = await renderApp('/tax?tab=profile');

    const home = await screen.findByLabelText('Emploi à domicile');
    await user.clear(home);
    await user.type(home, '9000');
    await user.type(screen.getByLabelText('Taux de cotisations de Alice (%)'), '23,5');
    await user.type(screen.getByLabelText('Plafonds PER non utilisés de Alice'), '4000');
    await user.click(screen.getByRole('button', { name: 'Enregistrer le profil' }));

    expect(await screen.findByText('Profil enregistré.')).toBeInTheDocument();
    expect(sent[0]).toMatchObject({ year: '2026', body: {
      homeEmployment: '9000', childrenUnder6: 2, dependents: null, statuses: { [ADULT]: 'non_executive' },
      contributionRates: { [ADULT]: '23.5' }, actualExpenses: {}, perUnusedCeilings: { [ADULT]: '4000' },
    } });
  });
});

const view2044 = (over: Partial<Rental2044> = {}): Rental2044 => ({
  year: 2025, regime: 'real', provisional: false,
  properties: [
    { id: 'a', name: 'Studio', lines: [{ line: '211', amount: '6000.00' }, { line: '263', amount: '4500.00' }] },
    { id: 'b', name: 'Loft', lines: [{ line: '211', amount: '1200.00' }] },
  ],
  totals: {
    receipts: '7200.00', charges: '1500.00', interest: '0.00', result: '5700.00', interestDeficit: '0.00', chargesDeficit: '0.00',
    globalDeficit: '0.00', carriedDeficit: '0.00', box4BA: '5700.00', box4BB: '0.00', box4BC: '0.00', box4BE: '0.00',
  },
  toCheck: [], notices: [],
  ...over,
});

describe('TaxPage, Déclaration 2044 tab', () => {
  beforeAll(async () => { await import('./TaxPage'); }, 30_000);

  it('asks last year, shows each property’s lines, the figures, the to-check list and the provisional mark', async () => {
    const asked: string[] = [];
    const loaded = view2044({
      provisional: true,
      toCheck: [{ property: 'a', name: 'Studio', occurredOn: '2025-04-02', category: 'works', note: 'Salle de bain', amount: '3000.00' }],
      notices: [{ code: 'rental_charges_removed', item: 'a', name: 'Studio', amount: '600.00' }],
    });
    server.use(
      http.get('*/api/v1/tax', ({ request }) => { asked.push(`tax ${new URL(request.url).searchParams.get('year')}`); return HttpResponse.json(estimate()); }),
      http.get('*/api/v1/tax/2044', ({ request }) => { asked.push(`2044 ${new URL(request.url).searchParams.get('year')}`); return HttpResponse.json(loaded); }),
    );
    const { container } = await renderApp('/tax?tab=2044');

    const studio = await screen.findByRole('region', { name: 'Studio' });
    expect(within(studio).getByText('211')).toBeInTheDocument();
    expect(within(studio).getByText('Loyers bruts')).toBeInTheDocument();
    expect(within(studio).getByText('6 000,00 €')).toBeInTheDocument();
    expect(screen.getByRole('region', { name: 'Loft' })).toBeInTheDocument();
    expect(screen.getByText('Provisoire : l’année n’est pas terminée.')).toBeInTheDocument();
    expect(screen.getByText('Recettes')).toBeInTheDocument();
    expect(screen.getAllByText('5 700,00 €').length).toBeGreaterThan(0);
    const check = screen.getByRole('region', { name: 'À vérifier' });
    expect(within(check).getByRole('link', { name: 'Studio' })).toHaveAttribute('href', '/properties/a?tab=expenses');
    expect(within(check).getByText('Travaux')).toBeInTheDocument();
    expect(screen.getByText(/Studio : 600,00 € de charges/)).toBeInTheDocument();
    expect(asked).toContain('2044 2025');
    expect(asked).toContain('tax 2025');
    await expectNoAxeViolations(container);
  });

  it('shows case 4BE and no form to fill under the micro-foncier', async () => {
    // The server always sends the columns; under micro none is shown, nor the to-check list.
    const micro = view2044({
      regime: 'micro', totals: { ...view2044().totals, box4BE: '7200.00' },
      toCheck: [{ property: 'a', name: 'Studio', occurredOn: '2025-04-02', category: 'works', note: null, amount: '3000.00' }],
    });
    server.use(
      http.get('*/api/v1/tax', () => HttpResponse.json(estimate())),
      http.get('*/api/v1/tax/2044', () => HttpResponse.json(micro)),
    );
    await renderApp('/tax?tab=2044');

    expect(await screen.findByText('Pas de 2044 à remplir au micro-foncier')).toBeInTheDocument();
    expect(screen.getByText('4BE')).toBeInTheDocument();
    expect(screen.getByText('7 200,00 €')).toBeInTheDocument();
    expect(screen.queryByRole('region', { name: 'Studio' })).not.toBeInTheDocument();
    expect(screen.queryByRole('region', { name: 'Loft' })).not.toBeInTheDocument();
    expect(screen.queryByRole('region', { name: 'À vérifier' })).not.toBeInTheDocument();
    expect(screen.queryByText('211')).not.toBeInTheDocument();
  });

  it('numbers the deficit lines 435 and 438 when the interest exceeds the rents, 440 and 441 otherwise', async () => {
    const totals = { ...view2044().totals, result: '-500.00', globalDeficit: '500.00', carriedDeficit: '100.00' };
    const answer = (interestDeficit: string) => server.use(
      http.get('*/api/v1/tax', () => HttpResponse.json(estimate())),
      http.get('*/api/v1/tax/2044', () => HttpResponse.json(view2044({ totals: { ...totals, interestDeficit } }))),
    );
    answer('300.00');
    const first = await renderApp('/tax?tab=2044');
    expect(await screen.findByText('435')).toBeInTheDocument();
    expect(screen.getByText('438')).toBeInTheDocument();
    expect(screen.queryByText('440')).not.toBeInTheDocument();
    first.unmount();
    answer('0.00');
    await renderApp('/tax?tab=2044');
    expect(await screen.findByText('440')).toBeInTheDocument();
    expect(screen.getByText('441')).toBeInTheDocument();
    expect(screen.queryByText('435')).not.toBeInTheDocument();
  });

  it('falls back to last year when ?year= is not one of the offered years', async () => {
    const asked: string[] = [];
    server.use(
      http.get('*/api/v1/tax', ({ request }) => { asked.push(`tax ${new URL(request.url).searchParams.get('year')}`); return HttpResponse.json(estimate()); }),
      http.get('*/api/v1/tax/2044', ({ request }) => { asked.push(`2044 ${new URL(request.url).searchParams.get('year')}`); return HttpResponse.json(view2044()); }),
    );
    await renderApp('/tax?tab=2044&year=2024');

    await screen.findByRole('region', { name: 'Studio' });
    expect(asked).not.toContain('2044 2024');
    expect(asked).not.toContain('tax 2024');
  });

  it('says so when the year has no rental income', async () => {
    server.use(
      http.get('*/api/v1/tax', () => HttpResponse.json(estimate())),
      http.get('*/api/v1/tax/2044', () => HttpResponse.json(view2044({ properties: [] }))),
    );
    await renderApp('/tax?tab=2044');

    expect(await screen.findByText('Aucun revenu foncier cette année-là')).toBeInTheDocument();
  });
});
