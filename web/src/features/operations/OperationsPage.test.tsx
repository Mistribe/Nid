import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { pick } from '@/test/controls';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';

const base = { bookedOn: '2026-10-27', valueOn: null, currency: 'EUR', counterparty: null, fromCard: false, cardName: null, fetchedVia: 'file', link: null, accountName: 'Compte courant', accountId: 'a1', sharedAccount: false };
const netflix = { ...base, id: 't1', amount: '-13.49', label: 'PRLV NETFLIX', linkState: 'unlinked', category: null };
const salary = { ...base, id: 't2', bookedOn: '2026-10-28', amount: '2000.00', label: 'VIREMENT EMPLOYEUR', linkState: 'linked', link: { itemName: 'Salaire Alice' }, category: { id: 'c1', name: 'Revenus' } };
const facets = { accounts: [{ id: 'a1', name: 'Compte courant' }], owners: [{ id: 'p1', name: 'Alice' }], categories: [{ id: 'c1', name: 'Revenus' }] };
const answer = (items: unknown[], total = items.length) => ({ items, total, totals: { out: '13.49', in: '2000.00', net: '1986.51', currency: 'EUR', incomplete: false }, facets,
  accountOwners: [{ accountId: 'a1', owners: [{ id: 'p1', name: 'Alice' }], writable: true }] });

const search = () => screen.getByRole<HTMLInputElement>('searchbox', { name: 'Rechercher' });

