import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { pick } from '@/test/controls';
import { expectLabelBefore } from '@/test/filters';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { EnvelopeDetail } from '@/api/investments';
import { detail, PEA } from '../testData';
import type { BankTransaction } from '@/api/banking';

const line = (over: Partial<BankTransaction> = {}): BankTransaction => ({
  id: 't1', bookedOn: '2026-10-27', valueOn: null, amount: '2450.00', currency: 'EUR', label: 'VIR SEPA SOCIETE EXEMPLE SAS',
  counterparty: 'Société Exemple', fromCard: false, cardName: null, fetchedVia: 'file', linkState: 'unlinked', link: null,
  accountName: 'Compte courant', category: null, categorySource: null, accountId: 'acc-1', suggestedPattern: 'SOCIETE EXEMPLE', sharedAccount: true, ...over,
});

/** Banking on (not configured: files alone), the envelope, and its lines page by page; every query string is kept. */
function serve(pages: BankTransaction[][], envelope: EnvelopeDetail = detail({ kind: 'checking' })) {
  const asked: URLSearchParams[] = [];
  server.use(
    http.get('*/api/v1/investments/envelopes/:id', () => HttpResponse.json(envelope)),
    http.get('*/api/v1/banking/status', () => HttpResponse.json({ enabled: true, configured: false })),
    http.get('*/api/v1/banking/envelopes/:id/transactions', ({ request }) => {
      const query = new URL(request.url).searchParams;
      asked.push(query);
      const page = Number(query.get('page') ?? '1');
      return HttpResponse.json({ items: pages[page - 1] ?? [], hasMore: page < pages.length });
    }),
  );
  return asked;
}

