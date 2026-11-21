import { screen, waitFor, within } from '@testing-library/react';
import type { JsonBodyType } from 'msw';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';

const line = (id: string, label: string) => ({
  id, bookedOn: '2026-10-27', valueOn: null, amount: '-12.50', currency: 'EUR', label, counterparty: null, fromCard: false, cardName: null, fetchedVia: 'api',
  linkState: 'unlinked', link: null, accountName: 'Compte courant', accountId: 'a1', category: null, categorySource: null, suggestedPattern: 'SHOP ONE', sharedAccount: true,
});
const suggestion = {
  pattern: 'SHOP ONE', direction: 'debit', categoryId: 'groceries', categoryName: 'Courses', lineCount: 2, evidenceCount: 5, agreement: 100,
  lines: [line('t1', 'CB SHOP ONE 12/10'), line('t2', 'CB SHOP ONE 14/10')],
};
const operations = { items: [], total: 0, totals: { out: '0', in: '0', net: '0', currency: 'EUR', incomplete: false }, facets: { accounts: [], owners: [], categories: [] }, accountOwners: [] };

/** The operations page with one suggestion, replaced by `after` once a write lands; every write as `[path, body]`. */
function serve(after: object[] = []) {
  let shown = true;
  const writes: [string, unknown][] = [];
  const write = (path: string, body: unknown, answer: JsonBodyType, status = 200) => { writes.push([path, body]); shown = false; return HttpResponse.json(answer, { status }); };
  server.use(
    http.get('*/api/v1/banking/operations', () => HttpResponse.json(operations)),
    http.get('*/api/v1/banking/category-suggestions', () => HttpResponse.json(shown ? { items: [suggestion], total: 1 } : { items: after, total: after.length })),
    http.post('*/api/v1/banking/transactions/category', async ({ request }) => write('categorise', await request.json(), { changed: ['t1', 't2'], refused: [] })),
    http.post('*/api/v1/banking/category-suggestions/dismissals', async ({ request }) => write('dismiss', await request.json(), { id: 'd1' }, 201)),
    http.post('*/api/v1/banking/category-rules/preview', () => HttpResponse.json({ total: 2, items: [], conflicts: [], tooBroad: false })),
    // As the server: the new rule matches the group's lines, so the block empties before the rule is applied to them.
    http.post('*/api/v1/banking/category-rules', async ({ request }) => write('rule', await request.json(), { id: 'r1' }, 201)),
    http.post('*/api/v1/banking/category-rules/:id/apply', async ({ params }) => {
      // The apply answers once the block has emptied, as it did on a real stack.
      await waitFor(() => expect(screen.queryByRole('region', { name: /suggestion/ })).not.toBeInTheDocument());
      return write(`apply ${String(params.id)}`, null, { changed: 2 }); }),
  );
  return writes;
}

async function open() {
  const app = await renderApp('/spending?tab=operations');
  const block = await screen.findByRole('region', { name: '1 suggestion de catégorie' });
  await app.user.click(within(block).getByRole('button', { name: 'Voir la suggestion' }));
  return { ...app, block };
}

