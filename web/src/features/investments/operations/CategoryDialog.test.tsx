import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { pick } from '@/test/controls';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { BankCategory, BankTransaction, CategoryRuleDraftPreview } from '@/api/banking';
import { detail, PEA } from '../testData';

const line: BankTransaction = {
  id: 't1', bookedOn: '2026-10-27', valueOn: null, amount: '-42.10', currency: 'EUR', label: 'CARREFOUR CITY', counterparty: null,
  fromCard: true, cardName: null, fetchedVia: 'file', linkState: 'unlinked', link: null, accountName: 'Compte', category: null, categorySource: null,
  accountId: 'acc-1', suggestedPattern: 'CARREFOUR CITY', sharedAccount: false,
};
const category = (over: Partial<BankCategory>): BankCategory => ({ id: 'c', parentId: null, key: null, name: '', kind: 'expense', archived: false, mergedIntoId: null, defaultName: null, lines: 0, ...over });
const categories = [
  category({ id: 'food', key: 'food', name: 'Alimentation' }),
  category({ id: 'groceries', parentId: 'food', key: 'food.groceries', name: 'Courses' }),
  category({ id: 'old', parentId: 'food', name: 'Marché du dimanche', archived: true }),
  category({ id: 'gone', parentId: 'food', name: 'Supérette', mergedIntoId: 'groceries' }),
  category({ id: 'income', key: 'income', name: 'Revenus', kind: 'income' }),
];

const courses = { id: 'groceries', name: 'Courses', parentId: 'food', parentName: 'Alimentation', kind: 'expense' };

const nothing: CategoryRuleDraftPreview = { total: 0, items: [], conflicts: [], tooBroad: false };
/** Every draft rule the dialog asked a preview for. */
let previews: unknown[] = [];

/**
 * The line (`over` on top), the categories, a draft rule's `preview`, and every write to a line's category or a rule as
 * `[method, line or rule id, body]`.
 */
