import { File as NodeFile } from 'node:buffer';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { afterAll, beforeAll, describe, expect, it, vi } from 'vitest';
import userEvent from '@testing-library/user-event';
import { pick } from '@/test/controls';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';

const base = { bookedOn: '2026-10-27', valueOn: null, currency: 'EUR', counterparty: null, fromCard: false, cardName: null, fetchedVia: 'file', link: null, accountName: 'Compte courant', accountId: 'a1', sharedAccount: false };
const netflix = { ...base, id: 't1', amount: '-13.49', label: 'PRLV NETFLIX', linkState: 'unlinked', category: null };
const salary = { ...base, id: 't2', bookedOn: '2026-10-28', amount: '2000.00', label: 'VIREMENT EMPLOYEUR', linkState: 'linked', link: { itemName: 'Salaire Alice' }, category: { id: 'c1', name: 'Revenus' } };
const facets = { accounts: [{ id: 'a1', name: 'Compte courant' }, { id: 'a2', name: 'Livret' }], owners: [{ id: 'p1', name: 'Alice' }], categories: [{ id: 'c1', name: 'Revenus' }] };
const answer = (items: unknown[], total = items.length) => ({ items, total, totals: { out: '13.49', in: '2000.00', net: '1986.51', currency: 'EUR', incomplete: false }, facets,
  accountOwners: [{ accountId: 'a1', owners: [{ id: 'p1', name: 'Alice' }], writable: true }] });

const search = () => screen.getByRole<HTMLInputElement>('searchbox', { name: 'Rechercher' });
const filters = () => screen.getByRole('button', { name: /^Filtres/ });

