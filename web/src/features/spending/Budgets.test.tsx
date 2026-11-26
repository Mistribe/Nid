import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { pick } from '@/test/controls';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { Spending } from './api';
import { budgeted, budgetServer, spendingServer, year } from './testData';

const COURSES = budgeted.categories[0]!.id;
const LOISIRS = budgeted.categories[1]!.id;

const rowOf = (table: HTMLElement, name: string) => within(table).getAllByRole('row').find((r) => within(r).queryAllByRole('cell')[0]?.textContent === name)!;

async function openBudget(user: Awaited<ReturnType<typeof renderApp>>['user'], name: string) {
  const table = await screen.findByRole('table', { name: 'Par catégorie' });
  await user.click(within(rowOf(table, name)).getByRole('button', { name: `Actions sur ${name}` }));
  await user.click(await screen.findByRole('menuitem', { name: 'Modifier le budget' }));
  return screen.findByRole('dialog', { name: `Budget : ${name}` });
}

describe('« Dépenses » budgets', () => {
  beforeAll(async () => { await import('./SpendingPage'); await import('./SpendingOverview'); }, 30_000);
  beforeEach(() => { vi.useFakeTimers({ toFake: ['Date'] }); vi.setSystemTime(new Date(2026, 10, 19, 12)); });
  afterEach(() => vi.useRealTimers());

  it('shows each budget, what is left and the pace as the server gives them, in Foyer', async () => {
    spendingServer(budgeted);
    const { container } = await renderApp('/spending');

    const table = await screen.findByRole('table', { name: 'Par catégorie' });
    expect(within(table).getAllByRole('columnheader').map((h) => h.textContent)).toEqual(
      ['Catégorie', 'Montant', 'Mois habituel', 'Budget', 'Reste', 'Rythme', 'Répartition', 'Actions']);
    const courses = within(rowOf(table, 'Courses')).getAllByRole('cell');
    expect(courses[3]).toHaveTextContent(/^600,00\s€annuel$/);
    expect(courses[4]).toHaveTextContent(/^Dépassé de 40,10\s€$/);
    expect(courses[5]).toHaveTextContent('Dépassé');
    const loisirs = within(rowOf(table, 'Loisirs')).getAllByRole('cell');
    expect(loisirs[3]).toHaveTextContent(/^300,00\s€d’après vos engagements$/);
    expect(within(loisirs[3]!).getByRole('button', { name: 'À propos : Budget de Loisirs' })).toBeInTheDocument();
    expect(loisirs[4]).toHaveTextContent(/^5,54\s€$/);
    expect(loisirs[5]).toHaveTextContent('En avance');
    expect(within(loisirs[5]!).getByRole('button', { name: 'À propos : En avance' })).toBeInTheDocument();
    // « Non classées » has no budget to set.
    expect(within(rowOf(table, 'Non classées')).queryByRole('button', { name: /Actions sur/ })).toBeNull();
    expect(screen.queryByText(/Le budget est celui du foyer/)).toBeNull();

    // The phone list: the budget's facts on one line; over budget says only « Dépassé de », without « Reste ».
    const items = within(screen.getByRole('list', { name: 'Par catégorie' })).getAllByRole('listitem');
    const line = (i: number) => within(items[i]!).getByText(/^Budget/, { selector: 'p' });
    expect(line(0)).toHaveTextContent(/^Budget 600,00\s€ · annuel · Dépassé de 40,10\s€ Dépassé$/);
    expect(line(1)).toHaveTextContent(/^Budget 300,00\s€ · d’après vos engagements · Reste 5,54\s€ En avance$/);
    await expectNoAxeViolations(container);
  });

  it('leaves the budget out of Moi, and points to Foyer keeping the month', async () => {
    const asked = spendingServer(budgeted);
    const { user, router } = await renderApp('/spending?scope=me&month=2026-10');

    const table = await screen.findByRole('table', { name: 'Par catégorie' });
    expect(within(table).queryByRole('columnheader', { name: 'Budget' })).toBeNull();
    expect(within(table).queryByRole('button', { name: /Actions sur/ })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Ajouter un budget' })).toBeNull();
    expect(screen.getByText(/Le budget est celui du foyer : passez en vue Foyer pour le suivre\./)).toBeInTheDocument();

    await user.click(screen.getByRole('link', { name: 'Voir la vue Foyer' }));
    await waitFor(() => expect(asked.at(-1)).toContain('scope=household'));
    expect(router.state.location.search).toEqual({ month: '2026-10' });
  });

  it('leaves the budget out of the 12 months', async () => {
    spendingServer(year);
    await renderApp('/spending?period=12m');
    const table = await screen.findByRole('table', { name: 'Par catégorie' });
    expect(within(table).queryByRole('columnheader', { name: 'Budget' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Ajouter un budget' })).toBeNull();
    expect(screen.queryByText(/Le budget est celui du foyer/)).toBeNull();
  });

  it('shows the committed part of a typed budget, as the server gives it', async () => {
    const committed: Spending = { ...budgeted, categories: [{ ...budgeted.categories[0]!, budget: { ...budgeted.categories[0]!.budget!, period: 'month', committed: '120.00' } }, budgeted.categories[1]!] };
    spendingServer(committed);
    await renderApp('/spending');

    const cells = within(rowOf(await screen.findByRole('table', { name: 'Par catégorie' }), 'Courses')).getAllByRole('cell');
    expect(cells[3]).toHaveTextContent(/^600,00\s€dont 120,00\s€ engagés$/);
    expect(within(cells[3]!).getByRole('button', { name: /^À propos : dont 120,00\s€ engagés$/ })).toBeInTheDocument();
    // A budget that follows its commitments says so already: no « dont ».
    expect(within(rowOf(await screen.findByRole('table', { name: 'Par catégorie' }), 'Loisirs')).queryByText(/dont/)).toBeNull();
  });

  it('says a budget from commitments adds the usual month of the rest, and how much is committed (#722)', async () => {
    const usual: Spending = { ...budgeted, categories: [budgeted.categories[0]!, { ...budgeted.categories[1]!, budget: { ...budgeted.categories[1]!.budget!, committed: '250.00' } }] };
    spendingServer(usual);
    await renderApp('/spending');

    const cells = within(rowOf(await screen.findByRole('table', { name: 'Par catégorie' }), 'Loisirs')).getAllByRole('cell');
    expect(cells[3]).toHaveTextContent(/^300,00\s€engagements \+ mois habituel\s*dont 250,00\s€ engagés$/);
  });

  it('says when a commitment had no rate, apart from the lines', async () => {
    spendingServer({ ...budgeted, budgetIncomplete: true });
    await renderApp('/spending');

    expect(await screen.findByText('Une échéance en devise n’a pas de taux : le budget d’après vos engagements est incomplet.')).toBeInTheDocument();
    expect(screen.queryByText(/total incomplet/)).toBeNull();
  });

  it('offers no budget on a child of a typed budget', async () => {
    spendingServer(budgeted);
    const { user } = await renderApp('/spending');

    const table = await screen.findByRole('table', { name: 'Par catégorie' });
    await user.click(within(table).getByRole('button', { name: 'Afficher les sous-catégories de Courses' }));
    expect(within(rowOf(table, 'Supermarché')).queryByRole('button', { name: /Actions sur/ })).toBeNull();
    // Loisirs follows its commitments: Cinéma can still take one (the server refuses a pair).
    await user.click(within(table).getByRole('button', { name: 'Afficher les sous-catégories de Loisirs' }));
    expect(within(rowOf(table, 'Cinéma')).getByRole('button', { name: 'Actions sur Cinéma' })).toBeInTheDocument();
  });

  it('adds a budget to a category with no row, picked among the active expense ones whose branch holds no budget', async () => {
    spendingServer(budgeted);
    const writes = budgetServer([
      { categoryId: COURSES, categoryName: 'Courses', amount: '7200.00', period: 'year' },
      { categoryId: 'c8', categoryName: 'Carburant', amount: '100.00', period: 'month' },
      { categoryId: 'c12', categoryName: 'Camping', amount: '50.00', period: 'month' },
    ]);
    server.use(http.get('*/api/v1/banking/categories', () => HttpResponse.json([
      { id: 'c1', name: 'Logement', kind: 'expense', archived: false },
      { id: 'c2', parentId: 'c1', name: 'Travaux', kind: 'expense', archived: false },
      { id: 'c3', parentId: 'c1', name: 'Ancienne', kind: 'expense', archived: true },
      { id: 'c4', name: 'Salaires', kind: 'income', archived: false },
      // A budgeted parent takes its children.
      { id: COURSES, name: 'Courses', kind: 'expense', archived: false },
      { id: 'c6', parentId: COURSES, name: 'Supermarché', kind: 'expense', archived: false },
      // A budgeted child takes its parent, not its siblings.
      { id: 'c7', name: 'Transport', kind: 'expense', archived: false },
      { id: 'c8', parentId: 'c7', name: 'Carburant', kind: 'expense', archived: false },
      { id: 'c9', parentId: 'c7', name: 'Parking', kind: 'expense', archived: false },
      // An archived category's budget takes nothing.
      { id: 'c11', name: 'Vacances', kind: 'expense', archived: false },
      { id: 'c12', parentId: 'c11', name: 'Camping', kind: 'expense', archived: true },
    ])));
    const { user } = await renderApp('/spending');

    await user.click(await screen.findByRole('button', { name: 'Ajouter un budget' }));
    const dialog = await screen.findByRole('dialog', { name: 'Ajouter un budget' });
    await user.click(await within(dialog).findByRole('combobox', { name: 'Catégorie' }));
    const options = within(await screen.findByRole('listbox')).getAllByRole('option').map((o) => o.textContent);
    expect(options).toEqual(['Logement', 'Logement › Travaux', 'Transport › Parking', 'Vacances']);
    await user.keyboard('{Escape}');
    await pick(user, 'Catégorie', 'Logement › Travaux');
    await user.type(within(dialog).getByRole('textbox', { name: 'Montant' }), '50');
    await user.click(within(dialog).getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(writes).toEqual([['PUT', 'c2', { amount: '50', period: 'month' }]]));
    expect(await screen.findByText('Budget enregistré.')).toBeInTheDocument();
  });

  it('changes a budget: the amount as typed, the period chosen', async () => {
    spendingServer(budgeted);
    const writes = budgetServer();
    const { user } = await renderApp('/spending');

    const dialog = await openBudget(user, 'Courses');
    const amount = await within(dialog).findByRole('textbox', { name: 'Montant' });
    expect(amount).toHaveValue('7200,00');
    expect(within(dialog).getByRole('radio', { name: 'Annuel' })).toBeChecked();
    // Nothing committed in Courses: nothing to follow.
    expect(within(dialog).queryByRole('button', { name: 'Suivre mes engagements' })).toBeNull();
    await expectNoAxeViolations(dialog);

    await user.clear(amount);
    await user.type(amount, '6 000,5');
    await user.click(within(dialog).getByRole('radio', { name: 'Mensuel' }));
    await user.click(within(dialog).getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(writes).toEqual([['PUT', COURSES, { amount: '6000.5', period: 'month' }]]));
    expect(await screen.findByText('Budget enregistré.')).toBeInTheDocument();
  });

  it('follows the commitments: the amount cleared', async () => {
    spendingServer(budgeted);
    const writes = budgetServer();
    const { user } = await renderApp('/spending');

    const dialog = await openBudget(user, 'Loisirs');
    // No budget yet on Loisirs: nothing to delete.
    expect(within(dialog).queryByRole('button', { name: 'Supprimer le budget' })).toBeNull();
    await user.click(await within(dialog).findByRole('button', { name: 'Suivre mes engagements' }));

    await waitFor(() => expect(writes).toEqual([['PUT', LOISIRS, { amount: null, period: 'month' }]]));
    expect(await screen.findByText('Le budget suit vos engagements.')).toBeInTheDocument();
  });

  it('deletes a budget after a confirmation', async () => {
    spendingServer(budgeted);
    const writes = budgetServer();
    const { user } = await renderApp('/spending');

    const dialog = await openBudget(user, 'Courses');
    await user.click(await within(dialog).findByRole('button', { name: 'Supprimer le budget' }));
    expect(await screen.findByText('Le budget de Courses sera supprimé.')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Confirmer' }));

    await waitFor(() => expect(writes).toEqual([['DELETE', COURSES, null]]));
    expect(await screen.findByText('Budget supprimé.')).toBeInTheDocument();
  });

  it('shows a refused amount under its field, and a refused branch above the form', async () => {
    spendingServer(budgeted);
    budgetServer();
    server.use(http.put('*/api/v1/banking/budgets/:id', () => HttpResponse.json({
      status: 422, detail: 'Invalid.', errors: [{ detail: 'Bad amount.', pointer: '#/amount', code: 'banking.budget_amount' }],
    }, { status: 422 })));
    const { user } = await renderApp('/spending');

    const dialog = await openBudget(user, 'Courses');
    const amount = await within(dialog).findByRole('textbox', { name: 'Montant' });
    await user.clear(amount);
    await user.type(amount, '-5');
    await user.click(within(dialog).getByRole('button', { name: 'Enregistrer' }));
    await waitFor(() => expect(amount).toHaveAccessibleDescription(/Un budget est un montant de zéro ou plus\./));

    server.use(http.put('*/api/v1/banking/budgets/:id', () => HttpResponse.json({
      status: 422, detail: 'Branch.', code: 'banking.budget_branch',
    }, { status: 422 })));
    await user.click(within(dialog).getByRole('button', { name: 'Enregistrer' }));
    expect(await within(dialog).findByText('Un budget existe déjà sur la catégorie parente ou une sous-catégorie.')).toBeInTheDocument();
  });
});
