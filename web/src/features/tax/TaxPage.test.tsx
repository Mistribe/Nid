import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it, vi } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { pick } from '@/test/controls';
import { server } from '@/test/server';
import type { Rental2044, TaxEstimate } from './api';
import { jointAssessment, soloAssessment } from './assessments/testData';
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
    expect(within(table).getByRole('row', { name: /^Revenu fiscal de référence/ })).toHaveTextContent('66 407,00 €');
    expect(screen.getByText(/se compare à l’avis d’imposition de la même année de revenus/)).toBeInTheDocument();
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

  it('says where each person’s taxable salary comes from, and lists the payments behind it', async () => {
    const detail = (over: Partial<TaxEstimate['salaryDetail'][number]>): TaxEstimate['salaryDetail'][number] => ({
      person: ADULT, item: 'i1', name: 'Salaire Alice', on: '2026-01-31', note: 'Janvier', category: 'entered', amount: '4400.00', ...over,
    });
    const loaded = estimate();
    loaded.result.declarants[0]!.method = 'mixed';
    loaded.result.declarants[0]!.person = ADULT;
    loaded.salaryDetail = [
      detail({}),
      detail({ on: '2026-02-28', note: null, category: 'gross', amount: '4400.00' }),
      detail({ on: '2026-03-31', note: 'Mars', category: 'net', amount: '3900.00' }),
    ];
    server.use(http.get('*/api/v1/tax', () => HttpResponse.json(loaded)));
    const { user, container } = await renderApp('/tax');

    const people = await screen.findByRole('table', { name: 'Revenus par déclarant' });
    const row = within(people).getByRole('row', { name: /Salaires nets imposables/ });
    expect(row).toHaveTextContent('mixte');
    await user.click(within(row).getByRole('button', { name: /Voir les versements/ }));
    const payments = within(row).getAllByRole('listitem').map((li) => li.textContent?.replace(/\s/g, ' '));
    expect(payments).toEqual([
      expect.stringContaining('31 janvier 2026'), expect.stringContaining('28 février 2026'), expect.stringContaining('31 mars 2026'),
    ]);
    expect(payments[0]).toContain('Janvier');
    expect(payments[0]).toContain('4 400,00 €');
    expect(payments[0]).toContain('saisi');
    expect(payments[1]).toContain('estimé');
    expect(payments[2]).toContain('estimé');
    await expectNoAxeViolations(container);
  });

  it('lists each person’s own payments by id when two adults share a display name', async () => {
    const OTHER = '0192f0aa-0000-7000-8000-0000000000a2';
    const loaded = estimate();
    loaded.adults = [{ id: ADULT, displayName: 'Alice' }, { id: OTHER, displayName: 'Alice' }];
    const base = loaded.result.declarants[0]!;
    loaded.result.declarants = [{ ...base, person: ADULT, method: 'entered' }, { ...base, person: OTHER, method: 'entered' }];
    const pay = (person: string, item: string, name: string, amount: string): TaxEstimate['salaryDetail'][number] =>
      ({ person, item, name, on: '2026-01-31', note: null, category: 'entered', amount });
    loaded.salaryDetail = [pay(ADULT, 'i1', 'Salaire premier', '1111.00'), pay(OTHER, 'i2', 'Salaire second', '2222.00')];
    server.use(http.get('*/api/v1/tax', () => HttpResponse.json(loaded)));
    const { user } = await renderApp('/tax');

    const people = await screen.findByRole('table', { name: 'Revenus par déclarant' });
    const row = within(people).getByRole('row', { name: /Salaires nets imposables/ });
    const [first, second] = within(row).getAllByRole('cell');
    await user.click(within(first!).getByRole('button', { name: /Voir les versements/ }));
    await user.click(within(second!).getByRole('button', { name: /Voir les versements/ }));
    expect(first).toHaveTextContent('1 111,00 €');
    expect(first).not.toHaveTextContent('2 222,00 €');
    expect(second).toHaveTextContent('2 222,00 €');
    expect(second).not.toHaveTextContent('1 111,00 €');
  });

  it.each([['entered', 'saisi'], ['estimated', 'estimé']] as const)('marks a salary that is %s', async (method, pill) => {
    const loaded = estimate();
    loaded.result.declarants[0]!.method = method;
    server.use(http.get('*/api/v1/tax', () => HttpResponse.json(loaded)));
    await renderApp('/tax');

    const people = await screen.findByRole('table', { name: 'Revenus par déclarant' });
    expect(within(people).getByRole('row', { name: /Salaires nets imposables/ })).toHaveTextContent(pill);
  });

  it('shows a rate differing from the payslips as a percent, never as euros', async () => {
    const loaded = estimate();
    loaded.notices = [{ code: 'contribution_rate_differs', item: 'i1', name: 'Alice', amount: '22.85' }];
    server.use(http.get('*/api/v1/tax', () => HttpResponse.json(loaded)));
    await renderApp('/tax');

    expect(await screen.findByText('Alice : le taux de cotisations saisi s’écarte du taux constaté sur les bulletins (22,85 %).')).toBeInTheDocument();
  });

  it('offers the rate seen on the payslips and shows the status’s rate as the default', async () => {
    const loaded = estimate();
    loaded.observedRates = { [ADULT]: '22.85' };
    server.use(http.get('*/api/v1/tax', () => HttpResponse.json(loaded)));
    const { user } = await renderApp('/tax?tab=profile');

    const rate = await screen.findByRole('textbox', { name: 'Taux de cotisations (%) Alice' });
    expect(rate).toHaveAttribute('placeholder', '22 · celui du statut');
    expect(screen.getByText('Constaté sur vos bulletins : 22,85 %')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Utiliser' }));
    expect(rate).toHaveValue('22,85');
    await pick(user, 'Statut Alice', 'Salarié cadre');
    expect(rate).toHaveAttribute('placeholder', '25 · celui du statut');
    expect(screen.getByRole('textbox', { name: 'Frais réels Alice' })).toHaveAttribute('placeholder', 'Abattement de 10 %');
  });

  it('shows where the PER ceiling comes from', async () => {
    const per = (source: string) => ({ ceiling: '3980.00', source, expiring: '0.00', contributions: '0.00', deducted: '0.00', excess: '0.00', remaining: '3980.00' });
    const loaded = estimate();
    loaded.result = result({ declarants: [{ ...result().declarants[0]!, person: ADULT, per: per('estimated') }] });
    server.use(http.get('*/api/v1/tax', () => HttpResponse.json(loaded)));
    await renderApp('/tax?tab=profile');

    expect(await screen.findByRole('textbox', { name: 'Plafond épargne retraite (avis) Alice' })).toHaveAttribute('placeholder', expect.stringMatching(/^Estimé : 3\s980\s€$/));
    expect(screen.getByText('Estimé par Nid : saisissez celui de l’avis')).toBeInTheDocument();
  });

  it('says a ceiling was typed from the notice', async () => {
    const loaded = estimate();
    loaded.profile.perCeilings = { [ADULT]: '4637.00' };
    loaded.result = result({ declarants: [{ ...result().declarants[0]!, person: ADULT, per: { ceiling: '4637.00', source: 'notice', expiring: '0.00', contributions: '0.00', deducted: '0.00', excess: '0.00', remaining: '4637.00' } }] });
    server.use(http.get('*/api/v1/tax', () => HttpResponse.json(loaded)));
    await renderApp('/tax?tab=profile');

    expect(await screen.findByText('Saisi depuis l’avis')).toBeInTheDocument();
  });

  it('explains the rental regime chosen and lists the Pinel commitments', async () => {
    const loaded = estimate();
    loaded.rentalProperties = [{ id: 'p1', name: 'Studio', acquiredIn: 2021 }];
    server.use(http.get('*/api/v1/tax', () => HttpResponse.json(loaded)));
    const { user } = await renderApp('/tax?tab=profile');

    expect(await screen.findByText('Micro-foncier si les loyers du foyer ne dépassent pas 15 000 €, sinon réel.')).toBeInTheDocument();
    await user.click(screen.getByRole('radio', { name: 'réel' }));
    expect(screen.getByText('Loyers moins intérêts, assurance d’emprunt et charges.')).toBeInTheDocument();
    const pinel = screen.getByRole('table', { name: 'Réductions Pinel' });
    expect(within(pinel).getByRole('row', { name: /Studio 2021/ })).toHaveTextContent('Sans objet');
  });

  it('lists the profile’s sections and what to review in a profile carried over', async () => {
    server.use(http.get('*/api/v1/tax', () => HttpResponse.json({ ...estimate(), profileFrom: 2025 })));
    await renderApp('/tax?tab=profile');

    const nav = await screen.findByRole('navigation', { name: 'Rubriques du profil' });
    expect(within(nav).getAllByRole('link').map((l) => l.getAttribute('href'))).toEqual(['#foyer', '#activite', '#retraite', '#depenses', '#exoneres']);
    expect(within(nav).getByRole('link', { name: /Dépenses de l’année/ })).toHaveTextContent('à revoir');
    expect(within(nav).getByRole('link', { name: /Foyer/ })).not.toHaveTextContent('à revoir');
    expect(screen.getByText(/Repris du profil de 2025/)).toBeInTheDocument();
  });

  it('sends nothing and focuses the first invalid field on save', async () => {
    const sent: unknown[] = [];
    server.use(
      http.get('*/api/v1/tax', () => HttpResponse.json(estimate())),
      http.put('*/api/v1/tax/profile/:year', () => { sent.push(1); return HttpResponse.json(estimate()); }),
    );
    const { user } = await renderApp('/tax?tab=profile');

    await user.click(await screen.findByRole('button', { name: /Revenus exonérés \(heures supplémentaires/ }));
    await user.type(screen.getByLabelText('Revenus exonérés de Alice'), 'abc');
    await user.click(screen.getByRole('button', { name: 'Enregistrer le profil' }));

    expect(screen.getByLabelText('Revenus exonérés de Alice')).toHaveFocus();
    expect(sent).toEqual([]);
  });

  it('keeps the unused ceiling’s field while a saved value is cleared, and focuses it when asked for', async () => {
    const loaded = estimate();
    loaded.profile.perUnusedCeilings = { [ADULT]: '4000.00' };
    server.use(http.get('*/api/v1/tax', () => HttpResponse.json(loaded)));
    const { user } = await renderApp('/tax?tab=profile');

    const unused = await screen.findByRole('textbox', { name: 'Plafonds non utilisés Alice' });
    await user.clear(unused);
    expect(screen.getByRole('textbox', { name: 'Plafonds non utilisés Alice' })).toHaveFocus();
  });

  it('focuses the unused ceiling’s field once « + Saisir » is pressed', async () => {
    server.use(http.get('*/api/v1/tax', () => HttpResponse.json(estimate())));
    const { user } = await renderApp('/tax?tab=profile');

    await user.click(await screen.findByRole('button', { name: '+ Saisir les plafonds non utilisés' }));
    expect(screen.getByRole('textbox', { name: 'Plafonds non utilisés Alice' })).toHaveFocus();
  });

  it('leaves a refused save’s message behind on another year already loaded', async () => {
    server.use(
      http.get('*/api/v1/tax', ({ request }) => HttpResponse.json({ ...estimate(), year: Number(new URL(request.url).searchParams.get('year')) })),
      http.put('*/api/v1/tax/profile/:year', () => HttpResponse.json({ status: 400, title: 'Profil refusé' }, { status: 400, headers: { 'Content-Type': 'application/problem+json' } })),
    );
    const { user } = await renderApp('/tax?tab=profile');

    await screen.findByLabelText('Emploi à domicile');
    await user.click(screen.getByRole('radio', { name: '2025' }));
    await screen.findByLabelText('Emploi à domicile');
    await user.click(screen.getByRole('radio', { name: '2026' }));
    await user.click(await screen.findByRole('button', { name: 'Enregistrer le profil' }));
    expect(await screen.findByText(/Profil refusé/)).toBeInTheDocument();
    await user.click(screen.getByRole('radio', { name: '2025' }));

    expect(await screen.findByLabelText('Emploi à domicile')).toBeInTheDocument();
    expect(screen.queryByText(/Profil refusé/)).not.toBeInTheDocument();
  });

  it('folds the exempt incomes again on a year already loaded that has none', async () => {
    server.use(http.get('*/api/v1/tax', ({ request }) => {
      const year = Number(new URL(request.url).searchParams.get('year'));
      const loaded = { ...estimate(), year };
      if (year === 2026) loaded.profile = { ...loaded.profile, exemptIncomes: { [ADULT]: '1200.00' } };
      return HttpResponse.json(loaded);
    }));
    const { user } = await renderApp('/tax?tab=profile&year=2025');

    await screen.findByLabelText('Emploi à domicile');
    expect(screen.queryByLabelText('Revenus exonérés de Alice')).not.toBeInTheDocument();
    await user.click(screen.getByRole('radio', { name: '2026' }));
    expect(await screen.findByLabelText('Revenus exonérés de Alice')).toBeInTheDocument();
    await user.click(screen.getByRole('radio', { name: '2025' }));

    await screen.findByLabelText('Emploi à domicile');
    expect(screen.queryByLabelText('Revenus exonérés de Alice')).not.toBeInTheDocument();
  });

  describe('profile fields a notice supplies', () => {
    const carried = () => {
      const loaded = estimate();
      loaded.rentalProperties = [{ id: 'p1', name: 'Studio', acquiredIn: 2020 }];
      loaded.profile = { ...loaded.profile, perCeilings: { [ADULT]: '1111.00' }, perUnusedCeilings: { [ADULT]: '222.00' }, rentalDeficitCarried: '33.00' };
      loaded.carriedFrom = { year: 2025, perCeilings: { [ADULT]: '5843.13' }, perCeilingsExpiring: {}, perUnusedCeilings: { [ADULT]: '3500.00' }, rentalDeficitCarried: '800.00' };
      return loaded;
    };

    it('Locks_only_the_fields_the_notice_supplies', async () => {
      server.use(http.get('*/api/v1/tax', () => HttpResponse.json(carried())));
      await renderApp('/tax?tab=profile');

      const ceiling = await screen.findByRole('textbox', { name: 'Plafond épargne retraite (avis) Alice' });
      expect(ceiling).toBeDisabled();
      expect(ceiling).toHaveValue('5843.13');
      expect(screen.getAllByText('Avis 2025')).toHaveLength(2);
      expect(screen.getByRole('textbox', { name: 'dont perdu au 31 décembre Alice' })).toBeEnabled();
      expect(screen.getByRole('textbox', { name: 'Plafonds non utilisés Alice' })).toBeDisabled();
      expect(screen.getByRole('textbox', { name: 'Plafonds non utilisés Alice' })).toHaveValue('3500.00');
      expect(screen.getByLabelText('Déficits fonciers antérieurs à reporter')).toBeEnabled();
      expect(screen.getByLabelText('Emploi à domicile')).toBeEnabled();
    });

    it('Saving_the_profile_keeps_the_stored_values_behind_locked_fields', async () => {
      const sent: unknown[] = [];
      server.use(
        http.get('*/api/v1/tax', () => HttpResponse.json(carried())),
        http.put('*/api/v1/tax/profile/:year', async ({ request }) => { sent.push(await request.json()); return HttpResponse.json(carried()); }),
      );
      const { user } = await renderApp('/tax?tab=profile');

      await user.click(await screen.findByRole('button', { name: 'Enregistrer le profil' }));
      await screen.findByText('Profil enregistré.');
      expect(sent[0]).toMatchObject({ perCeilings: { [ADULT]: '1111.00' }, perUnusedCeilings: { [ADULT]: '222.00' }, rentalDeficitCarried: '33.00' });
    });

    it('The_notice_4BD_pre_fills_an_empty_field_and_is_saved_as_edited', async () => {
      const sent: unknown[] = [];
      const loaded = carried();
      loaded.profile = { ...loaded.profile, rentalDeficitCarried: '0.00' };
      server.use(
        http.get('*/api/v1/tax', () => HttpResponse.json(loaded)),
        http.put('*/api/v1/tax/profile/:year', async ({ request }) => { sent.push(await request.json()); return HttpResponse.json(loaded); }),
      );
      const { user } = await renderApp('/tax?tab=profile');

      const field = await screen.findByLabelText('Déficits fonciers antérieurs à reporter');
      expect(field).toBeEnabled();
      expect(field).toHaveValue('800.00');
      expect(screen.getByText('D’après l’avis 2025, à ajuster')).toBeInTheDocument();
      await user.clear(field);
      await user.type(field, '650');
      await user.click(screen.getByRole('button', { name: 'Enregistrer le profil' }));
      await screen.findByText('Profil enregistré.');
      expect(sent[0]).toMatchObject({ rentalDeficitCarried: '650' });
    });

    it('does not suggest the 4BD of a household without rental property', async () => {
      const sent: unknown[] = [];
      const loaded = { ...carried(), rentalProperties: [] };
      loaded.profile = { ...loaded.profile, rentalDeficitCarried: '0.00' };
      server.use(
        http.get('*/api/v1/tax', () => HttpResponse.json(loaded)),
        http.put('*/api/v1/tax/profile/:year', async ({ request }) => { sent.push(await request.json()); return HttpResponse.json(loaded); }),
      );
      const { user } = await renderApp('/tax?tab=profile');

      await user.click(await screen.findByRole('button', { name: 'Enregistrer le profil' }));
      await screen.findByText('Profil enregistré.');
      expect(screen.queryByLabelText('Déficits fonciers antérieurs à reporter')).not.toBeInTheDocument();
      expect(sent[0]).toMatchObject({ rentalDeficitCarried: '0.00' });
    });

    it('locks nothing without a carried notice', async () => {
      server.use(http.get('*/api/v1/tax', () => HttpResponse.json(estimate())));
      await renderApp('/tax?tab=profile');

      expect(await screen.findByRole('textbox', { name: 'Plafond épargne retraite (avis) Alice' })).toBeEnabled();
    });
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
    const { user, container } = await renderApp('/tax?tab=profile');

    const home = await screen.findByLabelText('Emploi à domicile');
    await user.clear(home);
    await user.type(home, '9000');
    await user.type(screen.getByRole('textbox', { name: 'Taux de cotisations (%) Alice' }), '23,5');
    // A fallback for a missing tax notice: folded until asked for.
    expect(screen.queryByRole('textbox', { name: 'Plafonds non utilisés Alice' })).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: '+ Saisir les plafonds non utilisés' }));
    await user.type(screen.getByRole('textbox', { name: 'Plafonds non utilisés Alice' }), '4000');
    expect(screen.queryByLabelText('Revenus exonérés de Alice')).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: /Revenus exonérés \(heures supplémentaires/ }));
    await user.type(screen.getByLabelText('Revenus exonérés de Alice'), '2400,50');
    expect(screen.getByText('4 modifications non enregistrées')).toBeInTheDocument();
    // Beside the tab list, outside the form it submits.
    expect(within(screen.getByRole('form', { name: 'Profil fiscal' })).queryByRole('button', { name: 'Enregistrer le profil' })).not.toBeInTheDocument();
    await expectNoAxeViolations(container);
    await user.click(screen.getByRole('button', { name: 'Enregistrer le profil' }));

    expect(await screen.findByText('Profil enregistré.')).toBeInTheDocument();
    expect(sent[0]).toMatchObject({ year: '2026', body: {
      homeEmployment: '9000', childrenUnder6: 2, dependents: null, statuses: { [ADULT]: 'non_executive' },
      contributionRates: { [ADULT]: '23.5' }, actualExpenses: {}, perUnusedCeilings: { [ADULT]: '4000' },
      exemptIncomes: { [ADULT]: '2400.50' },
    } });
  });

  it('keeps the profile draft across tabs and resets it on another year', async () => {
    server.use(http.get('*/api/v1/tax', ({ request }) => HttpResponse.json({ ...estimate(), year: Number(new URL(request.url).searchParams.get('year')) })));
    const { user } = await renderApp('/tax?tab=profile');

    const home = await screen.findByLabelText('Emploi à domicile');
    await user.clear(home);
    await user.type(home, '9000');
    await user.click(screen.getByRole('tab', { name: 'Détail du calcul' }));
    await user.click(screen.getByRole('tab', { name: 'Profil fiscal' }));

    expect(await screen.findByLabelText('Emploi à domicile')).toHaveValue('9000');
    expect(screen.getByText('1 modification non enregistrée')).toBeInTheDocument();
    await user.click(screen.getByRole('radio', { name: '2025' }));
    expect(await screen.findByLabelText('Emploi à domicile')).toHaveValue('0.00');
    expect(screen.queryByText(/non enregistrée/)).not.toBeInTheDocument();
  });

  it('keeps the draft when the server refuses the profile', async () => {
    server.use(
      http.get('*/api/v1/tax', () => HttpResponse.json(estimate())),
      http.put('*/api/v1/tax/profile/:year', () => HttpResponse.json({ status: 400, title: 'Profil refusé' }, { status: 400, headers: { 'Content-Type': 'application/problem+json' } })),
    );
    const { user } = await renderApp('/tax?tab=profile');

    const home = await screen.findByLabelText('Emploi à domicile');
    await user.clear(home);
    await user.type(home, '9000');
    await user.click(screen.getByRole('button', { name: 'Enregistrer le profil' }));

    expect(await screen.findByText(/Profil refusé/)).toBeInTheDocument();
    expect(screen.getByLabelText('Emploi à domicile')).toHaveValue('9000');
    expect(screen.getByText('1 modification non enregistrée')).toBeInTheDocument();
  });

  it('offers to pool the PER ceilings only to a couple', async () => {
    server.use(http.get('*/api/v1/tax', () => HttpResponse.json(estimate())));
    await renderApp('/tax?tab=profile');
    await screen.findByLabelText('Emploi à domicile');
    expect(screen.queryByRole('checkbox', { name: /Mutualiser/ })).not.toBeInTheDocument();
  });

  it('shows one column per declarant and the pooling of a couple', async () => {
    const couple = estimate();
    couple.adults = [...couple.adults, { id: 'b', displayName: 'Bob' }];
    server.use(http.get('*/api/v1/tax', () => HttpResponse.json(couple)));
    await renderApp('/tax?tab=profile');

    expect(await screen.findByRole('textbox', { name: 'Taux de cotisations (%) Bob' })).toBeInTheDocument();
    expect(screen.getByRole('checkbox', { name: /Mutualiser les plafonds des deux déclarants/ })).toBeInTheDocument();
  });

  it('marks every read of the profile stale once saved: the estimate, the 2044, the dashboard and the goals', async () => {
    server.use(
      http.get('*/api/v1/tax', () => HttpResponse.json(estimate())),
      http.put('*/api/v1/tax/profile/:year', () => HttpResponse.json(estimate())),
    );
    const { user, queryClient } = await renderApp('/tax?tab=profile');
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries');

    await user.click(await screen.findByRole('button', { name: 'Enregistrer le profil' }));

    expect(await screen.findByText('Profil enregistré.')).toBeInTheDocument();
    expect(invalidate.mock.calls.map(([filters]) => filters?.queryKey)).toEqual(expect.arrayContaining([
      ['get', '/api/v1/tax'], ['get', '/api/v1/tax/2044'], ['get', '/api/v1/dashboard'],
      ['get', '/api/v1/goals'], ['get', '/api/v1/goals/{id}'],
    ]));
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

  describe('past notices', () => {
    const notices = (...items: ReturnType<typeof jointAssessment>[]) => http.get('*/api/v1/tax/assessments', () => HttpResponse.json({ items }));
    const taxCalls = () => {
      const years: (string | null)[] = [];
      server.use(http.get('*/api/v1/tax', ({ request }) => { years.push(new URL(request.url).searchParams.get('year')); return HttpResponse.json(estimate()); }));
      return years;
    };

    it('offers the years that have a notice', async () => {
      server.use(notices(soloAssessment({ year: 2018 }), jointAssessment({ year: 2023 })), http.get('*/api/v1/tax', () => HttpResponse.json(estimate())));
      await renderApp('/tax');

      const group = await screen.findByRole('radiogroup', { name: 'Année des revenus' });
      await waitFor(() => expect(within(group).getAllByRole('radio').map((r) => r.textContent)).toEqual(['2018', '2023', '2025', '2026']));
    });

    it('shows a past year’s notices without calling the estimate', async () => {
      const years = taxCalls();
      server.use(notices(jointAssessment({ year: 2023 })));
      await renderApp('/tax?year=2023');

      expect(await screen.findByRole('region', { name: 'Avis 2023' })).toBeInTheDocument();
      expect(years).toEqual([]);
    });

    it('has an Avis tab beside the estimate of a year that has a notice', async () => {
      server.use(notices(jointAssessment({ year: 2025 })), http.get('*/api/v1/tax', () => HttpResponse.json(estimate())));
      const { user } = await renderApp('/tax?year=2025');

      await user.click(await screen.findByRole('tab', { name: 'Avis reçus' }));
      expect(await screen.findByRole('region', { name: 'Avis 2025' })).toBeInTheDocument();
    });

    it('Shows_the_gaps_above_their_notice', async () => {
      const loaded = estimate();
      const a = jointAssessment({ year: 2025 });
      loaded.gaps = {
        assessment: a.id,
        lines: [
          { code: 'balance', notice: '-312.00', nid: null, gap: null },
          { code: 'net_tax', notice: '2688.00', nid: '2500.00', gap: '188.00' },
        ],
        declarants: [{ person: ADULT, notice: '46800.00', nid: '46000.00', gap: '800.00' }],
      };
      // The names come from the estimate's adults, not from the persons list.
      server.use(notices(a), http.get('*/api/v1/tax', () => HttpResponse.json(loaded)), http.get('*/api/v1/core/persons', () => HttpResponse.json([])));
      const { user } = await renderApp('/tax?year=2025');

      await user.click(await screen.findByRole('tab', { name: 'Avis reçus' }));
      const table = await screen.findByRole('table', { name: 'Écarts entre l’avis et l’estimation' });
      const rows = within(table).getAllByRole('row').slice(1);
      expect(rows.map((r) => r.firstChild?.textContent)).toEqual(['Impôt net', 'Solde', 'Salaires nets — Alice']);
      expect(rows[0]).toHaveTextContent('188,00 €');
      expect(rows[1]).toHaveTextContent('—');
      expect(screen.getByText('Écart = avis − estimation de Nid')).toBeInTheDocument();
      expect(screen.getByText(/prélèvement forfaitaire de 12,8 %/)).toBeInTheDocument();
      expect(table.compareDocumentPosition(screen.getByRole('region', { name: 'Avis 2025' })) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    });

    it('No_gap_table_without_gaps', async () => {
      server.use(notices(jointAssessment({ year: 2025 })), http.get('*/api/v1/tax', () => HttpResponse.json(estimate())));
      const { user } = await renderApp('/tax?year=2025');

      await user.click(await screen.findByRole('tab', { name: 'Avis reçus' }));
      await screen.findByRole('region', { name: 'Avis 2025' });
      expect(screen.queryByRole('table', { name: 'Écarts entre l’avis et l’estimation' })).not.toBeInTheDocument();
    });

    it('falls back to Détail when the year switched to has no notice', async () => {
      server.use(notices(jointAssessment({ year: 2025 })), http.get('*/api/v1/tax', () => HttpResponse.json(estimate())));
      const { user } = await renderApp('/tax?year=2025&tab=assessment');

      await screen.findByRole('region', { name: 'Avis 2025' });
      await user.click(screen.getByRole('radio', { name: '2026' }));
      expect(await screen.findByRole('tab', { name: 'Détail du calcul', selected: true })).toBeInTheDocument();
      expect(screen.getByRole('table', { name: 'Calcul de l’impôt étape par étape' })).toBeInTheDocument();
    });

    it('has no Avis tab when the year has no notice', async () => {
      server.use(http.get('*/api/v1/tax', () => HttpResponse.json(estimate())));
      await renderApp('/tax');

      await screen.findByText('Net fiscal');
      expect(screen.queryByRole('tab', { name: 'Avis reçus' })).not.toBeInTheDocument();
    });

    it('does not offer a year without a readable notice', async () => {
      const years = taxCalls();
      await renderApp('/tax?year=2018');

      await screen.findByText('Net fiscal');
      expect(years).toEqual(['2026']);
    });

    it('shows the solo notices of « Moi »', async () => {
      server.use(
        http.get('*/api/v1/tax/assessments', ({ request }) => HttpResponse.json({ items: new URL(request.url).searchParams.get('scope') === 'me' ? [soloAssessment({ year: 2024 })] : [] })),
        http.get('*/api/v1/tax', () => HttpResponse.json(estimate())),
      );
      await renderApp('/tax?scope=me&year=2024');

      expect(await screen.findByRole('region', { name: 'Avis 2024' })).toBeInTheDocument();
    });

    it('adds a notice from the header', async () => {
      server.use(http.get('*/api/v1/tax', () => HttpResponse.json(estimate())));
      await renderApp('/tax');

      expect(await screen.findByRole('link', { name: 'Ajouter un avis' })).toHaveAttribute('href', '/tax/assessments/new');
    });
  });
});