describe('« Opérations »', () => {
  beforeAll(async () => { await import('@/features/spending/SpendingPage'); await import('./OperationsPage'); }, 30_000);

  it('lists every line with the server totals as given', async () => {
    let asked = '';
    server.use(
      http.get('*/api/v1/banking/operations', ({ request }) => { asked = new URL(request.url).search; return HttpResponse.json(answer([netflix, salary])); }),
      http.get('*/api/v1/core/persons', () => HttpResponse.json([{ id: 'p1', kind: 'adult', displayName: 'Alice' }])),
    );
    await renderApp('/spending?tab=operations');

    expect(await screen.findByRole('tab', { name: 'Opérations', selected: true })).toBeInTheDocument();
    const rows = within(await screen.findByRole('table', { name: 'Opérations' })).getAllByRole('row');
    expect(rows[1]).toHaveTextContent('PRLV NETFLIX');
    expect(rows[1]).toHaveTextContent('-13,49 €');
    // « À relier », most lines' state, is left unsaid; a line without category says it is to classify.
    expect(rows[1]).not.toHaveTextContent('À relier');
    expect(rows[1]).toHaveTextContent('À classer');
    expect(await within(rows[1]!).findByText('Alice')).toBeInTheDocument();
    expect(rows[2]).toHaveTextContent('Revenus');
    expect(rows[2]).toHaveTextContent('Reliée · Salaire Alice');
    const stats = document.querySelector<HTMLElement>('[data-stat-line]')!;
    expect(within(stats).getByText('Sorties').nextElementSibling).toHaveTextContent('13,49 €');
    expect(within(stats).getByText('Entrées').nextElementSibling).toHaveTextContent('2 000,00 €');
    expect(within(stats).getByText('Solde').nextElementSibling).toHaveTextContent('1 986,51 €');
    expect(screen.getByText('2 opérations')).toBeInTheDocument();
    expect(asked).toBe('');
  });

  it('?state=unlinked asks for the lines to link and links one', async () => {
    let asked = '';
    let body: unknown;
    server.use(
      http.get('*/api/v1/banking/operations', ({ request }) => { asked = new URL(request.url).search; return HttpResponse.json(answer([netflix])); }),
      http.get('*/api/v1/banking/transactions/:id/candidates', () => HttpResponse.json([
        { module: 'subscriptions', itemId: 's1', itemName: 'Netflix', kind: 'Subscription', key: '2026-10-27', dueOn: '2026-10-27', amount: '13.49', currency: 'EUR', credit: false, gap: null },
      ])),
      http.post('*/api/v1/banking/transactions/:id/link', async ({ request }) => { body = await request.json(); return HttpResponse.json({ ...netflix, linkState: 'linked' }); }),
    );
    const { user } = await renderApp('/spending?tab=operations&state=unlinked');

    const row = within(await screen.findByRole('table', { name: 'Opérations' })).getAllByRole('row')[1]!;
    expect(asked).toBe('?state=unlinked');
    await user.click(within(row).getByRole('button', { name: /Actions/ }));
    await user.click(await screen.findByRole('menuitem', { name: 'Relier…' }));
    await screen.findByRole('combobox', { name: 'Charge attendue' });
    await pick(user, 'Charge attendue', /Netflix · 27\/10\/2026 · 13,49\s€$/);
    await user.click(screen.getByRole('button', { name: 'Relier' }));

    await waitFor(() => expect(body).toEqual({ module: 'subscriptions', itemId: 's1', key: '2026-10-27' }));
  });

  // #437: a figure's lines: those it counts, on the Foyer / Moi accounts; « Foyer » is the scope the URL leaves out.
  it.each([
    ['/spending?scope=me&tab=operations&state=counted', '?state=counted&scope=me'],
    ['/spending?tab=operations&state=counted', '?state=counted&scope=household'],
  ])('%s asks for the lines the scope counts', async (url, expected) => {
    let asked = '';
    server.use(http.get('*/api/v1/banking/operations', ({ request }) => { asked = new URL(request.url).search; return HttpResponse.json(answer([netflix])); }));
    await renderApp(url);

    await screen.findByRole('table', { name: 'Opérations' });
    expect(asked).toBe(expected);
    expect(screen.getByRole('list', { name: 'Filtres actifs' })).toHaveTextContent('Comptée dans les dépenses');
    fireEvent.click(filters());
    expect(screen.getByRole('combobox', { name: 'État' })).toHaveTextContent('Comptée dans les dépenses');
  });

  it('announces an ignored line', async () => {
    server.use(
      http.get('*/api/v1/banking/operations', () => HttpResponse.json(answer([netflix]))),
      http.post('*/api/v1/banking/transactions/:id/ignore', () => HttpResponse.json({ ...netflix, linkState: 'ignored' })),
    );
    const { user } = await renderApp('/spending?tab=operations');
    const row = within(await screen.findByRole('table', { name: 'Opérations' })).getAllByRole('row')[1]!;
    await user.click(within(row).getByRole('button', { name: /Actions/ }));
    await user.click(await screen.findByRole('menuitem', { name: 'Ignorer' }));
    expect(await screen.findByText('Opération ignorée.')).toBeInTheDocument();
  });

  it('filters and search write the URL and the request, and reset the page', async () => {
    const asked: string[] = [];
    server.use(http.get('*/api/v1/banking/operations', ({ request }) => { asked.push(new URL(request.url).search); return HttpResponse.json(answer([netflix], 120)); }));
    const { user, router } = await renderApp('/spending?tab=operations&page=2');
    await screen.findByRole('table', { name: 'Opérations' });
    expect(asked[0]).toBe('?page=2');

    await user.click(filters());
    await pick(user, 'État', 'À relier');
    await waitFor(() => expect(router.state.location.search).toEqual({ tab: 'operations', state: 'unlinked' }));
    await user.click(screen.getByRole('button', { name: 'Compte Tous les comptes' }));
    await user.click(await screen.findByRole('menuitemcheckbox', { name: 'Compte courant' }));
    await user.click(screen.getByRole('menuitemcheckbox', { name: 'Livret' }));
    await user.keyboard('{Escape}');
    await pick(user, 'Catégorie', 'Sans catégorie');
    await user.click(screen.getByRole('radio', { name: 'Sorties' }));
    fireEvent.change(screen.getByRole('searchbox', { name: 'Rechercher' }), { target: { value: 'netflix' } });

    await waitFor(() => expect(router.state.location.search).toEqual({ tab: 'operations', state: 'unlinked', account: ['a1', 'a2'], uncategorised: true, direction: 'out', q: 'netflix' }));
    await waitFor(() => {
      const last = new URLSearchParams(asked.at(-1));
      expect(last.getAll('account')).toEqual(['a1', 'a2']);
      expect(last.get('uncategorised')).toBe('true');
      expect(last.get('direction')).toBe('out');
      expect(last.get('q')).toBe('netflix');
      expect(last.get('state')).toBe('unlinked');
      expect(last.has('page')).toBe(false);
    });
  });

  it('seeds the inputs from the URL and follows it when it changes from outside', async () => {
    server.use(http.get('*/api/v1/banking/operations', () => HttpResponse.json(answer([netflix]))));
    const { router } = await renderApp('/spending?tab=operations&q=netflix&from=2026-10-01&min=5');
    const input = (name: string) => screen.getByRole<HTMLInputElement>('textbox', { name });
    await screen.findByRole('table', { name: 'Opérations' });
    expect(search().value).toBe('netflix');
    expect(within(screen.getByRole('list', { name: 'Filtres actifs' })).getAllByRole('listitem').map((i) => i.textContent)).toEqual(['Depuis le 1 oct. 2026', 'Au moins 5', 'Tout effacer']);
    fireEvent.click(filters());
    expect(input('Du').value).toBe('01/10/2026');
    expect(screen.getByRole<HTMLInputElement>('spinbutton', { name: 'Montant minimum' }).value).toBe('5');

    await router.navigate({ to: '/spending', search: { tab: 'operations', state: 'unlinked' } });
    await waitFor(() => expect(search().value).toBe(''));
    expect(input('Du').value).toBe('');
    expect(screen.getByRole<HTMLInputElement>('spinbutton', { name: 'Montant minimum' }).value).toBe('');
  });

  it('keeps a half-typed date out of the URL and waits for a pause before searching', async () => {
    const asked: string[] = [];
    server.use(http.get('*/api/v1/banking/operations', ({ request }) => { asked.push(new URL(request.url).search); return HttpResponse.json(answer([netflix])); }));
    const { router } = await renderApp('/spending?tab=operations');
    await screen.findByRole('table', { name: 'Opérations' });

    fireEvent.click(filters());
    fireEvent.change(screen.getByRole('textbox', { name: 'Du' }), { target: { value: '12/1' } });
    expect(screen.getByRole<HTMLInputElement>('textbox', { name: 'Du' }).value).toBe('12/1');
    expect(router.state.location.search).toEqual({ tab: 'operations' });

    fireEvent.change(screen.getByRole('searchbox', { name: 'Rechercher' }), { target: { value: 'ne' } });
    fireEvent.change(screen.getByRole('searchbox', { name: 'Rechercher' }), { target: { value: 'net' } });
    expect(router.state.location.search).toEqual({ tab: 'operations' });
    await waitFor(() => expect(router.state.location.search).toEqual({ tab: 'operations', q: 'net' }));
    await waitFor(() => expect(asked.at(-1)).toBe('?q=net'));
    expect(asked).not.toContain('?q=ne');
  });

  it('goes to page 2', async () => {
    const asked: string[] = [];
    server.use(http.get('*/api/v1/banking/operations', ({ request }) => { asked.push(new URL(request.url).search); return HttpResponse.json(answer([netflix], 120)); }));
    const { user, router } = await renderApp('/spending?tab=operations');
    await user.click(await screen.findByRole('button', { name: 'Suivant' }));
    await waitFor(() => expect(router.state.location.search).toEqual({ tab: 'operations', page: 2 }));
    await waitFor(() => expect(asked.at(-1)).toBe('?page=2'));
  });

  it('says so when nothing matches', async () => {
    server.use(http.get('*/api/v1/banking/operations', () => HttpResponse.json(answer([]))));
    await renderApp('/spending?tab=operations&state=unlinked');
    expect(await screen.findByText('Aucune opération à relier.')).toBeInTheDocument();
  });

  it('says no line matches when other filters narrow the list', async () => {
    server.use(http.get('*/api/v1/banking/operations', () => HttpResponse.json(answer([]))));
    await renderApp('/spending?tab=operations&state=unlinked&direction=in');
    expect(await screen.findByText('Aucune opération ne correspond.')).toBeInTheDocument();
  });

  it('drops a negative or exponent bound from the URL', async () => {
    const asked: string[] = [];
    server.use(http.get('*/api/v1/banking/operations', ({ request }) => { asked.push(new URL(request.url).search); return HttpResponse.json(answer([netflix])); }));
    await renderApp('/spending?tab=operations&min=-5&max=1e21');
    await screen.findByRole('table', { name: 'Opérations' });
    expect(asked).toEqual(['']);
  });

  it('does not search a lone letter and a space', async () => {
    const asked: string[] = [];
    server.use(http.get('*/api/v1/banking/operations', ({ request }) => { asked.push(new URL(request.url).search); return HttpResponse.json(answer([netflix])); }));
    await renderApp('/spending?tab=operations&q=a%20');
    await screen.findByRole('table', { name: 'Opérations' });
    expect(asked).toEqual(['']);
  });

  it('keeps one id of a longer category list', async () => {
    const { router } = await renderApp('/spending?tab=operations&category=c1&category=c2');
    await waitFor(() => expect(router.state.location.search).toEqual({ tab: 'operations', category: ['c1'] }));
  });
});

