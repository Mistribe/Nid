import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { pick } from '@/test/controls';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';

const base = { bookedOn: '2026-10-27', valueOn: null, currency: 'EUR', counterparty: null, fromCard: false, cardName: null, fetchedVia: 'file', link: null, accountName: 'Compte courant', accountId: 'a1', sharedAccount: false };
const shop = { ...base, id: 't1', amount: '-60.00', label: 'MARCHE', linkState: 'unlinked', category: null, note: null, parts: null };
const splitShop = { ...shop, categorySource: 'split', parts: [
  { id: 'p1', amount: '-40.00', category: { id: 'food', name: 'Alimentation' }, note: 'fruits' },
  { id: 'p2', amount: '-20.00', category: null, note: null },
] };
const facets = { accounts: [{ id: 'a1', name: 'Compte courant' }], owners: [], categories: [] };
const answer = (items: unknown[], writable = true) => ({ items, total: items.length, totals: { out: '60.00', in: '0.00', net: '-60.00', currency: 'EUR', incomplete: false }, facets, accountOwners: [{ accountId: 'a1', owners: [], writable }] });
const categories = [{ id: 'food', parentId: null, key: null, name: 'Alimentation', kind: 'expense', archived: false, mergedIntoId: null, defaultName: null }];

function serve(line: object, writable = true) {
  const writes: [string, unknown][] = [];
  server.use(
    http.get('*/api/v1/banking/operations', () => HttpResponse.json(answer([line], writable))),
    http.get('*/api/v1/core/persons', () => HttpResponse.json([])),
    http.get('*/api/v1/banking/categories', () => HttpResponse.json(categories)),
    http.put('*/api/v1/banking/transactions/:id/note', async ({ request }) => { writes.push(['note', await request.json()]); return HttpResponse.json(line); }),
    http.delete('*/api/v1/banking/transactions/:id/split', () => { writes.push(['unsplit', null]); return HttpResponse.json(shop); }),
  );
  return writes;
}

async function openMenu(item: string) {
  const { user } = await renderApp('/operations');
  const row = within(await screen.findByRole('table', { name: 'Opérations' })).getAllByRole('row')[1]!;
  await user.click(within(row).getByRole('button', { name: /Actions/ }));
  await user.click(await screen.findByRole('menuitem', { name: item }));
  return user;
}