describe('« Opérations »', () => {
  beforeAll(async () => { await import('../EnvelopeDetailPage'); await import('./OperationsTab'); }, 30_000);

  it('lists the server’s lines: date, label, counterparty, amount as sent, and a card’s badge', async () => {
    serve([[line(), line({ id: 't2', bookedOn: '2026-10-12', amount: '-13.49', label: 'AMAZON EU SARL', counterparty: null, fromCard: true, cardName: 'Visa Premier' })]]);
    const { container } = await renderApp(`/investments/${PEA}?tab=operations`);

    const rows = within(await screen.findByRole('table', { name: 'Opérations' })).getAllByRole('row').slice(1);
    expect(rows[0]).toHaveTextContent('27 oct. 2026');
    expect(rows[0]).toHaveTextContent('VIR SEPA SOCIETE EXEMPLE SAS');
    expect(rows[0]).toHaveTextContent('Société Exemple');
    expect(rows[0]).toHaveTextContent('2 450,00 €');
    expect(rows[0]).not.toHaveTextContent('Carte');
    expect(rows[1]).toHaveTextContent('-13,49 €');
    expect(within(rows[1]!).getByText('Carte')).toBeInTheDocument();
    expect(rows[1]).toHaveTextContent('Visa Premier');
    expect(screen.getByRole('list', { name: 'Opérations' })).toHaveTextContent('AMAZON EU SARL');
    await expectNoAxeViolations(container);
  });

  it('shows a line’s note, and a split line’s parts on the narrow list', async () => {
    serve([[line({ note: 'pour Léa' }), line({ id: 't2', amount: '-60.00', label: 'MARCHE', categorySource: 'split', parts: [
      { id: 'p1', amount: '-40.00', category: { id: 'food', name: 'Alimentation', kind: 'expense' }, note: 'fruits' },
      { id: 'p2', amount: '-20.00', category: null, note: null },
    ] })]]);
    await renderApp(`/investments/${PEA}?tab=operations`);

    const rows = within(await screen.findByRole('table', { name: 'Opérations' })).getAllByRole('row').slice(1);
    expect(rows[0]).toHaveTextContent('pour Léa');
    expect(within(screen.getByRole('list', { name: 'Opérations' })).getAllByText('-40,00 € · Alimentation · fruits')).toHaveLength(1);
  });

  it('« Plus » loads the next page under the first', async () => {
    const asked = serve([[line()], [line({ id: 't2', label: 'PRLV SEPA EDF' })]]);
    const { user } = await renderApp(`/investments/${PEA}?tab=operations`);

    await user.click(await screen.findByRole('button', { name: 'Plus' }));

    const table = screen.getByRole('table', { name: 'Opérations' });
    await waitFor(() => expect(within(table).getAllByRole('row')).toHaveLength(3));
    expect(table).toHaveTextContent('PRLV SEPA EDF');
    expect(asked.map((q) => q.get('page'))).toEqual(['1', '2']);
    expect(screen.queryByRole('button', { name: 'Plus' })).toBeNull();
  });

  it('puts its period in the filter row, each label before its date', async () => {
    serve([[line()]]);
    await renderApp(`/investments/${PEA}?tab=operations`);

    await screen.findByRole('table', { name: 'Opérations' });
    const row = screen.getByRole('group', { name: 'Filtres' });
    expectLabelBefore(within(row).getByText('Du'), within(row).getByRole('textbox', { name: 'Du' }));
    expectLabelBefore(within(row).getByText('Au'), within(row).getByRole('textbox', { name: 'Au' }));
    expect(within(row).getByRole('button', { name: 'Choisir une date : Du' })).toBeInTheDocument();
  });

  it('filters on a period', async () => {
    const asked = serve([[line()]]);
    const { user } = await renderApp(`/investments/${PEA}?tab=operations`);

    await screen.findByRole('table', { name: 'Opérations' });
    await user.type(screen.getByLabelText('Du'), '01/10/2026');
    await user.type(screen.getByLabelText('Au'), '15/10/2026');

    await waitFor(() => expect(asked.at(-1)?.get('to')).toBe('2026-10-15'));
    expect(asked.at(-1)?.get('from')).toBe('2026-10-01');
  });

  it('says when there is no line', async () => {
    serve([]);
    await renderApp(`/investments/${PEA}?tab=operations`);

    expect(await screen.findByText('Aucune opération.')).toBeInTheDocument();
  });

  it('shows the link state and the item name', async () => {
    serve([[
      line({ id: 'a' }),
      line({ id: 'b', linkState: 'linked', link: { outcome: 'as_expected', module: 'subscriptions', itemId: 'i1', itemName: 'Netflix' } }),
      line({ id: 'c', linkState: 'internal', link: { outcome: 'internal_transfer', counterpartName: 'Livret A' } }),
      line({ id: 'd', linkState: 'ignored' }),
    ]]);
    await renderApp(`/investments/${PEA}?tab=operations`);

    const rows = within(await screen.findByRole('table', { name: 'Opérations' })).getAllByRole('row').slice(1);
    expect(rows[0]).toHaveTextContent('À relier');
    expect(rows[1]).toHaveTextContent('Reliée · Netflix');
    expect(rows[2]).toHaveTextContent('Interne · Livret A');
    expect(rows[3]).toHaveTextContent('Ignorée');
  });

  it('shows a line’s category and where it comes from, « — » when it has none', async () => {
    serve([[
      line({ id: 'a', category: { id: 'groceries', name: 'Courses', parentId: 'food', parentName: 'Alimentation', kind: 'expense' }, categorySource: 'hand' }),
      line({ id: 'b' }),
      line({ id: 'c', category: null, categorySource: 'hand' }),
    ]]);
    const { container } = await renderApp(`/investments/${PEA}?tab=operations`);

    const table = await screen.findByRole('table', { name: 'Opérations' });
    expect(within(table).getByRole('columnheader', { name: /Catégorie/ })).toBeInTheDocument();
    const rows = within(table).getAllByRole('row').slice(1);
    expect(rows[0]).toHaveTextContent('CoursesÀ la main');
    expect(within(rows[0]!).getByRole('button', { name: 'À propos : À la main' })).toBeInTheDocument();
    expect(rows[1]).toHaveTextContent('—');
    expect(rows[2]).toHaveTextContent('Sans catégorieÀ la main');
    expect(screen.getByRole('list', { name: 'Opérations' })).toHaveTextContent('Courses');
    await expectNoAxeViolations(container);
  });

  it('offers « Catégoriser… » on every line but an ignored one', async () => {
    serve([[line({ id: 'a', linkState: 'linked', link: null }), line({ id: 'b', linkState: 'ignored' })]]);
    const { user } = await renderApp(`/investments/${PEA}?tab=operations`);

    const table = await screen.findByRole('table', { name: 'Opérations' });
    const [linked, ignored] = within(table).getAllByRole('button', { name: /Actions/ });
    await user.click(linked!);
    expect(await screen.findByRole('menuitem', { name: 'Catégoriser…' })).toBeInTheDocument();
    await user.keyboard('{Escape}');
    await user.click(ignored!);
    await screen.findByRole('menuitem', { name: 'Délier' });
    expect(screen.queryByRole('menuitem', { name: 'Catégoriser…' })).toBeNull();
  });

  it('a masked link reads « Reliée » only', async () => {
    serve([[line({ linkState: 'linked', link: null })]]);
    await renderApp(`/investments/${PEA}?tab=operations`);

    const row = within(await screen.findByRole('table', { name: 'Opérations' })).getAllByRole('row')[1]!;
    expect(row).toHaveTextContent(/Reliée$/);
  });

  it('links a line through the dialog', async () => {
    serve([[line()]]);
    let body: unknown;
    server.use(
      http.get('*/api/v1/banking/transactions/:id/candidates', () => HttpResponse.json([
        { module: 'realestate', itemId: 'p1', itemName: 'Loyer Lyon', kind: 'Rent', key: 'rent:2026-10', dueOn: '2026-10-28', amount: '2450.00', currency: 'EUR', credit: true, gap: '0.00' },
      ])),
      http.post('*/api/v1/banking/transactions/:id/link', async ({ request }) => { body = await request.json(); return HttpResponse.json(line({ linkState: 'linked' })); }),
    );
    const { user } = await renderApp(`/investments/${PEA}?tab=operations`);

    await user.click((await screen.findAllByRole('button', { name: /Actions/ }))[0]!);
    await user.click(await screen.findByRole('menuitem', { name: 'Relier…' }));
    await screen.findByRole('combobox', { name: 'Charge attendue' });
    await pick(user, 'Charge attendue', /Loyer Lyon · 28\/10\/2026 · 2\s450,00\s€ · écart 0,00\s€/);
    await user.click(screen.getByRole('button', { name: 'Relier' }));

    await waitFor(() => expect(body).toEqual({ module: 'realestate', itemId: 'p1', key: 'rent:2026-10' }));
  });

  it('offers « Délier » on a linked line and posts the unlink', async () => {
    serve([[line({ linkState: 'linked', link: null })]]);
    let called = false;
    server.use(http.post('*/api/v1/banking/transactions/:id/unlink', () => { called = true; return HttpResponse.json(line()); }));
    const { user } = await renderApp(`/investments/${PEA}?tab=operations`);

    await user.click((await screen.findAllByRole('button', { name: /Actions/ }))[0]!);
    await user.click(await screen.findByRole('menuitem', { name: 'Délier' }));

    await waitFor(() => expect(called).toBe(true));
  });

  it('offers « Délier » on an internal line too', async () => {
    serve([[line({ linkState: 'internal', link: { outcome: 'internal_transfer', counterpartName: 'Livret A' } })]]);
    let called = false;
    server.use(http.post('*/api/v1/banking/transactions/:id/unlink', () => { called = true; return HttpResponse.json(line()); }));
    const { user } = await renderApp(`/investments/${PEA}?tab=operations`);

    await user.click((await screen.findAllByRole('button', { name: /Actions/ }))[0]!);
    await user.click(await screen.findByRole('menuitem', { name: 'Délier' }));

    await waitFor(() => expect(called).toBe(true));
  });

  describe('several lines at once', () => {
    const table = () => screen.getByRole('table', { name: 'Opérations' });
    const box = (label: string) => within(table()).getByRole('checkbox', { name: `Sélectionner « ${label} »` });
    const all = () => within(table()).getByRole('checkbox', { name: 'Sélectionner les opérations de la page' });

    function serveBulk(items: BankTransaction[], answerWith: (ids: string[]) => { changed: string[]; refused: { id: string; code: string }[] }) {
      serve([items]);
      const writes: unknown[] = [];
      server.use(
        http.get('*/api/v1/banking/categories', () => HttpResponse.json([
          { id: 'food', parentId: null, key: 'food', name: 'Alimentation', kind: 'expense', archived: false, mergedIntoId: null, defaultName: null, lines: 0 },
        ])),
        http.post('*/api/v1/banking/transactions/category', async ({ request }) => {
          const body = await request.json() as { transactionIds: string[] };
          writes.push(body);
          return HttpResponse.json(answerWith(body.transactionIds));
        }),
        http.post('*/api/v1/banking/category-rules/preview', () => HttpResponse.json({ total: 0, items: [], conflicts: [], tooBroad: false })),
      );
      return writes;
    }

    it('selects the lines a writer may categorise, and categorises them in one call', async () => {
      const writes = serveBulk([
        line({ id: 'a', label: 'CB CARREFOUR CITY 12', suggestedPattern: 'CARREFOUR CITY' }),
        line({ id: 'b', label: 'CB MONOPRIX', suggestedPattern: 'MONOPRIX' }),
        line({ id: 'c', label: 'CB IGNOREE', linkState: 'ignored' }),
        line({ id: 'd', label: 'CB DIVERS', categorySource: 'split', parts: [{ id: 'p1', amount: '-10.00', category: null, note: null }] }),
      ], (ids) => ({ changed: ids, refused: [] }));
      const { user } = await renderApp(`/investments/${PEA}?tab=operations`);
      await screen.findByRole('table', { name: 'Opérations' });

      expect(box('CB IGNOREE')).toBeDisabled();
      expect(box('CB DIVERS')).toBeDisabled();
      expect(screen.queryByRole('button', { name: /^Catégoriser…/ })).toBeNull();
      await user.click(all());
      expect(box('CB IGNOREE')).not.toBeChecked();
      await user.click(screen.getByRole('button', { name: 'Catégoriser… (2)' }));
      const dialog = await screen.findByRole('dialog', { name: 'Catégoriser 2 opérations' });
      await pick(user, 'Catégorie', 'Alimentation');
      await user.click(within(dialog).getByRole('button', { name: 'Enregistrer' }));

      await waitFor(() => expect(writes).toEqual([{ transactionIds: ['a', 'b'], categoryId: 'food' }]));
      expect(await screen.findByText('2 opérations classées.')).toBeInTheDocument();
      expect(screen.queryByRole('dialog')).toBeNull();
      expect(screen.queryByRole('button', { name: /^Catégoriser…/ })).toBeNull();
      expect(box('CB MONOPRIX')).not.toBeChecked();
    });

    it('offers the rule when the changed lines share their suggested pattern', async () => {
      serveBulk([
        line({ id: 'a', label: 'CB CARREFOUR CITY 12', suggestedPattern: 'CARREFOUR CITY' }),
        line({ id: 'b', label: 'CB CARREFOUR CITY 34', suggestedPattern: 'CARREFOUR CITY' }),
      ], (ids) => ({ changed: ids, refused: [] }));
      const { user } = await renderApp(`/investments/${PEA}?tab=operations`);
      await screen.findByRole('table', { name: 'Opérations' });
      await user.click(all());
      await user.click(screen.getByRole('button', { name: 'Catégoriser… (2)' }));
      const dialog = await screen.findByRole('dialog', { name: 'Catégoriser 2 opérations' });
      await pick(user, 'Catégorie', 'Alimentation');
      await user.click(within(dialog).getByRole('button', { name: 'Enregistrer' }));

      expect(await within(dialog).findByText('Toujours classer « CARREFOUR CITY » en Alimentation ?')).toBeInTheDocument();
      expect(within(dialog).getByRole('textbox', { name: 'Motif' })).toHaveValue('CARREFOUR CITY');
    });

    it('shows no selection to a reader', async () => {
      serve([[line()]], detail({ kind: 'checking', canWrite: false }));
      await renderApp(`/investments/${PEA}?tab=operations`);

      await screen.findByRole('table', { name: 'Opérations' });
      expect(screen.queryByRole('checkbox')).toBeNull();
    });
  });
});
