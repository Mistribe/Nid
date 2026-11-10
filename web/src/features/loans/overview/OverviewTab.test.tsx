import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { LoansOverview } from '@/api/loans';
import { CAR, household, LOAN, overview, overviewRow } from '../testData';
import { expectOnePrimary } from '@/test/headerActions';

const serve = (body: LoansOverview, seen: string[] = []) =>
  server.use(http.get('*/api/v1/realestate/loans/overview', ({ request }) => {
    seen.push(new URL(request.url).search);
    return HttpResponse.json(body);
  }));
const region = (name: string) => screen.findByRole('region', { name });

describe('Crédits › vue d’ensemble', () => {
  beforeAll(async () => { await import('../LoansPage'); await import('./OverviewTab'); }, 30_000);
  // The figures read « dans … » against the browser's today.
  beforeEach(() => { vi.useFakeTimers({ toFake: ['Date'] }); vi.setSystemTime(new Date(2026, 9, 21, 12)); });
  afterEach(() => { vi.useRealTimers(); });

  it('opens on the overview and shows the server figures', async () => {
    const seen: string[] = [];
    serve(household(), seen);
    const { container } = await renderApp('/loans');

    expect(screen.getByRole('tab', { name: 'Vue d’ensemble' })).toHaveAttribute('aria-selected', 'true');
    expect(await region('Capital restant dû')).toHaveTextContent('Capital restant dû199 564,23 €sur 212 000,00 € empruntés');
    const figures = (await screen.findByText('Mensualités', { selector: 'dt' })).closest('dl')!;
    expect(figures).toHaveTextContent('2 159,92 €par mois, assurances comprises');
    expect(figures).toHaveTextContent('72 157,31 €6 823,35 € déjà payés');
    expect(figures).toHaveTextContent('Sans crédit enoctobre 2045dans 18 ans et 11 mois');
    expect(seen).toEqual(['?scope=household']);
    expect(screen.getByRole('link', { name: 'Ajouter un crédit' })).toHaveAttribute('href', '/loans/new');
    await expectNoAxeViolations(container);
  });

  it('keeps the simple view to the figures, the curve and what comes; « Analyse » has the rest (issue #20)', async () => {
    const seen: string[] = [];
    serve(household(), seen);
    const { user, container } = await renderApp('/loans');

    expect(screen.getAllByRole('tab').map((tab) => tab.textContent).slice(0, 3)).toEqual(['Vue d’ensemble', 'Analyse', 'Liste']);
    await region('À venir');
    expect(screen.getByRole('region', { name: 'Capital restant dû jusqu’à la fin' })).toBeInTheDocument();
    for (const name of ['Avancement', 'Crédits en cours'])
      expect(screen.queryByRole('region', { name })).not.toBeInTheDocument();

    await user.click(screen.getByRole('tab', { name: 'Analyse' }));
    expect(await region('Avancement')).toHaveTextContent('12 435,77 € remboursés sur 212 000,00 € · 5,87 %');
    expect(await region('Crédits en cours')).toBeInTheDocument();
    expect(screen.queryByRole('region', { name: 'À venir' })).not.toBeInTheDocument();
    // Both tabs and the page read the one answer.
    expect(seen).toEqual(['?scope=household']);
    await expectNoAxeViolations(container);
  });

  it('leads with the hero: the balance as the page’s first figure', async () => {
    serve(household());
    await renderApp('/loans');

    const hero = await region('Capital restant dû');
    expect(within(hero).getByText('199 564,23 €')).toHaveClass('text-hero', 'tabular-nums');
    expect(hero.querySelector('svg[aria-hidden="true"] path[stroke]')).toHaveAttribute('stroke', 'var(--primary)');
    const figures = (await screen.findByText('Mensualités', { selector: 'dt' })).closest('dl')!;
    expect(figures).not.toHaveTextContent('Capital restant dû');
    expect(figures).not.toHaveTextContent('199 564,23 €');
  });

  it('lists the running loans with their figures, a total, and links to each detail', async () => {
    serve(household());
    await renderApp('/loans?tab=analysis');

    const table = await screen.findByRole('table', { name: 'Crédits en cours du foyer' });
    expect(within(table).getAllByRole('columnheader').map((h) => h.textContent))
      .toEqual(['Crédit', 'Propriétaires', 'Avancement', 'Restant dû', 'Mensualité', 'Taux', 'Fin']);
    const [home, car] = within(table).getAllByRole('row').slice(1);
    expect(within(home!).getByRole('link', { name: /Prêt maison/ })).toHaveAttribute('href', `/loans/${LOAN}`);
    expect(home).toHaveTextContent('3,22 %');
    expect(home).toHaveTextContent('193 564,23 €');
    expect(home).toHaveTextContent('1 159,92 €');
    expect(home).toHaveTextContent('3,5 %');
    expect(car).toHaveTextContent('Prêt auto');
    expect(car).toHaveTextContent('50 %');
    expect(await region('Crédits en cours')).toHaveTextContent('Total · 2 crédits199 564,23 € · 2 159,92 € / mois');
  });

  it('says what changes ahead, with the household payment after it', async () => {
    serve(household());
    await renderApp('/loans');

    const upcoming = await region('À venir');
    expect(upcoming).toHaveTextContent('Prochaine échéance');
    expect(upcoming).toHaveTextContent('2 159,92 €');
    // A grouped list, as the dashboard's agenda: one link a row, the event then its date.
    expect(within(upcoming).getByRole('link', { name: /^Dernière échéance de « Prêt auto » : la mensualité du foyer passe à 1\s159,92\s€ \S/ }))
      .toHaveAttribute('href', `/loans/${CAR}`);
    expect(within(upcoming).getByRole('link', { name: /^Prochaine échéance/ })).toHaveAttribute('href', expect.stringContaining('tab=list'));
    expect(upcoming.querySelectorAll('li[data-tile]').length).toBeGreaterThan(1);
    // Under the chart too, as a date and what ends.
    expect(await region('Capital restant dû jusqu’à la fin')).toHaveTextContent('janvier 2027 : fin de « Prêt auto »');
  });

  it('shows the curve as a table on demand: a month a row, a column per loan and the total', async () => {
    serve(household());
    const { user } = await renderApp('/loans');

    await user.click(await screen.findByRole('button', { name: 'Afficher en tableau' }));

    const table = screen.getByRole('table', { name: 'Capital restant dû, mois par mois' });
    expect(within(table).getAllByRole('columnheader').map((h) => h.textContent)).toEqual(['Mois', 'Prêt maison', 'Prêt auto', 'Total']);
    const rows = within(table).getAllByRole('row').slice(1);
    expect(rows).toHaveLength(4);
    expect(rows[0]).toHaveTextContent('193 564,23 €6 000,00 €199 564,23 €');
    expect(rows[3]).toHaveTextContent('188 000,00 €—188 000,00 €');
    await user.click(screen.getByRole('button', { name: 'Afficher le graphique' }));
    expect(screen.queryByRole('table', { name: 'Capital restant dû, mois par mois' })).not.toBeInTheDocument();
  });

  it('shows the household debt ratio against the 35 % ceiling, with the room left', async () => {
    serve(household({ debtRatio: { ratio: '0.2783', ceiling: '0.3500', payments: { amount: '2159.92', currency: 'EUR' },
      income: { amount: '7761.00', currency: 'EUR' }, room: { amount: '556.43', currency: 'EUR' }, unknown: null } }));
    await renderApp('/loans');

    const figures = (await screen.findByText('Taux d’endettement', { selector: 'dt' })).closest('div')!;
    expect(figures).toHaveTextContent('Taux d’endettement27,83 %seuil courant 35 %marge : 556,43 € / mois');
  });

  it('says the debt ratio is unknown without a declared income', async () => {
    serve(household({ debtRatio: { ratio: null, ceiling: '0.3500', payments: { amount: '2159.92', currency: 'EUR' }, income: null, room: null, unknown: 'no_income' } }));
    await renderApp('/loans');

    const figures = (await screen.findByText('Taux d’endettement', { selector: 'dt' })).closest('div')!;
    expect(figures).toHaveTextContent('Taux d’endettementInconnuaucun revenu déclaré');
  });

  it('says « Inconnu » and why when a rate is missing, and that the curve skips those months', async () => {
    serve(household({
      balance: null, monthlyPayment: null,
      months: [{ month: '2026-10', total: null, byLoan: [{ loanId: LOAN, balance: { amount: '193564.23', currency: 'EUR' } }] }],
      findings: [{ code: 'core.fx_rate_missing', message: 'No USD rate', reference: 'USD:2026-10-21' }],
    }));
    await renderApp('/loans');

    const hero = await region('Capital restant dû');
    expect(hero.querySelector('.text-hero')).toHaveTextContent(/^Inconnutaux USD manquant$/);
    expect(within(hero.querySelector<HTMLElement>('.text-hero')!).getByText('Inconnu').closest('.text-muted-foreground'))
      .toHaveClass('text-sm', 'font-normal');
    expect(await region('Capital restant dû jusqu’à la fin')).toHaveTextContent('Les mois dont un taux de change manque ne sont pas tracés.');
  });

  it('shows a loan in another currency in its currency, the converted amount on hover', async () => {
    serve(household({ loans: [overviewRow({ currency: 'USD', balance: '1000.00', balanceConverted: { amount: '900.00', currency: 'EUR' } })] }));
    const { user } = await renderApp('/loans?tab=analysis');

    const table = await screen.findByRole('table', { name: 'Crédits en cours du foyer' });
    const amount = within(table).getByText(/^1\s000,00\s\$US$/);
    await user.hover(amount);
    expect((await screen.findAllByText(/^900,00\s€ au taux du jour$/)).length).toBeGreaterThan(0);
  });

  it('invites to add a first loan when there is none', async () => {
    serve(overview());
    const { container } = await renderApp('/loans');

    expect(await screen.findByRole('heading', { name: 'Aucun crédit pour l’instant' })).toBeInTheDocument();
    expect(screen.queryByText('Capital restant dû')).not.toBeInTheDocument();
    expectOnePrimary(container);
  });

  it('says nothing is running when only ended loans exist, and opens the list with them', async () => {
    serve(overview({ endedCount: 1 }));
    const { user, router } = await renderApp('/loans');

    expect(await screen.findByRole('heading', { name: 'Aucun crédit en cours' })).toBeInTheDocument();
    await user.click(screen.getByRole('link', { name: 'Voir les crédits terminés' }));

    await waitFor(() => expect(router.state.location.search).toMatchObject({ tab: 'list', ended: 1 }));
    expect(screen.getByRole('switch', { name: 'Afficher les crédits terminés' })).toBeChecked();
  });

  it('opens the list from ?tab=list and back, the first tab staying out of the URL', async () => {
    serve(household());
    const { user, router } = await renderApp('/loans?tab=list');

    expect(await screen.findByRole('switch', { name: 'Afficher les crédits terminés' })).toBeInTheDocument();
    await user.click(screen.getByRole('tab', { name: 'Vue d’ensemble' }));

    await waitFor(() => expect(router.state.location.search).not.toHaveProperty('tab'));
    expect(await screen.findByText('Capital restant dû')).toBeInTheDocument();
  });

  it('passes the Moi scope to the API', async () => {
    const seen: string[] = [];
    serve(household(), seen);
    await renderApp('/loans?scope=me');

    await screen.findByText('Capital restant dû');
    expect(seen).toEqual(['?scope=me']);
  });
});