describe('« Opérations »', () => {
  beforeAll(async () => { await import('./OperationsPage'); }, 30_000);

  it('lists every line with the server totals as given', async () => {
    let asked = '';
    server.use(
      http.get('*/api/v1/banking/operations', ({ request }) => { asked = new URL(request.url).search; return HttpResponse.json(answer([netflix, salary])); }),
      http.get('*/api/v1/core/persons', () => HttpResponse.json([{ id: 'p1', kind: 'adult', displayName: 'Alice' }])),
    );
    await renderApp('/operations');

    expect(await screen.findByRole('heading', { name: 'Opérations' })).toBeInTheDocument();
    const rows = within(await screen.findByRole('table', { name: 'Opérations' })).getAllByRole('row');
    expect(rows[1]).toHaveTextContent('PRLV NETFLIX');
    expect(rows[1]).toHaveTextContent('-13,49 €');
    expect(rows[1]).toHaveTextContent('À relier');
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
    const { user } = await renderApp('/operations?state=unlinked');

    const row = within(await screen.findByRole('table', { name: 'Opérations' })).getAllByRole('row')[1]!;
    expect(asked).toBe('?state=unlinked');
    await user.click(within(row).getByRole('button', { name: /Actions/ }));
    await user.click(await screen.findByRole('menuitem', { name: 'Relier…' }));
    await screen.findByRole('combobox', { name: 'Charge attendue' });
    await pick(user, 'Charge attendue', /Netflix · 27\/10\/2026 · 13,49\s€$/);
    await user.click(screen.getByRole('button', { name: 'Relier' }));

    await waitFor(() => expect(body).toEqual({ module: 'subscriptions', itemId: 's1', key: '2026-10-27' }));
  });

  it('announces an ignored line', async () => {
    server.use(
      http.get('*/api/v1/banking/operations', () => HttpResponse.json(answer([netflix]))),
      http.post('*/api/v1/banking/transactions/:id/ignore', () => HttpResponse.json({ ...netflix, linkState: 'ignored' })),
    );
    const { user } = await renderApp('/operations');
    const row = within(await screen.findByRole('table', { name: 'Opérations' })).getAllByRole('row')[1]!;
    await user.click(within(row).getByRole('button', { name: /Actions/ }));
    await user.click(await screen.findByRole('menuitem', { name: 'Ignorer' }));
    expect(await screen.findByText('Opération ignorée.')).toBeInTheDocument();
  });

  it('filters and search write the URL and the request, and reset the page', async () => {
    const asked: string[] = [];
    server.use(http.get('*/api/v1/banking/operations', ({ request }) => { asked.push(new URL(request.url).search); return HttpResponse.json(answer([netflix], 120)); }));
    const { user, router } = await renderApp('/operations?page=2');
    await screen.findByRole('table', { name: 'Opérations' });
    expect(asked[0]).toBe('?page=2');

    await pick(user, 'État', 'À relier');
    await waitFor(() => expect(router.state.location.search).toEqual({ state: 'unlinked' }));
    await pick(user, 'Compte', 'Compte courant');
    await pick(user, 'Catégorie', 'Sans catégorie');
    await user.click(screen.getByRole('radio', { name: 'Sorties' }));
    fireEvent.change(screen.getByRole('searchbox', { name: 'Rechercher' }), { target: { value: 'netflix' } });

    await waitFor(() => expect(router.state.location.search).toEqual({ state: 'unlinked', account: ['a1'], uncategorised: true, direction: 'out', q: 'netflix' }));
    await waitFor(() => {
      const last = new URLSearchParams(asked.at(-1));
      expect(last.getAll('account')).toEqual(['a1']);
      expect(last.get('uncategorised')).toBe('true');
      expect(last.get('direction')).toBe('out');
      expect(last.get('q')).toBe('netflix');
      expect(last.get('state')).toBe('unlinked');
      expect(last.has('page')).toBe(false);
    });
  });

  it('seeds the inputs from the URL and follows it when it changes from outside', async () => {
    server.use(http.get('*/api/v1/banking/operations', () => HttpResponse.json(answer([netflix]))));
    const { router } = await renderApp('/operations?q=netflix&from=2026-10-01&min=5');
    const input = (name: string) => screen.getByRole<HTMLInputElement>('textbox', { name });
    await screen.findByRole('table', { name: 'Opérations' });
    expect(search().value).toBe('netflix');
    expect(input('Du').value).toBe('01/10/2026');
    expect(screen.getByRole<HTMLInputElement>('spinbutton', { name: 'Montant de' }).value).toBe('5');

    await router.navigate({ to: '/operations', search: { state: 'unlinked' } });
    await waitFor(() => expect(search().value).toBe(''));
    expect(input('Du').value).toBe('');
    expect(screen.getByRole<HTMLInputElement>('spinbutton', { name: 'Montant de' }).value).toBe('');
  });

  it('keeps a half-typed date out of the URL and waits for a pause before searching', async () => {
    const asked: string[] = [];
    server.use(http.get('*/api/v1/banking/operations', ({ request }) => { asked.push(new URL(request.url).search); return HttpResponse.json(answer([netflix])); }));
    const { router } = await renderApp('/operations');
    await screen.findByRole('table', { name: 'Opérations' });

    fireEvent.change(screen.getByRole('textbox', { name: 'Du' }), { target: { value: '12/1' } });
    expect(screen.getByRole<HTMLInputElement>('textbox', { name: 'Du' }).value).toBe('12/1');
    expect(router.state.location.search).toEqual({});

    fireEvent.change(screen.getByRole('searchbox', { name: 'Rechercher' }), { target: { value: 'ne' } });
    fireEvent.change(screen.getByRole('searchbox', { name: 'Rechercher' }), { target: { value: 'net' } });
    expect(router.state.location.search).toEqual({});
    await waitFor(() => expect(router.state.location.search).toEqual({ q: 'net' }));
    await waitFor(() => expect(asked.at(-1)).toBe('?q=net'));
    expect(asked).not.toContain('?q=ne');
  });

  it('goes to page 2', async () => {
    const asked: string[] = [];
    server.use(http.get('*/api/v1/banking/operations', ({ request }) => { asked.push(new URL(request.url).search); return HttpResponse.json(answer([netflix], 120)); }));
    const { user, router } = await renderApp('/operations');
    await user.click(await screen.findByRole('button', { name: 'Suivant' }));
    await waitFor(() => expect(router.state.location.search).toEqual({ page: 2 }));
    await waitFor(() => expect(asked.at(-1)).toBe('?page=2'));
  });

  it('says so when nothing matches', async () => {
    server.use(http.get('*/api/v1/banking/operations', () => HttpResponse.json(answer([]))));
    await renderApp('/operations?state=unlinked');
    expect(await screen.findByText('Aucune opération à relier.')).toBeInTheDocument();
  });

  it('says no line matches when other filters narrow the list', async () => {
    server.use(http.get('*/api/v1/banking/operations', () => HttpResponse.json(answer([]))));
    await renderApp('/operations?state=unlinked&direction=in');
    expect(await screen.findByText('Aucune opération ne correspond.')).toBeInTheDocument();
  });

  it('drops a negative or exponent bound from the URL', async () => {
    const asked: string[] = [];
    server.use(http.get('*/api/v1/banking/operations', ({ request }) => { asked.push(new URL(request.url).search); return HttpResponse.json(answer([netflix])); }));
    await renderApp('/operations?min=-5&max=1e21');
    await screen.findByRole('table', { name: 'Opérations' });
    expect(asked).toEqual(['']);
  });

  it('does not search a lone letter and a space', async () => {
    const asked: string[] = [];
    server.use(http.get('*/api/v1/banking/operations', ({ request }) => { asked.push(new URL(request.url).search); return HttpResponse.json(answer([netflix])); }));
    await renderApp('/operations?q=a%20');
    await screen.findByRole('table', { name: 'Opérations' });
    expect(asked).toEqual(['']);
  });

  it('keeps one id of a longer category list', async () => {
    const { router } = await renderApp('/operations?category=c1&category=c2');
    await waitFor(() => expect(router.state.location.search).toEqual({ category: ['c1'] }));
  });
});
