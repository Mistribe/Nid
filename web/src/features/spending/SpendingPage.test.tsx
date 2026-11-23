import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { reduceMotion } from '@/test/motion';
import { month, spendingServer, year } from './testData';

describe('« Dépenses »', () => {
  beforeAll(async () => { await import('./SpendingPage'); await import('./SpendingOverview'); }, 30_000);
  beforeEach(() => { vi.useFakeTimers({ toFake: ['Date'] }); vi.setSystemTime(new Date(2026, 10, 19, 12)); });
  afterEach(() => vi.useRealTimers());

  it('shows the server figures as they come, children folded, « Non classées » last', async () => {
    const asked = spendingServer();
    const { container, user } = await renderApp('/spending');

    const table = await screen.findByRole('table', { name: 'Par catégorie' });
    expect(new URLSearchParams(asked[0]).toString()).toBe('scope=household&month=2026-11');
    const figures = screen.getByText('Dépensé').closest('div')!;
    expect(figures).toHaveTextContent(/1\s234,56\s€/);
    expect(screen.getByText('Mois habituel', { selector: 'dt' }).closest('div')).toHaveTextContent(/1\s100,00\s€/);
    const rows = within(table).getAllByRole('row').slice(1);
    expect(rows.map((r) => within(r).getAllByRole('cell')[0]!.textContent)).toEqual(['Courses', 'Loisirs', 'Non classées']);
    expect(rows[0]).toHaveTextContent(/640,10\s€/);
    expect(rows[1]).toHaveTextContent('—');
    expect(within(table).queryByText('Supermarché')).toBeNull();

    await user.click(within(rows[0]!).getByRole('button', { name: 'Afficher les sous-catégories de Courses' }));
    expect(within(table).getByText('Supermarché')).toBeInTheDocument();
    // A single subcategory unfolds too.
    await user.click(within(table).getByRole('button', { name: 'Afficher les sous-catégories de Loisirs' }));
    expect(within(table).getByText('Cinéma')).toBeInTheDocument();
    expect(screen.queryByRole('region', { name: 'Mois par mois' })).toBeNull();
    expect(screen.queryByRole('note')).toBeNull();
    await expectNoAxeViolations(container);
  });

  describe('what is left this month (#425)', () => {
    const figure = (label: string) => screen.getByText(label, { selector: 'dt' }).closest('div')!;

    it('shows Accueil’s figures as the server gives them, then how the month would end', async () => {
      spendingServer();
      const { container } = await renderApp('/spending');

      expect(await screen.findByText('Reste ce mois-ci', { selector: 'dt' })).toBeInTheDocument();
      expect(figure('Reste ce mois-ci')).toHaveTextContent(/800,00\s€/);
      expect(figure('Revenus')).toHaveTextContent(/3\s200,00\s€/);
      expect(figure('Engagements')).toHaveTextContent(/1\s500,00\s€/);
      expect(figure('Dépenses courantes')).toHaveTextContent(/900,00\s€/);
      expect(screen.getByText(/Si le mois finit comme d’habitude/)).toHaveTextContent(/il vous restera 600,00\s€/);
      await expectNoAxeViolations(container);
    });

    it('reads an overspend as « Dépassement de », and the month’s end as how far it would go over', async () => {
      spendingServer({ ...month, left: '-120.50', leftEstimate: '-300.00' });
      await renderApp('/spending');

      const over = await screen.findByText(/Dépassement de 120,50\s€/);
      expect(over).toHaveClass('text-status-critical');
      expect(screen.getByText(/Si le mois finit comme d’habitude/)).toHaveTextContent(/vous dépasserez de 300,00\s€/);
    });

    it('reads « inconnu » without a declared income, links to Revenus, and says nothing of the month’s end without an estimate', async () => {
      spendingServer({ ...month, income: null, left: null, leftEstimate: null });
      await renderApp('/spending');

      await screen.findByText('Reste ce mois-ci', { selector: 'dt' });
      expect(figure('Reste ce mois-ci')).toHaveTextContent('inconnu');
      expect(within(figure('Reste ce mois-ci')).getByRole('link', { name: 'Déclarer un revenu' })).toHaveAttribute('href', '/income');
      expect(figure('Revenus')).toHaveTextContent('inconnu');
      expect(screen.queryByText(/Si le mois finit/)).toBeNull();
    });

    it('is the period’s on the 12 months, with its savings rate (#428), which a month has not', async () => {
      spendingServer(year);
      const { container } = await renderApp('/spending?period=12m');
      await screen.findByRole('table', { name: 'Par catégorie' });
      expect(screen.queryByText('Reste ce mois-ci')).toBeNull();
      expect(figure('Reste sur la période')).toHaveTextContent(/8\s250,00\s€/);
      expect(figure('Revenus')).toHaveTextContent(/37\s200,00\s€/);
      expect(figure('Engagements')).toHaveTextContent(/18\s000,00\s€/);
      expect(figure('Dépenses courantes')).toHaveTextContent(/10\s950,00\s€/);
      expect(figure('Taux d’épargne')).toHaveTextContent(/22,2\s%/);
      expect(within(figure('Taux d’épargne')).getByRole('button', { name: /Taux d’épargne/ })).toBeInTheDocument();
      expect(screen.queryByText(/Si le mois finit/)).toBeNull();
      await expectNoAxeViolations(container);
    });

    it('reads the savings rate « inconnu » over 12 months without a declared income', async () => {
      spendingServer({ ...year, income: null, left: null, savingsRate: null, months: year.months.map((m) => ({ ...m, income: null, left: null })) });
      await renderApp('/spending?period=12m');
      await screen.findByText('Reste sur la période', { selector: 'dt' });
      expect(figure('Taux d’épargne')).toHaveTextContent('inconnu');
      expect(figure('Revenus')).toHaveTextContent('inconnu');
      expect(within(figure('Reste sur la période')).getByRole('link', { name: 'Déclarer un revenu' })).toHaveAttribute('href', '/income');
    });

    it('has no savings rate on a month', async () => {
      spendingServer();
      await renderApp('/spending');
      await screen.findByText('Reste ce mois-ci', { selector: 'dt' });
      expect(screen.queryByText('Taux d’épargne')).toBeNull();
    });
  });

  describe('the 12 months', () => {
    reduceMotion();

    it('draws each month’s income against its commitments and everyday spending, its table showing the server’s figures, and no usual month beside a period’s amounts', async () => {
      spendingServer(year);
      const { user, container } = await renderApp('/spending?period=12m');

      const chart = await screen.findByRole('region', { name: 'Mois par mois' });
      // #428: four series, named in the legend; a bar per month for each spending part.
      await waitFor(() => expect(container.querySelectorAll('.recharts-bar-rectangle')).toHaveLength(24));
      for (const name of ['Revenus', 'Engagements', 'Dépenses courantes', 'Reste']) expect(within(chart).getByText(name)).toBeInTheDocument();
      expect(screen.getByText('Mois habituel', { selector: 'dt' }).closest('div')).toHaveTextContent('par mois, sur les mois avant la période (12 au plus)');
      const categories = screen.getByRole('table', { name: 'Par catégorie' });
      expect(within(categories).queryByRole('columnheader', { name: 'Mois habituel' })).toBeNull();
      await expectNoAxeViolations(container);

      await user.click(within(chart).getByRole('button', { name: 'Afficher en tableau' }));
      const rows = within(within(chart).getByRole('table', { name: 'Mois par mois' })).getAllByRole('row').slice(1);
      expect(rows).toHaveLength(12);
      expect(within(chart).getAllByRole('columnheader').map((h) => h.textContent)).toEqual(['Mois', 'Revenus', 'Engagements', 'Dépenses courantes', 'Reste']);
      expect(rows[0]).toHaveTextContent(/décembre 2025\s*3\s000,00\s€\s*1\s500,00\s€\s*1\s600,00\s€\s*-100,00\s€/);
      expect(rows[11]).toHaveTextContent(/novembre 2026\s*3\s200,00\s€\s*1\s500,00\s€\s*850,00\s€\s*850,00\s€/);
    });
  });

  it('opens « Non classées » in the Opérations tab, over the period', async () => {
    spendingServer();
    const { user, router } = await renderApp('/spending?month=2026-02');
    await screen.findByRole('table', { name: 'Par catégorie' });

    const figure = screen.getByText('Non classées', { selector: 'dt' }).closest('div')!;
    expect(figure).toHaveTextContent(/300,00\s€/);
    await user.click(within(figure).getByRole('link', { name: '12 opérations à classer' }));
    // The answer's months (testData: 2026-11), as the server read them.
    await waitFor(() => expect(router.state.location.search).toEqual({ tab: 'operations', state: 'counted', from: '2026-11-01', to: '2026-11-30', uncategorised: true, direction: 'out' }));
    expect(screen.getByRole('tab', { name: /^Opérations/, selected: true })).toBeInTheDocument();
  });

  it('says nothing is left to classify when the period has no such line (#426)', async () => {
    spendingServer({ ...month, uncategorised: { amount: '0.00', average: null, count: 0 } });
    await renderApp('/spending');
    await screen.findByRole('table', { name: 'Par catégorie' });
    expect(screen.queryByRole('link', { name: /à classer/ })).toBeNull();
    expect(screen.getByRole('tab', { name: 'Opérations' })).toBeInTheDocument();
  });

  it('counts the current month’s lines to classify on the « Opérations » tab, asking the overview’s question once (#426)', async () => {
    const asked = spendingServer();
    await renderApp('/spending');
    expect(await screen.findByRole('tab', { name: 'Opérations12 opérations à classer' })).toBeInTheDocument();
    expect(asked).toEqual(['?scope=household&month=2026-11']);
  });

  it('keeps each tab’s filters to itself, Foyer / Moi carried over', async () => {
    server.use(
      http.get('*/api/v1/banking/operations', () => HttpResponse.json({ items: [], total: 0, totals: { out: '0.00', in: '0.00', net: '0.00', currency: 'EUR', incomplete: false },
        facets: { accounts: [], owners: [], categories: [] }, accountOwners: [] })),
      http.get('*/api/v1/banking/categories', () => HttpResponse.json([])),
      http.get('*/api/v1/banking/category-rules', () => HttpResponse.json([])),
    );
    const { user, router } = await renderApp('/spending?scope=me&tab=operations&category=c1&month=2026-10');
    expect(await screen.findByRole('heading', { level: 1, name: 'Dépenses' })).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: 'Opérations', selected: true })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Importer un relevé' })).toBeInTheDocument();
    expect(screen.queryByRole('group', { name: 'Mois' })).toBeNull();

    await user.click(screen.getByRole('tab', { name: 'Catégories' }));
    await waitFor(() => expect(router.state.location.search).toEqual({ scope: 'me', tab: 'categories' }));
    await user.click(screen.getByRole('tab', { name: 'Vue d’ensemble' }));
    await waitFor(() => expect(router.state.location.search).toEqual({ scope: 'me' }));
    expect(await screen.findByRole('group', { name: 'Mois' })).toBeInTheDocument();
  });

  it('sends the old addresses to their tab, filters kept', async () => {
    const { router } = await renderApp('/operations?state=unlinked&category=c1&scope=me');
    await waitFor(() => expect(router.state.location.pathname).toBe('/spending'));
    expect(router.state.location.search).toEqual({ scope: 'me', tab: 'operations', state: 'unlinked', category: ['c1'] });
  });

  it('reads the scope from the address', async () => {
    const asked = spendingServer();
    await renderApp('/spending?scope=me');
    await screen.findByRole('table', { name: 'Par catégorie' });
    expect(asked[0]).toContain('scope=me');
  });

  it('counts the ticked accounts only, leaving out the household’s figures, and keeps them in the address', async () => {
    const accounts = [{ id: 'a1', name: 'Compte joint' }, { id: 'a2', name: 'Livret' }];
    const asked = spendingServer({ ...month, accounts });
    const { user, router } = await renderApp('/spending');
    await screen.findByRole('table', { name: 'Par catégorie' });
    expect(screen.getByText('Reste ce mois-ci')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Comptes' }));
    await user.click(await screen.findByRole('menuitemcheckbox', { name: 'Livret' }));
    await waitFor(() => expect(new URLSearchParams(asked.at(-1)).getAll('account')).toEqual(['a2']));
    await user.click(screen.getByRole('menuitemcheckbox', { name: 'Compte joint' }));
    await waitFor(() => expect(new URLSearchParams(asked.at(-1)).getAll('account')).toEqual(['a2', 'a1']));
    expect(router.state.location.search).toMatchObject({ account: ['a2', 'a1'] });
    await user.keyboard('{Escape}');
    expect(screen.getByRole('button', { name: 'Comptes (2)' })).toBeInTheDocument();
    expect(screen.queryByText('Reste ce mois-ci')).toBeNull();

    await user.click(screen.getByRole('button', { name: 'Tous les comptes' }));
    await waitFor(() => expect(router.state.location.search).not.toHaveProperty('account'));
    expect(await screen.findByText('Reste ce mois-ci')).toBeInTheDocument();
  });

  it('steps through the months and the 12 months up to one', async () => {
    const asked = spendingServer();
    const { user, router } = await renderApp('/spending');
    await screen.findByRole('table', { name: 'Par catégorie' });

    const next = screen.getByRole('button', { name: 'Mois suivant' });
    expect(next).toHaveAttribute('aria-disabled', 'true');
    await user.click(screen.getByRole('button', { name: 'Mois précédent' }));
    await waitFor(() => expect(asked.at(-1)).toContain('month=2026-10'));
    expect(router.state.location.search).toMatchObject({ month: '2026-10' });
    expect(screen.getByRole('group', { name: 'Mois' })).toHaveTextContent('octobre 2026');

    await user.click(screen.getByRole('radio', { name: '12 derniers mois' }));
    await waitFor(() => expect(new URLSearchParams(asked.at(-1)).toString()).toBe('scope=household&from=2025-11&to=2026-10'));
    expect(router.state.location.search).toMatchObject({ month: '2026-10', period: '12m' });
  });

  it('marks a total left incomplete by a missing rate', async () => {
    spendingServer({ ...month, incomplete: true });
    await renderApp('/spending');
    expect(await screen.findByRole('note')).toHaveTextContent('Certaines opérations en devise n’ont pas de taux : total incomplet.');
  });

  it('invites to turn bank sync on, then to link an account', async () => {
    await renderApp('/spending');
    expect(await screen.findByRole('heading', { name: 'La banque n’est pas activée' })).toBeInTheDocument();

    server.use(
      http.get('*/api/v1/banking/status', () => HttpResponse.json({ enabled: true, configured: true })),
      http.get('*/api/v1/banking/accounts', () => HttpResponse.json([])),
    );
    await renderApp('/spending');
    expect(await screen.findByRole('heading', { name: 'Aucun compte bancaire relié' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Relier une banque' })).toHaveAttribute('href', '/settings/banks');
  });

  it('points to the Opérations tab while nothing is categorised, the unclassified lines still listed', async () => {
    spendingServer({ ...month, categories: [] });
    const { container } = await renderApp('/spending');
    expect(await screen.findByRole('heading', { name: 'Aucune dépense classée sur cette période' })).toBeInTheDocument();
    const rows = within(screen.getByRole('table', { name: 'Par catégorie' })).getAllByRole('row').slice(1);
    expect(rows).toHaveLength(1);
    expect(rows[0]).toHaveTextContent('Non classées');
    await expectNoAxeViolations(container);
  });

  it('points Foyer to Moi while it has nothing classified, keeping the month and period', async () => {
    const asked = spendingServer({ ...month, categories: [] });
    const { user, router } = await renderApp('/spending?month=2026-10&period=12m');
    expect(await screen.findByText('Les comptes que vous êtes seul à lire sont comptés dans la vue Moi.')).toBeInTheDocument();

    await user.click(screen.getByRole('link', { name: 'Voir la vue Moi' }));
    await waitFor(() => expect(asked.at(-1)).toContain('scope=me'));
    expect(router.state.location.search).toMatchObject({ scope: 'me', month: '2026-10', period: '12m' });
    expect(screen.queryByText('Les comptes que vous êtes seul à lire sont comptés dans la vue Moi.')).toBeNull();
  });

  it('renames and merges a category from its row', async () => {
    spendingServer();
    const [courses, loisirs] = [month.categories[0]!, month.categories[1]!];
    const cat = (id: string, name: string) => ({ id, parentId: null, key: null, name, kind: 'expense', archived: false, mergedIntoId: null, defaultName: null, lines: 1 });
    const writes: [string, string, unknown][] = [];
    server.use(
      http.get('*/api/v1/banking/categories', () => HttpResponse.json([cat(courses.id, 'Courses'), cat(loisirs.id, 'Loisirs')])),
      http.patch('*/api/v1/banking/categories/:id', async ({ request, params }) => {
        writes.push(['PATCH', String(params.id), await request.json()]);
        return HttpResponse.json(cat(String(params.id), 'Sorties'));
      }),
      http.post('*/api/v1/banking/categories/:id/merge', async ({ request, params }) => {
        writes.push(['MERGE', String(params.id), await request.json()]);
        return HttpResponse.json(cat(courses.id, 'Courses'));
      }),
    );
    const { user } = await renderApp('/spending?scope=me');
    const table = await screen.findByRole('table', { name: 'Par catégorie' });

    await user.click(await within(table).findByRole('button', { name: 'Actions sur Loisirs' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Renommer' }));
    const rename = await screen.findByRole('dialog', { name: 'Renommer Loisirs' });
    const field = within(rename).getByRole('textbox', { name: 'Nom' });
    await user.clear(field);
    await user.type(field, 'Sorties');
    await user.click(within(rename).getByRole('button', { name: 'Enregistrer' }));
    await waitFor(() => expect(writes).toEqual([['PATCH', loisirs.id, { name: 'Sorties' }]]));

    await user.click(within(table).getByRole('button', { name: 'Actions sur Loisirs' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Fusionner…' }));
    const merge = await screen.findByRole('dialog', { name: 'Fusionner Loisirs' });
    await user.click(within(merge).getByRole('combobox', { name: 'Fusionner dans' }));
    await user.click(within(await screen.findByRole('listbox')).getByRole('option', { name: 'Courses' }));
    await user.click(within(merge).getByRole('button', { name: 'Fusionner' }));
    await user.click(await screen.findByRole('button', { name: 'Confirmer' }));
    await waitFor(() => expect(writes[1]).toEqual(['MERGE', loisirs.id, { into: courses.id }]));
  });
});
