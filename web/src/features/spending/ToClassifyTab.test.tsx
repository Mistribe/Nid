import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';

const line = (id: string, label: string, amount = '-12.50') => ({
  id, bookedOn: '2026-10-27', valueOn: null, amount, currency: 'EUR', label, counterparty: null, fromCard: false, cardName: null, fetchedVia: 'api',
  linkState: 'unlinked', link: null, accountName: 'Compte courant', accountId: 'a1', category: null, categorySource: null, suggestedPattern: null, sharedAccount: true,
});
const suggestion = (categoryId: string, categoryName: string, byMeaning = false) =>
  ({ categoryId, categoryName, evidenceCount: 3, agreement: 100, byMeaning, pattern: 'SHOP' });
const page = {
  items: [
    { line: line('t1', 'CB SHOP ONE 12/10'), suggestion: suggestion('groceries', 'Courses') },
    { line: line('t2', 'CB CITY MARKET 14/10'), suggestion: suggestion('groceries', 'Courses', true) },
    { line: line('t3', 'PRLV ENERGIE'), suggestion: suggestion('energy', 'Énergie') },
    { line: line('t4', 'VIR INCONNU'), suggestion: null },
  ],
  total: 4,
  accounts: [{ id: 'a1', name: 'Compte courant' }, { id: 'a2', name: 'Livret' }],
};

/** The tab's list; every categorisation as its body. */
function serve() {
  const sent: unknown[] = [];
  const queries: (string | null)[] = [];
  server.use(
    http.get('*/api/v1/banking/lines-to-classify', ({ request }) => { queries.push(new URL(request.url).searchParams.get('account')); return HttpResponse.json(page); }),
    http.post('*/api/v1/banking/transactions/category', async ({ request }) => {
      const body = await request.json() as { transactionIds: string[] };
      sent.push(body);
      return HttpResponse.json({ changed: body.transactionIds, refused: [] });
    }),
  );
  return { sent, queries };
}

describe('Dépenses › À classer', () => {
  beforeAll(async () => { await import('./SpendingPage'); await import('./ToClassifyTab'); }, 30_000);

  it("shows each line with Nid's suggestion and accepts one", async () => {
    const { sent } = serve();
    const { user, container } = await renderApp('/spending?tab=to-classify');

    const table = await screen.findByRole('table');
    expect(within(table).getByRole('row', { name: /CB SHOP ONE/ })).toHaveTextContent('Courses');
    expect(within(table).getByRole('row', { name: /VIR INCONNU/ })).toHaveTextContent('—');
    expect(within(within(table).getByRole('row', { name: /VIR INCONNU/ })).queryByRole('button', { name: /^Accepter/ })).not.toBeInTheDocument();

    await user.click(within(table).getByRole('button', { name: 'Accepter Courses pour « CB SHOP ONE 12/10 »' }));
    await waitFor(() => expect(sent).toEqual([{ transactionIds: ['t1'], categoryId: 'groceries' }]));
    expect(await screen.findByText('1 opération classée.')).toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it("accepts the page's suggestions, one call per category", async () => {
    const { sent } = serve();
    const { user } = await renderApp('/spending?tab=to-classify');

    await user.click(await screen.findByRole('button', { name: 'Accepter les 3 suggestions de la page' }));
    await waitFor(() => expect(sent).toEqual([
      { transactionIds: ['t1', 't2'], categoryId: 'groceries' },
      { transactionIds: ['t3'], categoryId: 'energy' },
    ]));
    expect(await screen.findByText('3 opérations classées.')).toBeInTheDocument();
  });

  it('sends the account picked to the server', async () => {
    const { queries } = serve();
    await renderApp('/spending?tab=to-classify&account=a2');
    await screen.findByRole('table');
    expect(queries.at(-1)).toBe('a2');
  });
});