describe('« Suggestions » in Opérations (#449)', () => {
  beforeAll(async () => { await import('@/features/spending/SpendingPage'); await import('./OperationsPage'); }, 30_000);

  it('is not there when there is nothing to suggest', async () => {
    server.use(http.get('*/api/v1/banking/operations', () => HttpResponse.json(operations)));
    await renderApp('/spending?tab=operations');
    await screen.findByText('0 opération');
    expect(screen.queryByRole('region', { name: /suggestion/ })).not.toBeInTheDocument();
  });

  it('shows the group, its counts and its lines as the server gives them, and categorises them by hand', async () => {
    const writes = serve();
    const { user, block } = await open();
    expect(within(block).getByText('SHOP ONE')).toBeInTheDocument();
    expect(within(block).getByText(/2 opérations à classer · d'après 5 opérations classées à la main/)).toBeInTheDocument();
    await user.click(within(block).getByRole('button', { name: 'Voir les opérations' }));
    const lines = within(block).getByRole('list', { name: 'Opérations « SHOP ONE »' });
    expect(within(lines).getAllByRole('listitem')[0]).toHaveTextContent('CB SHOP ONE 12/10 · Compte courant');
    expect(within(lines).getAllByRole('listitem')[0]).toHaveTextContent('-12,50 €');

    await user.click(within(block).getByRole('button', { name: 'Classer ces 2 opérations' }));
    await waitFor(() => expect(writes).toEqual([['categorise', { transactionIds: ['t1', 't2'], categoryId: 'groceries' }]]));
    expect(await screen.findByText('2 opérations classées en Courses.')).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByRole('region', { name: /suggestion/ })).not.toBeInTheDocument());
    // Nothing left to suggest: the focus goes to the list's count, not to the page's body.
    await waitFor(() => expect(screen.getByText('0 opération')).toHaveFocus());
  });

  it('ignores a group for the household, the focus on the block while groups remain', async () => {
    const writes = serve([{ ...suggestion, pattern: 'OTHER SHOP', lines: [line('t3', 'OTHER SHOP')] }]);
    const { user, block } = await open();
    await user.click(within(block).getByRole('button', { name: 'Ignorer' }));
    await waitFor(() => expect(writes).toEqual([['dismiss', { pattern: 'SHOP ONE', direction: 'debit', categoryId: 'groceries' }]]));
    expect(await screen.findByText(/Suggestion ignorée/)).toBeInTheDocument();
    await waitFor(() => expect(screen.getByText('OTHER SHOP')).toBeInTheDocument());
    await waitFor(() => expect(screen.getByRole('heading', { name: '1 suggestion de catégorie' })).toHaveFocus());
  });

  it('offers the rule, its pattern and category filled in and the past lines taken', async () => {
    const writes = serve();
    const { user, block } = await open();
    await user.click(within(block).getByRole('button', { name: 'Créer la règle' }));
    const offer = await screen.findByRole('form', { name: 'Toujours classer « SHOP ONE » en Courses\u00a0?' });
    expect(within(offer).getByRole('textbox', { name: 'Motif' })).toHaveValue('SHOP ONE');
    expect(await within(offer).findByRole('checkbox', { name: 'Appliquer aussi aux 2 opérations passées' })).toBeChecked();
    await user.click(within(offer).getByRole('button', { name: 'Créer la règle' }));

    await waitFor(() => expect(writes).toEqual([['rule', { pattern: 'SHOP ONE', categoryId: 'groceries', accountId: null }], ['apply r1', null]]));
    expect(await screen.findByText('Règle créée, et 2 opérations passées classées.')).toBeInTheDocument();
    await waitFor(() => expect(screen.getByText('0 opération')).toHaveFocus());
  });
});

describe('« Suggestions ignorées » in Catégories (#449)', () => {
  beforeAll(async () => { await import('@/features/spending/SpendingPage'); await import('@/features/spending/CategoriesTab'); }, 30_000);

  it('lists the dismissed suggestions and brings one back', async () => {
    let withdrawn = '';
    server.use(
      http.get('*/api/v1/banking/categories', () => HttpResponse.json([])),
      http.get('*/api/v1/banking/category-rules', () => HttpResponse.json([])),
      http.get('*/api/v1/banking/category-suggestions/dismissals', () => HttpResponse.json(withdrawn ? [] : [
        { id: 'd1', pattern: 'SHOP ONE', direction: 'debit', categoryId: 'groceries', categoryName: 'Courses', createdAt: '2026-10-28T09:00:00Z' },
      ])),
      http.delete('*/api/v1/banking/category-suggestions/dismissals/:id', ({ params }) => { withdrawn = String(params.id); return new HttpResponse(null, { status: 204 }); }),
    );
    const { user } = await renderApp('/spending?tab=categories');
    const section = await screen.findByRole('region', { name: 'Suggestions ignorées' });
    const row = within(within(section).getByRole('table', { name: 'Suggestions ignorées' })).getAllByRole('row')[1]!;
    expect(row).toHaveTextContent('SHOP ONE');
    expect(row).toHaveTextContent('Courses');
    await user.click(within(row).getByRole('button', { name: 'Rétablir « SHOP ONE » en Courses' }));

    await waitFor(() => expect(withdrawn).toBe('d1'));
    expect(await screen.findByText('Suggestion rétablie.')).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByRole('region', { name: 'Suggestions ignorées' })).not.toBeInTheDocument());
  });
});
