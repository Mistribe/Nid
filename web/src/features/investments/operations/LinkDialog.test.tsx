import { screen, waitFor } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { pick } from '@/test/controls';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { detail, PEA } from '../testData';

describe('« Relier »', () => {
  beforeAll(async () => { await import('../EnvelopeDetailPage'); await import('./OperationsTab'); }, 30_000);

  it('says so when no expected charge is close, and cannot be submitted', async () => {
    server.use(
      http.get('*/api/v1/investments/envelopes/:id', () => HttpResponse.json(detail({ kind: 'checking' }))),
      http.get('*/api/v1/banking/status', () => HttpResponse.json({ enabled: true, configured: false })),
      http.get('*/api/v1/banking/envelopes/:id/transactions', () => HttpResponse.json({
        items: [{ id: 't1', bookedOn: '2026-10-27', valueOn: null, amount: '-9.99', currency: 'EUR', label: 'PRLV X', counterparty: null, fromCard: false, cardName: null, fetchedVia: 'file', linkState: 'unlinked', link: null, accountName: 'Compte' }],
        hasMore: false,
      })),
      http.get('*/api/v1/banking/transactions/:id/candidates', () => HttpResponse.json([])),
    );
    const { user } = await renderApp(`/investments/${PEA}?tab=operations`);

    await user.click((await screen.findAllByRole('button', { name: /Actions/ }))[0]!);
    await user.click(await screen.findByRole('menuitem', { name: 'Relier…' }));

    expect(await screen.findByText('Aucune charge attendue proche')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Relier' })).toBeDisabled();
  });
});

describe('« Relier » a vehicle', () => {
  beforeAll(async () => { await import('../EnvelopeDetailPage'); await import('./OperationsTab'); }, 30_000);

  it('names the vehicle alone and sends the cost category picked', async () => {
    const linked: unknown[] = [];
    server.use(
      http.get('*/api/v1/investments/envelopes/:id', () => HttpResponse.json(detail({ kind: 'checking' }))),
      http.get('*/api/v1/banking/status', () => HttpResponse.json({ enabled: true, configured: false })),
      http.get('*/api/v1/banking/envelopes/:id/transactions', () => HttpResponse.json({
        items: [{ id: 't1', bookedOn: '2026-10-27', valueOn: null, amount: '-85.40', currency: 'EUR', label: 'CB GARAGE', counterparty: null, fromCard: false, cardName: null, fetchedVia: 'file', linkState: 'unlinked', link: null, accountName: 'Compte' }],
        hasMore: false,
      })),
      http.get('*/api/v1/banking/transactions/:id/candidates', () => HttpResponse.json([
        { module: 'assets', itemId: 'v1', itemName: 'Citadine', kind: 'vehicle_cost', key: 'cost', dueOn: '2026-10-27', amount: '85.40', currency: 'EUR', credit: false, gap: null },
      ])),
      http.post('*/api/v1/banking/transactions/:id/link', async ({ request }) => { linked.push(await request.json()); return HttpResponse.json({ id: 't1' }); }),
    );
    const { user } = await renderApp(`/investments/${PEA}?tab=operations`);

    await user.click((await screen.findAllByRole('button', { name: /Actions/ }))[0]!);
    await user.click(await screen.findByRole('menuitem', { name: 'Relier…' }));
    await pick(user, 'Charge attendue', 'Citadine');
    await pick(user, 'Type de frais', 'Pneus');
    await user.click(screen.getByRole('button', { name: 'Relier' }));

    await waitFor(() => expect(linked).toEqual([{ module: 'assets', itemId: 'v1', key: 'cost', category: 'tyres' }]));
  });
});