describe('« Opérations » receipts', () => {
  beforeAll(async () => { await import('@/features/spending/SpendingPage'); await import('./OperationsPage'); await import('@/features/documents/ItemUploadDialog'); await import('@/features/documents/ItemLinkDialog'); await import('../investments/operations/ReceiptPreview');
    // jsdom's FormData and File do not cross into Node's Request (MSW): a browser sends them as is, the tests use Node's.
    const form = await new Response('a=1', { headers: { 'content-type': 'application/x-www-form-urlencoded' } }).formData();
    vi.stubGlobal('FormData', form.constructor);
    vi.stubGlobal('File', NodeFile);
  }, 30_000);
  afterAll(() => { vi.unstubAllGlobals(); });
  const withReceipts = { ...netflix, documents: [{ id: 'd1', title: 'Facture octobre' }, { id: 'd2', title: 'Reçu' }] };
  // A submenu item is chosen from the keyboard: jsdom pointer moves close the submenu before the click lands.
  const choose = async (name: string) => { (await screen.findByRole('menuitem', { name })).focus(); await userEvent.keyboard('{Enter}'); };
  const rowOf = async () => within(await screen.findByRole('table', { name: 'Opérations' })).getAllByRole('row')[1]!;

  it('shows a paperclip with the count, lists the documents and opens one in the preview', async () => {
    server.use(
      http.get('*/api/v1/banking/operations', () => HttpResponse.json(answer([withReceipts, salary]))),
      http.get('*/api/v1/documents/:id', ({ params }) => HttpResponse.json({ document: { id: params.id, title: 'Facture octobre', mediaType: 'application/pdf', links: [], archived: false, canEdit: true }, links: [] })),
    );
    const { user } = await renderApp('/spending?tab=operations');
    const row = await rowOf();
    expect(within(screen.getAllByRole('table')[0]!).getAllByRole('button', { name: /justificatif/ })).toHaveLength(1);
    await user.click(within(row).getByRole('button', { name: '2 justificatifs' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Facture octobre' }));
    expect(await screen.findByRole('dialog', { name: 'Facture octobre' })).toBeInTheDocument();
  });

  it('« Joindre un justificatif » uploads onto the line with the receipt role', async () => {
    let body: Record<string, FormDataEntryValue> = {};
    server.use(
      http.get('*/api/v1/banking/operations', () => HttpResponse.json(answer([netflix]))),
      http.post('*/api/v1/documents', async ({ request }) => { body = Object.fromEntries((await request.formData()).entries()); return HttpResponse.json({ id: 'd9' }, { status: 201 }); }),
    );
    const { user } = await renderApp('/spending?tab=operations');
    await user.click(within(await rowOf()).getByRole('button', { name: /Actions/ }));
    await user.click(await screen.findByRole('menuitem', { name: 'Joindre un justificatif' }));
    await choose('Déposer un fichier…');
    const dialog = await screen.findByRole('dialog');
    await user.upload(within(dialog).getByLabelText('Fichiers'), new File(['%PDF-1.7'], 'facture.pdf', { type: 'application/pdf' }));
    expect(within(dialog).getByRole('combobox', { name: 'Type' })).toHaveTextContent('Justificatif');
    await user.click(within(dialog).getByRole('button', { name: 'Déposer' }));
    await waitFor(() => expect(body).toMatchObject({ targetKind: 'banking.transaction', targetId: 't1', role: 'receipt' }));
  });

  it('« Choisir un document… » links an existing document to the line', async () => {
    let body: unknown;
    server.use(
      http.get('*/api/v1/banking/operations', () => HttpResponse.json(answer([netflix]))),
      http.get('*/api/v1/documents', () => HttpResponse.json({ items: [{ id: 'd3', title: 'Ticket', mediaType: 'application/pdf', archived: false, canEdit: true, links: [] }], total: 1, facets: {} })),
      http.get('*/api/v1/documents/:id/links/preview', () => HttpResponse.json({ gainedReaders: [] })),
      http.post('*/api/v1/documents/:id/links', async ({ request }) => { body = await request.json(); return new HttpResponse(null, { status: 204 }); }),
    );
    const { user } = await renderApp('/spending?tab=operations');
    await user.click(within(await rowOf()).getByRole('button', { name: /Actions/ }));
    await user.click(await screen.findByRole('menuitem', { name: 'Joindre un justificatif' }));
    await choose('Choisir un document…');
    await screen.findByRole('dialog');
    await pick(user, 'Document', 'Ticket');
    expect(screen.getByRole('combobox', { name: 'Type' })).toHaveTextContent('Justificatif');
    await user.click(screen.getByRole('button', { name: 'Relier' }));
    await waitFor(() => expect(body).toEqual({ targetKind: 'banking.transaction', targetId: 't1', role: 'receipt' }));
  });

  it('a receipt gone since the list was read closes with a toast, and the paperclip still answers', async () => {
    server.use(
      http.get('*/api/v1/banking/operations', () => HttpResponse.json(answer([withReceipts]))),
      http.get('*/api/v1/documents/:id', ({ params }) => params.id === 'd2'
        ? HttpResponse.json({ status: 404, code: 'not_found' }, { status: 404, headers: { 'content-type': 'application/problem+json' } })
        : HttpResponse.json({ status: 500 }, { status: 500, headers: { 'content-type': 'application/problem+json' } })),
    );
    const { user } = await renderApp('/spending?tab=operations');
    await user.click(within(await rowOf()).getByRole('button', { name: '2 justificatifs' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Reçu' }));
    expect(await screen.findByText("Ce justificatif n'est plus disponible.")).toBeInTheDocument();
    // Another failure says so as any other does, and closes too.
    await user.click(within(await rowOf()).getByRole('button', { name: '2 justificatifs' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Facture octobre' }));
    expect(await screen.findByText('Une erreur inattendue est survenue. Réessayez dans un instant.')).toBeInTheDocument();
    await user.click(within(await rowOf()).getByRole('button', { name: '2 justificatifs' }));
    expect(await screen.findByRole('menuitem', { name: 'Reçu' })).toBeInTheDocument();
  });

  it('closing the upload dialog reads the lines again, so a receipt kept before closing shows', async () => {
    let reads = 0;
    server.use(http.get('*/api/v1/banking/operations', () => { reads += 1; return HttpResponse.json(answer([netflix])); }));
    const { user } = await renderApp('/spending?tab=operations');
    await user.click(within(await rowOf()).getByRole('button', { name: /Actions/ }));
    await user.click(await screen.findByRole('menuitem', { name: 'Joindre un justificatif' }));
    await choose('Déposer un fichier…');
    await screen.findByRole('dialog');
    const before = reads;
    await user.keyboard('{Escape}');
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    await waitFor(() => expect(reads).toBeGreaterThan(before));
  });

  it('a read-only account has the paperclip but no attach entry', async () => {
    server.use(http.get('*/api/v1/banking/operations', () => HttpResponse.json({ ...answer([withReceipts]), accountOwners: [{ accountId: 'a1', owners: [], writable: false }] })));
    await renderApp('/spending?tab=operations');
    expect(within(await rowOf()).getByRole('button', { name: '2 justificatifs' })).toBeInTheDocument();
    expect(within(await rowOf()).queryByRole('button', { name: /Actions/ })).not.toBeInTheDocument();
  });
});

describe('« Opérations », several lines at once (#426)', () => {
  beforeAll(async () => {
    await import('@/features/spending/SpendingPage'); await import('./OperationsPage'); await import('../investments/operations/CategoryDialog');
  }, 30_000);

  const shop = (id: string, label = 'CB CARREFOUR CITY 12') => ({ ...netflix, id, label, amount: '-20.00', suggestedPattern: 'CARREFOUR CITY', categorySource: null });
  const split = { ...shop('t5', 'CB DIVERS'), categorySource: 'split', parts: [{ id: 'p1', amount: '-10.00', category: null, note: null }, { id: 'p2', amount: '-10.00', category: null, note: null }] };
  const ignored = { ...shop('t6', 'CB IGNOREE'), linkState: 'ignored' };
  // The table's; the phone list has its own.
  const table = () => screen.getByRole('table', { name: 'Opérations' });
  const box = (label: string) => within(table()).getByRole('checkbox', { name: `Sélectionner « ${label} »` });
  const all = () => within(table()).getByRole('checkbox', { name: 'Sélectionner les opérations de la page' });

  /** The page's lines, the categories, and every bulk write. */
  function serve(items: unknown[], answerWith: (ids: string[]) => { changed: unknown[]; refused: unknown[] }, total = items.length) {
    const writes: unknown[] = [];
    server.use(
      http.get('*/api/v1/banking/operations', () => HttpResponse.json(answer(items, total))),
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

  async function categoriseAll(count: number) {
    const { user } = await renderApp('/spending?tab=operations');
    await screen.findByRole('table', { name: 'Opérations' });
    await user.click(all());
    await user.click(screen.getByRole('button', { name: `Catégoriser… (${count})` }));
    return { user, dialog: await screen.findByRole('dialog', { name: `Catégoriser ${count} opérations` }) };
  }

  it('selects the lines a person may categorise, the page at once, and starts again on another page', async () => {
    serve([shop('t3'), split, ignored, shop('t4', 'CB MONOPRIX')], () => ({ changed: [], refused: [] }), 120);
    const { user } = await renderApp('/spending?tab=operations');
    await screen.findByRole('table', { name: 'Opérations' });

    // A split line and an ignored one take no category of their own.
    expect(box('CB DIVERS')).toBeDisabled();
    expect(box('CB IGNOREE')).toBeDisabled();
    expect(screen.queryByRole('button', { name: /^Catégoriser…/ })).toBeNull();
    await user.click(box('CB CARREFOUR CITY 12'));
    expect(all()).toHaveAttribute('data-state', 'indeterminate');
    expect(screen.getByRole('button', { name: 'Catégoriser… (1)' })).toBeInTheDocument();
    await user.click(all());
    expect(box('CB MONOPRIX')).toBeChecked();
    expect(all()).toBeChecked();
    expect(screen.getByRole('button', { name: 'Catégoriser… (2)' })).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Suivant' }));
    await waitFor(() => expect(screen.queryByRole('button', { name: /^Catégoriser…/ })).toBeNull());
    expect(box('CB MONOPRIX')).not.toBeChecked();
  });

  it('categorises the selected lines in one call, and names the refused ones in the message', async () => {
    const writes = serve([shop('t3'), shop('t4', 'CB MONOPRIX')], (ids) => ({ changed: [ids[0]], refused: [{ id: ids[1], code: 'banking.line_split' }] }));
    const { user, dialog } = await categoriseAll(2);
    await pick(user, 'Catégorie', 'Sans catégorie');
    await user.click(within(dialog).getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(writes).toEqual([{ transactionIds: ['t3', 't4'], categoryId: null }]));
    expect(await screen.findByText(
      '1 opération classée. 1 opération non classée : « CB MONOPRIX » : Cette opération est découpée : classez ses parties, ou annulez le découpage.',
    )).toBeInTheDocument();
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(screen.queryByRole('button', { name: /^Catégoriser…/ })).toBeNull();
  });

  it('offers no « Laisser Nid décider » for several lines', async () => {
    serve([shop('t3'), shop('t4', 'CB MONOPRIX')], () => ({ changed: [], refused: [] }));
    const { user, dialog } = await categoriseAll(2);
    await user.click(within(dialog).getByRole('combobox', { name: 'Catégorie' }));
    expect(await screen.findByRole('option', { name: 'Sans catégorie' })).toBeInTheDocument();
    expect(screen.queryByRole('option', { name: 'Laisser Nid décider' })).toBeNull();
  });

  it('offers the rule when the changed lines share their suggested pattern', async () => {
    serve([shop('t3'), shop('t4', 'CB CARREFOUR CITY 34')], (ids) => ({ changed: ids, refused: [] }));
    const { user } = await categoriseAll(2);
    await pick(user, 'Catégorie', 'Alimentation');
    await user.click(screen.getByRole('button', { name: 'Enregistrer' }));

    const offer = await screen.findByRole('form', { name: 'Toujours classer « CARREFOUR CITY » en Alimentation ?' });
    expect(within(offer).getByRole('textbox', { name: 'Motif' })).toHaveValue('CARREFOUR CITY');
    await user.click(within(offer).getByRole('button', { name: 'Non merci' }));
    expect(await screen.findByText('Catégorie enregistrée.')).toBeInTheDocument();
  });

  it('closes without a rule when the lines suggest different patterns', async () => {
    serve([shop('t3'), { ...shop('t4', 'CB MONOPRIX'), suggestedPattern: 'MONOPRIX' }], (ids) => ({ changed: ids, refused: [] }));
    const { user } = await categoriseAll(2);
    await pick(user, 'Catégorie', 'Alimentation');
    await user.click(screen.getByRole('button', { name: 'Enregistrer' }));

    expect(await screen.findByText('2 opérations classées.')).toBeInTheDocument();
    expect(screen.queryByRole('form', { name: /Toujours classer/ })).toBeNull();
  });
});
