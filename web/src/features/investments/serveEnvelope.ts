import { http, HttpResponse } from 'msw/http';
import { server } from '@/test/server';
import type { EnvelopeDetail } from '@/api/investments';

export type Call = { method: string; path: string; body?: unknown };

/**
 * Mocks the server's real behaviour, not just an id echo: a value on an already-known date replaces that row (a
 * new id), a flow correction supersedes the old one (a new id, the server's `SupersedeAsync`), and a deletion
 * removes the row — so a test can assert on the table and the toasts after the refetch, the way the
 * real page reacts.
 */
export function serve(current: EnvelopeDetail, calls: Call[]) {
  let state = current;
  server.use(
    http.get('*/api/v1/investments/envelopes/:id', () => HttpResponse.json(state)),
    http.post('*/api/v1/investments/envelopes/:id/valuations', async ({ request }) => {
      const body = await request.json() as { asOf: string; amount: { amount: string; currency: string }; note?: string | null };
      calls.push({ method: 'POST', path: 'valuations', body });
      state = { ...state, valuations: [...state.valuations.filter((v) => v.asOf !== body.asOf), { id: 'new-value', asOf: body.asOf, amount: body.amount, note: body.note ?? null }] };
      return HttpResponse.json({ id: 'new-value' }, { status: 201 });
    }),
    http.post('*/api/v1/investments/envelopes/:id/flows', async ({ request }) => {
      const body = await request.json() as { occurredOn: string; kind: string; amount: { amount: string; currency: string }; note?: string | null };
      calls.push({ method: 'POST', path: 'flows', body });
      state = { ...state, flows: [...state.flows, { id: 'new-flow', occurredOn: body.occurredOn, kind: body.kind, amount: body.amount, category: null, externalRef: null, note: body.note ?? null }] };
      return HttpResponse.json({ id: 'new-flow' }, { status: 201 });
    }),
    http.put('*/api/v1/investments/envelopes/:id/flows/:flowId', async ({ request, params }) => {
      const body = await request.json() as { occurredOn: string; kind: string; amount: { amount: string; currency: string }; note?: string | null };
      calls.push({ method: 'PUT', path: `flows/${String(params.flowId)}`, body });
      state = {
        ...state,
        flows: [
          ...state.flows.filter((f) => f.id !== params.flowId),
          { id: 'superseded-flow', occurredOn: body.occurredOn, kind: body.kind, amount: body.amount, category: null, externalRef: null, note: body.note ?? null },
        ],
      };
      return HttpResponse.json({ id: 'superseded-flow' });
    }),
    http.delete('*/api/v1/investments/envelopes/:id/flows/:flowId', ({ params }) => {
      calls.push({ method: 'DELETE', path: `flows/${String(params.flowId)}` });
      state = { ...state, flows: state.flows.filter((f) => f.id !== params.flowId) };
      return HttpResponse.json({ id: String(params.flowId) });
    }),
    http.delete('*/api/v1/investments/envelopes/:id/valuations/:valuationId', ({ params }) => {
      calls.push({ method: 'DELETE', path: `valuations/${String(params.valuationId)}` });
      state = { ...state, valuations: state.valuations.filter((v) => v.id !== params.valuationId) };
      return HttpResponse.json({ id: String(params.valuationId) });
    }),
  );
}

