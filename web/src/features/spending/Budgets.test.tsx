import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
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
  beforeAll(async () => { await import('./SpendingPage'); }, 30_000);
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
    await expectNoAxeViolations(container);
  });

  it('leaves the budget out of Moi, and points to Foyer keeping the month', async () => {
    const asked = spendingServer(budgeted);
    const { user, router } = await renderApp('/spending?scope=me&month=2026-10');

    const table = await screen.findByRole('table', { name: 'Par catégorie' });
    expect(within(table).queryByRole('columnheader', { name: 'Budget' })).toBeNull();
    expect(within(table).queryByRole('button', { name: /Actions sur/ })).toBeNull();
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
    expect(screen.queryByText(/Le budget est celui du foyer/)).toBeNull();
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
