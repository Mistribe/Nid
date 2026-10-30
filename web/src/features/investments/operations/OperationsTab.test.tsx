import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { EnvelopeDetail } from '../api';
import { detail, PEA } from '../testData';
import type { BankTransaction } from './api';

const line = (over: Partial<BankTransaction> = {}): BankTransaction => ({
  id: 't1', bookedOn: '2026-10-27', valueOn: null, amount: '2450.00', currency: 'EUR', label: 'VIR SEPA SOCIETE EXEMPLE SAS',
  counterparty: 'Société Exemple', fromCard: false, cardName: null, fetchedVia: 'file', ...over,
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
});
