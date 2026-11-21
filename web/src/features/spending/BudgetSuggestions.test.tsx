import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { BudgetSuggestion } from './api';
import { budgetServer, month, spendingServer } from './testData';

/** Invented suggestions of the demo household: two ticked, one following its commitments, one left out. */
const suggestions: BudgetSuggestion[] = [
  { categoryId: 'c1', name: 'Alimentation', average: '121.00', proposed: '130.00', committed: false, preselected: true },
  { categoryId: 'c2', name: 'Loisirs', average: '9.99', proposed: '10.00', committed: true, preselected: true },
  { categoryId: 'c3', name: 'Transport', average: '8.00', proposed: '10.00', committed: false, preselected: false },
];

function suggestionServer(list = suggestions) {
  const posted: unknown[] = [];
  server.use(
    http.get('*/api/v1/banking/budgets/suggestions', () => HttpResponse.json(list)),
    http.post('*/api/v1/banking/budgets', async ({ request }) => { posted.push(await request.json()); return HttpResponse.json([], { status: 201 }); }),
  );
  return posted;
}

describe('« Proposer des budgets »', () => {
  beforeAll(async () => { await import('./SpendingPage'); await import('./SpendingOverview'); await import('./BudgetSuggestions'); }, 30_000);
  beforeEach(() => { vi.useFakeTimers({ toFake: ['Date'] }); vi.setSystemTime(new Date(2026, 10, 19, 12)); });
  afterEach(() => vi.useRealTimers());

  it('is offered only to a household with no budget, in Foyer', async () => {
    spendingServer(month);
    budgetServer();
    const { unmount } = await renderApp('/spending');
    expect(await screen.findByRole('button', { name: 'Ajouter un budget' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Proposer des budgets' })).toBeNull();
    unmount();

    budgetServer([]);
    const { unmount: again } = await renderApp('/spending');
    expect(await screen.findByRole('button', { name: 'Proposer des budgets' })).toBeInTheDocument();
    again();

    await renderApp('/spending?scope=me');
    await screen.findByRole('table', { name: 'Par catégorie' });
    expect(screen.queryByRole('button', { name: 'Proposer des budgets' })).toBeNull();
  });

  it('lists the server’s suggestions and creates the ticked ones in one call', async () => {
    spendingServer(month);
    const posted = suggestionServer();
    const { user } = await renderApp('/spending');

    await user.click(await screen.findByRole('button', { name: 'Proposer des budgets' }));
    const dialog = await screen.findByRole('dialog', { name: 'Budgets proposés' });
    const items = within(await within(dialog).findByRole('list', { name: 'Budgets proposés' })).getAllByRole('listitem');
    expect(items.map((i) => within(i).getByText(/^Mois habituel/).textContent.replace(/\s/g, ' ')))
      .toEqual(['Mois habituel 121,00 €', 'Mois habituel 9,99 €', 'Mois habituel 8,00 €']);
    // Ticked as the server says; the committed one follows the commitments, with no amount.
    expect(within(dialog).getByRole('checkbox', { name: 'Alimentation' })).toBeChecked();
    expect(within(dialog).getByRole('checkbox', { name: 'Loisirs' })).toBeChecked();
    expect(within(dialog).getByRole('checkbox', { name: 'Transport' })).not.toBeChecked();
    expect(within(items[1]!).getByText('Suivre mes engagements')).toBeInTheDocument();
    expect(within(items[1]!).queryByRole('textbox')).toBeNull();
    expect(within(dialog).getByRole('textbox', { name: 'Budget mensuel de Alimentation' })).toHaveValue('130,00');
    expect(within(dialog).getByRole('button', { name: 'Créer 2 budgets' })).toBeEnabled();
    await expectNoAxeViolations(dialog);

    // Edit an amount, tick Transport, untick Loisirs.
    const food = within(dialog).getByRole('textbox', { name: 'Budget mensuel de Alimentation' });
    await user.clear(food);
    await user.type(food, '150');
    await user.click(within(dialog).getByRole('checkbox', { name: 'Transport' }));
    await user.click(within(dialog).getByRole('checkbox', { name: 'Loisirs' }));
    await user.click(within(dialog).getByRole('button', { name: 'Créer 2 budgets' }));

    await waitFor(() => expect(posted).toEqual([{ budgets: [
      { categoryId: 'c1', amount: '150', source: 'typed', period: 'month' },
      { categoryId: 'c3', amount: '10.00', source: 'typed', period: 'month' },
    ] }]));
    expect(await screen.findByText('2 budgets créés.')).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
  });

  it('sends a committed one without an amount, and shows a refused amount on its row', async () => {
    spendingServer(month);
    const posted = suggestionServer();
    server.use(http.post('*/api/v1/banking/budgets', async ({ request }) => {
      posted.push(await request.json());
      return HttpResponse.json({ status: 422, detail: 'Invalid.', errors: [{ detail: 'Bad amount.', pointer: '#/budgets/0/amount', code: 'banking.budget_amount' }] }, { status: 422 });
    }));
    const { user } = await renderApp('/spending');

    await user.click(await screen.findByRole('button', { name: 'Proposer des budgets' }));
    const dialog = await screen.findByRole('dialog', { name: 'Budgets proposés' });
    await user.click(await within(dialog).findByRole('button', { name: 'Créer 2 budgets' }));

    await waitFor(() => expect(posted).toEqual([{ budgets: [
      { categoryId: 'c1', amount: '130.00', source: 'typed', period: 'month' },
      { categoryId: 'c2', source: 'committed', period: 'month' },
    ] }]));
    expect(await within(dialog).findByRole('textbox', { name: 'Budget mensuel de Alimentation' }))
      .toHaveAccessibleDescription('Un budget est un montant de zéro ou plus.');
  });

  it('says so in a sentence when there is nothing to propose', async () => {
    spendingServer(month);
    suggestionServer([]);
    const { user } = await renderApp('/spending');

    await user.click(await screen.findByRole('button', { name: 'Proposer des budgets' }));
    const dialog = await screen.findByRole('dialog', { name: 'Budgets proposés' });
    expect(await within(dialog).findByText(/Aucune proposition pour l’instant/)).toBeInTheDocument();
    expect(within(dialog).queryByRole('checkbox')).toBeNull();
  });
});
