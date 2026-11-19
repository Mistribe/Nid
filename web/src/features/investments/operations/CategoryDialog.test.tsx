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

const courses = { id: 'groceries', name: 'Courses', parentId: 'food', parentName: 'Alimentation', kind: 'expense' };

/** The line (`over` on top), the categories, and every write to a line's category as `[method, line id, body]`. */
function serve(over: Partial<BankTransaction> = {}) {
  const writes: [string, unknown, unknown][] = [];
  server.use(
    http.get('*/api/v1/investments/envelopes/:id', () => HttpResponse.json(detail({ kind: 'checking' }))),
    http.get('*/api/v1/banking/status', () => HttpResponse.json({ enabled: true, configured: false })),
    http.get('*/api/v1/banking/envelopes/:id/transactions', () => HttpResponse.json({ items: [{ ...line, ...over }], hasMore: false })),
    http.get('*/api/v1/banking/categories', () => HttpResponse.json(categories)),
    http.put('*/api/v1/banking/transactions/:id/category', async ({ request, params }) => { writes.push(['PUT', params.id, await request.json()]); return HttpResponse.json(line); }),
    http.delete('*/api/v1/banking/transactions/:id/category', ({ params }) => { writes.push(['DELETE', params.id, null]); return HttpResponse.json(line); }),
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

    await waitFor(() => expect(writes).toEqual([['PUT', 't1', { categoryId: 'groceries' }]]));
  });

  it('opens on the line’s category', async () => {
    serve({ category: courses, categorySource: 'rule' });
    await open();

    expect(await screen.findByRole('combobox', { name: 'Catégorie' })).toHaveTextContent('Alimentation › Courses');
  });

  it('does not offer to save a link’s or a rule’s category as it is, nor a hand « Sans catégorie »', async () => {
    serve({ category: courses, categorySource: 'rule' });
    const user = await open();
    expect(screen.getByRole('button', { name: 'Enregistrer' })).toBeDisabled();
    await pick(user, 'Catégorie', 'Alimentation');
    expect(screen.getByRole('button', { name: 'Enregistrer' })).toBeEnabled();
  });

  it('keeps « Enregistrer » off on a line already left without category by hand', async () => {
    serve({ category: null, categorySource: 'hand' });
    await open();
    expect(await screen.findByRole('combobox', { name: 'Catégorie' })).toHaveTextContent('Sans catégorie');
    expect(screen.getByRole('button', { name: 'Enregistrer' })).toBeDisabled();
  });

  it('says so when the line’s category is archived, and asks for another', async () => {
    serve({ category: { ...courses, id: 'old', name: 'Marché du dimanche' }, categorySource: 'hand' });
    await open();

    const field = await screen.findByRole('combobox', { name: 'Catégorie' });
    await waitFor(() => expect(field).toHaveAccessibleDescription('Sa catégorie actuelle, Marché du dimanche, est archivée : choisissez-en une autre.'));
    expect(screen.getByRole('button', { name: 'Enregistrer' })).toBeDisabled();
  });

  it('« Sans catégorie » sends no category', async () => {
    const writes = serve();
    const user = await open();

    await pick(user, 'Catégorie', 'Sans catégorie');
    await user.click(screen.getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(writes).toEqual([['PUT', 't1', { categoryId: null }]]));
  });

  it('« Laisser Nid décider » hands the line back to Nid', async () => {
    const writes = serve();
    const user = await open();

    await pick(user, 'Catégorie', 'Laisser Nid décider');
    await user.click(screen.getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(writes).toEqual([['DELETE', 't1', null]]));
  });
});
