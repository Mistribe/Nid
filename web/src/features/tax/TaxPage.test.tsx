import { screen, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { TaxEstimate, TaxResult } from './api';

const ADULT = '0192f0aa-0000-7000-8000-0000000000a1';

const result = (over: Partial<TaxResult> = {}): TaxResult => ({
  year: 2026, rulesYear: 2025, parts: '3.0',
  declarants: [{ name: 'Alice', netSalary: '39000.00', netTaxableIncome: '40424.63', allowance: '4042.46', taxable: '36382.17', perDeducted: '0.00' }],
  rentalRegime: 'micro', rentalTaxable: '0.00', rentalDeficit: '0.00', netTaxableIncome: '64407.00', grossTax: '3256.77', familyCapApplied: false,
  decote: '9.31', pinelReduction: '0.00', donationReduction: '0.00', childcareCredit: '3000.00', homeEmploymentCredit: '0.00', nicheCapExcess: '0.00',
  incomeTax: '247.00', capitalFlatTax: '0.00', socialLevies: '0.00', totalTax: '247.00', marginalRate: '11', averageRate: '0.38',
  netBeforeTax: '69000.00', netAfterTax: '68753.00', netAfterTaxMonthly: '5729.42', warnings: ['rules_carried_forward'],
  withholding: {
    rate: '4.5', taxBase: '3247.00', income: '40424.63', declarants: [{ name: 'Alice', salaries: '40424.63', monthly: '151.59' }],
    rentalDeclared: '0.00', rentalMonthly: '0.00', annual: '3220.39', leftOut: '3000.00', balance: '-2973.39',
  },
  ...over,
});

const estimate = (): TaxEstimate => ({
  year: 2026, profileFrom: null, dependents: 2, rentalProperties: [],
  profile: { statuses: {}, dependents: null, childrenUnder6: 2, rentalRegime: null, pinel: [], childcareOutsideHome: '6000.00', homeEmployment: '0.00', donations: '0.00', donationsAid: '0.00', contributionRates: {}, actualExpenses: {} },
  inputs: {
    year: 2026, dependents: 2, childrenUnder6: 2, rentalGross: '0.00', rentalInterest: '0.00', rentalCharges: '0.00', rentalRegime: null,
    capitalIncome: '0.00', nonTaxableIncome: '0.00', pinelReduction: '0.00', childcareOutsideHome: '6000.00', homeEmployment: '0.00', donations: '0.00', donationsAid: '0.00',
    declarants: [{ name: 'Alice', salaryGross: '50000.00', status: 'non_executive', otherTaxable: '0.00', perContributions: '0.00' }],
  },
  result: result(),
  notices: [{ code: 'missing_gross', item: 'b', name: 'Prime', amount: '1000.00' }],
  adults: [{ id: ADULT, displayName: 'Alice' }],
});

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
    await user.click(screen.getByRole('button', { name: 'Enregistrer le profil' }));

    expect(await screen.findByText('Profil enregistré.')).toBeInTheDocument();
    expect(sent[0]).toMatchObject({ year: '2026', body: {
      homeEmployment: '9000', childrenUnder6: 2, dependents: null, statuses: { [ADULT]: 'non_executive' },
      contributionRates: { [ADULT]: '23.5' }, actualExpenses: {},
    } });
  });
});
