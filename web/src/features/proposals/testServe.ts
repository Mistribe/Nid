import { HttpResponse, http } from 'msw/http';
import { server } from '@/test/server';
import type { ProposalView } from '@/api/proposals';

export const preview = {
  current: null,
  previous: { asOf: '2025-01-31', amount: { amount: '21000.0000', currency: 'EUR' } },
  proposed: { asOf: '2025-09-30', amount: { amount: '23500.00', currency: 'EUR' } },
};

export function serve(items: ProposalView[], over: { canDecide?: boolean; preview?: unknown } = {}) {
  let pending = [...items];
  const sent: { approve?: unknown; reject?: unknown } = {};
  server.use(
    http.get('*/api/v1/core/persons', () => HttpResponse.json([{ id: 'a', kind: 'adult', displayName: 'Camille', email: null }])),
    http.get('*/api/v1/core/proposals', () => HttpResponse.json({ items: pending })),
    http.get('*/api/v1/core/proposals/:id', ({ params }) => {
      const p = items.find((i) => i.id === params.id)!;
      return HttpResponse.json({ proposal: p, preview: p.status === 'pending' ? over.preview ?? preview : null, canDecide: over.canDecide ?? true });
    }),
    http.post('*/api/v1/core/proposals/:id/approve', async ({ params, request }) => {
      sent.approve = await request.json();
      pending = pending.filter((i) => i.id !== params.id);
      return HttpResponse.json({ ...items.find((i) => i.id === params.id), status: 'approved' });
    }),
    http.post('*/api/v1/core/proposals/:id/reject', async ({ params, request }) => {
      sent.reject = await request.json();
      pending = pending.filter((i) => i.id !== params.id);
      return HttpResponse.json({ ...items.find((i) => i.id === params.id), status: 'rejected' });
    }),
  );
  return sent;
}