function serve(over: Partial<BankTransaction> = {}, preview: CategoryRuleDraftPreview = nothing, sameLabel: string[] = []) {
  const writes: [string, unknown, unknown][] = [];
  previews = [];
  server.use(
    http.get('*/api/v1/investments/envelopes/:id', () => HttpResponse.json(detail({ kind: 'checking' }))),
    http.get('*/api/v1/banking/status', () => HttpResponse.json({ enabled: true, configured: false })),
    http.get('*/api/v1/banking/envelopes/:id/transactions', () => HttpResponse.json({ items: [{ ...line, ...over }], hasMore: false })),
    http.get('*/api/v1/banking/categories', () => HttpResponse.json(categories)),
    http.put('*/api/v1/banking/transactions/:id/category', async ({ request, params }) => { writes.push(['PUT', params.id, await request.json()]); return HttpResponse.json(line); }),
    http.delete('*/api/v1/banking/transactions/:id/category', ({ params }) => { writes.push(['DELETE', params.id, null]); return HttpResponse.json(line); }),
    http.get('*/api/v1/banking/transactions/:id/same-label', () => HttpResponse.json(sameLabel)),
    http.post('*/api/v1/banking/transactions/category', async ({ request }) => {
      const body = await request.json() as { transactionIds: string[] };
      writes.push(['BULK', null, body]);
      return HttpResponse.json({ changed: body.transactionIds, refused: [] });
    }),
    http.post('*/api/v1/banking/category-rules', async ({ request }) => { writes.push(['RULE', null, await request.json()]); return HttpResponse.json({ id: 'r1' }, { status: 201 }); }),
    http.post('*/api/v1/banking/category-rules/preview', async ({ request }) => { previews.push(await request.json()); return HttpResponse.json(preview); }),
    http.post('*/api/v1/banking/category-rules/:id/apply', ({ params }) => { writes.push(['APPLY', params.id, null]); return HttpResponse.json({ changed: 2 }); }),
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

  it('classifies the other lines of the same label with it, when asked (#721)', async () => {
    const writes = serve({ suggestedPattern: null }, nothing, ['t8', 't9']);
    const user = await open();

    await pick(user, 'Catégorie', 'Alimentation › Courses');
    await user.click(await screen.findByRole('checkbox', { name: 'Appliquer aussi aux 2 opérations au même libellé' }));
    await user.click(screen.getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(writes).toEqual([['BULK', null, { transactionIds: ['t1', 't8', 't9'], categoryId: 'groceries' }]]));
    expect(await screen.findByText('3 opérations classées.')).toBeInTheDocument();
  });

  it('offers no other line for « Sans catégorie »', async () => {
    serve({}, nothing, ['t8']);
    const user = await open();
    await pick(user, 'Catégorie', 'Alimentation');
    expect(await screen.findByRole('checkbox', { name: 'Appliquer aussi à l’autre opération au même libellé' })).toBeInTheDocument();
    await pick(user, 'Catégorie', 'Sans catégorie');
    expect(screen.queryByRole('checkbox', { name: /même libellé/ })).toBeNull();
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
    // No category, no rule to offer: the dialog closes.
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
  });

  it('then offers a rule from the line’s suggested pattern, on its account alone when not every adult reads it', async () => {
    const writes = serve();
    const user = await open();

    await pick(user, 'Catégorie', 'Alimentation › Courses');
    await user.click(screen.getByRole('button', { name: 'Enregistrer' }));

    const offer = await screen.findByRole('form', { name: 'Toujours classer « CARREFOUR CITY » en Alimentation › Courses\u00a0?' });
    expect(within(offer).getByRole('textbox', { name: 'Motif' })).toHaveValue('CARREFOUR CITY');
    expect(within(offer).getByRole('checkbox', { name: 'Seulement ce compte (Compte)' })).toBeChecked();
    await user.clear(within(offer).getByRole('textbox', { name: 'Motif' }));
    await user.type(within(offer).getByRole('textbox', { name: 'Motif' }), 'CARREFOUR');
    await user.click(within(offer).getByRole('button', { name: 'Créer la règle' }));

    await waitFor(() => expect(writes).toEqual([
      ['PUT', 't1', { categoryId: 'groceries' }],
      ['RULE', null, { pattern: 'CARREFOUR', categoryId: 'groceries', accountId: 'acc-1' }],
    ]));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
  });

  it('leaves « Seulement ce compte » unticked on an account every adult reads, and « Non merci » creates nothing', async () => {
    const writes = serve({ sharedAccount: true });
    const user = await open();

    await pick(user, 'Catégorie', 'Alimentation');
    await user.click(screen.getByRole('button', { name: 'Enregistrer' }));

    expect(await screen.findByRole('checkbox', { name: 'Seulement ce compte (Compte)' })).not.toBeChecked();
    await user.click(screen.getByRole('button', { name: 'Non merci' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(writes).toEqual([['PUT', 't1', { categoryId: 'food' }]]);
  });

  it('says so in plain words when the rule is refused on this account', async () => {
    serve();
    server.use(http.post('*/api/v1/banking/category-rules', () => HttpResponse.json({ status: 403, detail: 'Forbidden.' }, { status: 403 })));
    const user = await open();

    await pick(user, 'Catégorie', 'Alimentation');
    await user.click(screen.getByRole('button', { name: 'Enregistrer' }));
    expect(await screen.findByRole('status')).toHaveTextContent('Catégorie enregistrée.');
    await user.click(await screen.findByRole('button', { name: 'Créer la règle' }));

    expect(await screen.findByText('Vous ne pouvez pas créer de règle sur ce compte.')).toBeInTheDocument();
  });

  it('previews the rule as its pattern is typed: the past lines it would change, those it would not by reason, a too broad pattern', async () => {
    serve({}, {
      total: 2,
      items: [{ ...line, id: 'p1', bookedOn: '2026-09-14', label: 'CARREFOUR MARKET', amount: '-18.00', category: null }],
      conflicts: [
        { reason: 'hand', rulePattern: null, categoryId: 'income', categoryName: 'Revenus', lines: 3 },
        { reason: 'rule', rulePattern: 'CARREFOUR CITY', categoryId: 'food', categoryName: 'Alimentation', lines: 1 },
        { reason: 'link', rulePattern: null, categoryId: null, categoryName: null, lines: 2 },
      ],
      tooBroad: true,
    });
    const user = await open();
    await pick(user, 'Catégorie', 'Alimentation › Courses');
    await user.click(screen.getByRole('button', { name: 'Enregistrer' }));
    const offer = await screen.findByRole('form', { name: /Toujours classer/ });
    await user.clear(within(offer).getByRole('textbox', { name: 'Motif' }));
    await user.type(within(offer).getByRole('textbox', { name: 'Motif' }), 'CARREFOUR');

    expect(await within(offer).findByText('2 opérations passées passeraient en Alimentation › Courses.')).toBeInTheDocument();
    expect(within(within(offer).getByRole('list', { name: 'Les plus récentes' })).getByRole('listitem')).toHaveTextContent('CARREFOUR MARKET · Sans catégorie');
    expect(within(offer).getByText('3 classées à la main en Revenus')).toBeInTheDocument();
    expect(within(offer).getByText('1 gardée par la règle « CARREFOUR CITY » (Alimentation)')).toBeInTheDocument();
    expect(within(offer).getByText('2 classées par leur lien en Sans catégorie')).toBeInTheDocument();
    expect(within(offer).getByText(/Ce motif est très large/)).toBeInTheDocument();
    // Asked for the pattern once typed, not for each key.
    await waitFor(() => expect(previews.at(-1)).toEqual({ pattern: 'CARREFOUR', categoryId: 'groceries', accountId: 'acc-1' }));
    expect(previews).not.toContainEqual(expect.objectContaining({ pattern: 'CARRE' }));
  });

  it('applies the new rule to the past lines when asked', async () => {
    const writes = serve({}, { ...nothing, total: 2 });
    const user = await open();
    await pick(user, 'Catégorie', 'Alimentation › Courses');
    await user.click(screen.getByRole('button', { name: 'Enregistrer' }));

    await user.click(await screen.findByRole('checkbox', { name: 'Appliquer aussi aux 2 opérations passées' }));
    await user.click(screen.getByRole('button', { name: 'Créer la règle' }));

    await waitFor(() => expect(writes).toEqual([
      ['PUT', 't1', { categoryId: 'groceries' }],
      ['RULE', null, { pattern: 'CARREFOUR CITY', categoryId: 'groceries', accountId: 'acc-1' }],
      ['APPLY', 'r1', null],
    ]));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
  });

  it('offers no rule for a line without a suggested pattern', async () => {
    serve({ suggestedPattern: null });
    const user = await open();

    await pick(user, 'Catégorie', 'Alimentation');
    await user.click(screen.getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
  });

  it('« Laisser Nid décider » hands the line back to Nid', async () => {
    const writes = serve();
    const user = await open();

    await pick(user, 'Catégorie', 'Laisser Nid décider');
    await user.click(screen.getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(writes).toEqual([['DELETE', 't1', null]]));
  });
});
