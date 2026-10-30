import { screen } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it } from 'vitest';
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