describe('« Opérations »: note and split', () => {
  beforeAll(async () => { await import('./OperationsPage'); await import('../investments/operations/NoteDialog'); await import('../investments/operations/SplitDialog'); }, 30_000);

  it('saves a note, and clears it when emptied', async () => {
    const writes = serve({ ...shop, note: 'pour Léa' });
    const user = await openMenu('Note…');
    const box = await screen.findByRole('textbox', { name: 'Note' });
    expect(box).toHaveValue('pour Léa');
    await user.clear(box);
    await user.click(screen.getByRole('button', { name: 'Enregistrer' }));
    await waitFor(() => expect(writes).toEqual([['note', { note: null }]]));
  });

  it('sends the parts with the line sign, and adds and removes rows', async () => {
    const writes: unknown[] = [];
    serve(shop);
    server.use(http.put('*/api/v1/banking/transactions/:id/split', async ({ request }) => { writes.push(await request.json()); return HttpResponse.json(splitShop); }));
    const user = await openMenu('Découper…');
    const dialog = await screen.findByRole('dialog', { name: "Découper l'opération" });
    expect(within(dialog).getByText(/60,00/)).toBeInTheDocument();
    expect(within(dialog).getAllByRole('textbox', { name: /Montant/ })).toHaveLength(2);
    await user.click(within(dialog).getByRole('button', { name: 'Ajouter une partie' }));
    expect(within(dialog).getAllByRole('textbox', { name: /Montant/ })).toHaveLength(3);
    await user.click(within(dialog).getByRole('button', { name: 'Retirer la partie 3' }));
    const amounts = within(dialog).getAllByRole('textbox', { name: /Montant/ });
    expect(amounts).toHaveLength(2);
    await user.type(amounts[0]!, '40,00');
    await user.type(amounts[1]!, '20');
    await pick(user, 'Catégorie de la partie 1', 'Alimentation');
    await user.type(within(dialog).getByRole('textbox', { name: 'Note de la partie 1' }), 'fruits');
    await user.click(within(dialog).getByRole('button', { name: 'Découper' }));
    await waitFor(() => expect(writes).toEqual([{ parts: [
      { amount: '-40.00', categoryId: 'food', note: 'fruits' },
      { amount: '-20', categoryId: null, note: null },
    ] }]));
  });

  it('says by how much the parts miss the line', async () => {
    serve(shop);
    server.use(http.put('*/api/v1/banking/transactions/:id/split', () => HttpResponse.json(
      { status: 422, code: 'banking.split_sum', detail: "The parts add up to the line's amount.", partsSum: '-50.00', lineAmount: '-60.00' },
      { status: 422, headers: { 'content-type': 'application/problem+json' } })));
    const user = await openMenu('Découper…');
    const dialog = await screen.findByRole('dialog', { name: "Découper l'opération" });
    const amounts = within(dialog).getAllByRole('textbox', { name: /Montant/ });
    await user.type(amounts[0]!, '30');
    await user.type(amounts[1]!, '20');
    await user.click(within(dialog).getByRole('button', { name: 'Découper' }));
    expect(await within(dialog).findByText(/Les parties font 50,00.€, l'opération fait 60,00.€\./)).toBeInTheDocument();
  });

  it('sends a credit line unsigned, a comma as a point, and refuses junk typed amounts', async () => {
    const writes: unknown[] = [];
    serve({ ...shop, amount: '60.00' });
    server.use(http.put('*/api/v1/banking/transactions/:id/split', async ({ request }) => { writes.push(await request.json()); return HttpResponse.json(splitShop); }));
    const user = await openMenu('Découper…');
    const dialog = await screen.findByRole('dialog', { name: "Découper l'opération" });
    const amounts = within(dialog).getAllByRole('textbox', { name: /Montant/ });
    await user.type(amounts[0]!, '5,5,0');
    await user.type(amounts[1]!, 'abc');
    await user.click(within(dialog).getByRole('button', { name: 'Découper' }));
    expect(within(dialog).getAllByText('Saisissez un montant positif, avec au plus deux décimales.')).toHaveLength(2);
    expect(writes).toEqual([]);
    await user.clear(amounts[0]!); await user.type(amounts[0]!, '5,50');
    await user.clear(amounts[1]!); await user.type(amounts[1]!, '54.50');
    await user.click(within(dialog).getByRole('button', { name: 'Découper' }));
    await waitFor(() => expect(writes).toEqual([{ parts: [{ amount: '5.50', categoryId: null, note: null }, { amount: '54.50', categoryId: null, note: null }] }]));
  });

  it('shows a split line with its parts, and un-splits it from the menu', async () => {
    const writes = serve(splitShop);
    const user = await openMenu('Annuler le découpage');
    expect(writes).toEqual([]);
    await user.click(await screen.findByRole('button', { name: 'Confirmer' }));
    await waitFor(() => expect(writes).toEqual([['unsplit', null]]));
    void user;
  });

  it('offers no actions on a line of an account the reader cannot write', async () => {
    serve(shop, false);
    await renderApp('/operations');
    const row = within(await screen.findByRole('table', { name: 'Opérations' })).getAllByRole('row')[1]!;
    expect(row).toHaveTextContent('MARCHE');
    expect(screen.queryByRole('button', { name: /Actions/ })).not.toBeInTheDocument();
  });

  it('shows the parts of a split line on the narrow list too', async () => {
    serve(splitShop);
    await renderApp('/operations');
    await screen.findByRole('table', { name: 'Opérations' });
    expect(screen.getAllByText('-40,00 € · Alimentation · fruits')).toHaveLength(2);
  });

  it('lists a split line as « Découpée (2) » with its parts under it', async () => {
    serve(splitShop);
    await renderApp('/operations');
    const row = within(await screen.findByRole('table', { name: 'Opérations' })).getAllByRole('row')[1]!;
    expect(row).toHaveTextContent('Découpée (2)');
    expect(row).toHaveTextContent('-40,00 € · Alimentation · fruits');
    expect(row).toHaveTextContent('-20,00 € · Sans catégorie');
  });
});
