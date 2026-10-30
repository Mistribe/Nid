import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it } from 'vitest';
import { pick } from '@/test/controls';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';

const line = { id: 't1', bookedOn: '2026-10-27', valueOn: null, amount: '-13.49', currency: 'EUR', label: 'PRLV NETFLIX', counterparty: null, fromCard: false, cardName: null, fetchedVia: 'file', linkState: 'unlinked', link: null, accountName: 'Compte courant' };

describe('« Opérations à relier »', () => {
  beforeAll(async () => { await import('./UnlinkedPage'); }, 30_000);

  it('the page lists unlinked lines and links one', async () => {
    let asked = '';
    let body: unknown;
    server.use(
      http.get('*/api/v1/banking/transactions', ({ request }) => { asked = new URL(request.url).search; return HttpResponse.json({ items: [line], hasMore: false }); }),
      http.get('*/api/v1/banking/transactions/:id/candidates', () => HttpResponse.json([
        { module: 'subscriptions', itemId: 's1', itemName: 'Netflix', kind: 'Subscription', key: '2026-10-27', dueOn: '2026-10-27', amount: '13.49', currency: 'EUR', credit: false, gap: null },
      ])),
      http.post('*/api/v1/banking/transactions/:id/link', async ({ request }) => { body = await request.json(); return HttpResponse.json({ ...line, linkState: 'linked' }); }),
    );
    const { user } = await renderApp('/operations');

    expect(await screen.findByRole('heading', { name: 'Opérations à relier' })).toBeInTheDocument();
    const row = within(await screen.findByRole('table', { name: 'Opérations à relier' })).getAllByRole('row')[1]!;
    expect(row).toHaveTextContent('Compte courant');
    expect(row).toHaveTextContent('PRLV NETFLIX');
    expect(row).toHaveTextContent('-13,49 €');
    expect(asked).not.toContain('state=');

    await user.click(within(row).getByRole('button', { name: /Actions/ }));
    await user.click(await screen.findByRole('menuitem', { name: 'Relier…' }));
    await screen.findByRole('combobox', { name: 'Charge attendue' });
    await pick(user, 'Charge attendue', /Netflix · 27\/10\/2026 · 13,49\s€$/);
    await user.click(screen.getByRole('button', { name: 'Relier' }));

    await waitFor(() => expect(body).toEqual({ module: 'subscriptions', itemId: 's1', key: '2026-10-27' }));
  });

  it('announces an ignored line that leaves the list', async () => {
    let ignored = false;
    server.use(
      http.get('*/api/v1/banking/transactions', () => HttpResponse.json({ items: ignored ? [] : [line], hasMore: false })),
      http.post('*/api/v1/banking/transactions/:id/ignore', () => { ignored = true; return HttpResponse.json({ ...line, linkState: 'ignored' }); }),
    );
    const { user } = await renderApp('/operations');

    const row = within(await screen.findByRole('table', { name: 'Opérations à relier' })).getAllByRole('row')[1]!;
    await user.click(within(row).getByRole('button', { name: /Actions/ }));
    await user.click(await screen.findByRole('menuitem', { name: 'Ignorer' }));

    expect(await screen.findByText('Opération ignorée.')).toBeInTheDocument();
    expect(await screen.findByText('Aucune opération à relier.')).toBeInTheDocument();
  });
});
