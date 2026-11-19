import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { pick } from '@/test/controls';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { BankCategory, BankTransaction } from '@/api/banking';
import { detail, PEA } from '../testData';

const line: BankTransaction = {
  id: 't1', bookedOn: '2026-10-27', valueOn: null, amount: '-42.10', currency: 'EUR', label: 'CARREFOUR CITY', counterparty: null,
  fromCard: true, cardName: null, fetchedVia: 'file', linkState: 'unlinked', link: null, accountName: 'Compte', category: null, categorySource: null,
};
const category = (over: Partial<BankCategory>): BankCategory => ({ id: 'c', parentId: null, key: null, name: '', kind: 'expense', archived: false, mergedIntoId: null, defaultName: null, ...over });
const categories = [
  category({ id: 'food', key: 'food', name: 'Alimentation' }),
  category({ id: 'groceries', parentId: 'food', key: 'food.groceries', name: 'Courses' }),
  category({ id: 'old', parentId: 'food', name: 'Marché du dimanche', archived: true }),
  category({ id: 'gone', parentId: 'food', name: 'Supérette', mergedIntoId: 'groceries' }),
  category({ id: 'income', key: 'income', name: 'Revenus', kind: 'income' }),
];

/** The line, the categories, and every write to the line's category as `[method, body]`. */
function serve() {
  const writes: [string, unknown][] = [];
  server.use(
    http.get('*/api/v1/investments/envelopes/:id', () => HttpResponse.json(detail({ kind: 'checking' }))),
    http.get('*/api/v1/banking/status', () => HttpResponse.json({ enabled: true, configured: false })),
    http.get('*/api/v1/banking/envelopes/:id/transactions', () => HttpResponse.json({ items: [line], hasMore: false })),
    http.get('*/api/v1/banking/categories', () => HttpResponse.json(categories)),
    http.put('*/api/v1/banking/transactions/:id/category', async ({ request }) => { writes.push(['PUT', await request.json()]); return HttpResponse.json(line); }),
    http.delete('*/api/v1/banking/transactions/:id/category', () => { writes.push(['DELETE', null]); return HttpResponse.json(line); }),
  );
  return writes;
}

async function open() {
  const { user } = await renderApp(`/investments/${PEA}?tab=operations`);
  await user.click((await screen.findAllByRole('button', { name: /Actions/ }))[0]!);
  await user.click(await screen.findByRole('menuitem', { name: 'Catégoriser…' }));
  await screen.findByRole('dialog', { name: "Catégoriser l'opération" });
  return user;
}

describe('« Catégoriser… »', () => {
  beforeAll(async () => { await import('../EnvelopeDetailPage'); await import('./OperationsTab'); await import('./CategoryDialog'); }, 30_000);

  it('lists the active categories under their parent, and puts the picked one on the line', async () => {
    const writes = serve();
    const user = await open();

    await user.click(await screen.findByRole('combobox', { name: 'Catégorie' }));
    const list = await screen.findByRole('listbox');
    expect(within(list).getByRole('option', { name: 'Alimentation' })).toBeInTheDocument();
    expect(within(list).getByRole('option', { name: 'Alimentation › Courses' })).toBeInTheDocument();
    expect(within(list).queryByRole('option', { name: /Marché du dimanche|Supérette/ })).toBeNull();
    await user.click(within(list).getByRole('option', { name: 'Alimentation › Courses' }));
    await user.click(screen.getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(writes).toEqual([['PUT', { categoryId: 'groceries' }]]));
  });

  it('« Sans catégorie » sends no category', async () => {
    const writes = serve();
    const user = await open();

    await pick(user, 'Catégorie', 'Sans catégorie');
    await user.click(screen.getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(writes).toEqual([['PUT', { categoryId: null }]]));
  });

  it('« Laisser Nid décider » hands the line back to Nid', async () => {
    const writes = serve();
    const user = await open();

    await pick(user, 'Catégorie', 'Laisser Nid décider');
    await user.click(screen.getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(writes).toEqual([['DELETE', null]]));
  });
});
