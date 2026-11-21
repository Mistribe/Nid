import { screen } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { detail, PEA } from '../testData';

describe('an SCPI\'s loan (#180)', () => {
  beforeAll(async () => { await import('../EnvelopeDetailPage'); }, 30_000);

  it('lists the loans the server says finance it, with their outstanding capital', async () => {
    let asked: string | null = null;
    server.use(
      http.get('*/api/v1/investments/envelopes/:id', () => HttpResponse.json(detail({ kind: 'scpi' }))),
      http.get('*/api/v1/realestate/loans', ({ request }) => {
        asked = new URL(request.url).searchParams.get('envelope');
        return HttpResponse.json({ items: [{ id: 'l1', name: 'Prêt SCPI', currency: 'EUR', remainingBalance: '12000.00' }] });
      }),
    );
    await renderApp(`/investments/${PEA}`);

    expect(await screen.findByRole('link', { name: /Prêt SCPI/ })).toHaveTextContent(/Capital restant dû : 12\s000,00\s€/);
    expect(asked).toBe(PEA);
  });

  it('says how to link one when none does', async () => {
    server.use(
      http.get('*/api/v1/investments/envelopes/:id', () => HttpResponse.json(detail({ kind: 'scpi' }))),
      http.get('*/api/v1/realestate/loans', () => HttpResponse.json({ items: [] })),
    );
    await renderApp(`/investments/${PEA}`);
    expect(await screen.findByText(/Aucun crédit ne finance cette SCPI/)).toBeInTheDocument();
  });
});
