import { screen, waitFor } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeAll, beforeEach, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { resetAnnouncement } from './announce';
import { proposal } from './testData';
import type { ProposalView } from './api';

const preview = {
  current: null,
  previous: { asOf: '2025-01-31', amount: { amount: '21000.0000', currency: 'EUR' } },
  proposed: { asOf: '2025-09-30', amount: { amount: '23500.00', currency: 'EUR' } },
};

function serve(items: ProposalView[], over: { canDecide?: boolean; preview?: unknown } = {}) {
  let pending = [...items];
  const sent: { approve?: unknown; reject?: unknown } = {};
  server.use(
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

describe('Une proposition', () => {
  beforeEach(resetAnnouncement);
  beforeAll(async () => { await import('./ProposalDetailPage'); await import('./ProposalsPage'); });

  it('shows the comparison and approves it unchanged, then opens the next one', async () => {
    const first = proposal();
    const second = proposal({ targetName: 'Assurance-vie' });
    const sent = serve([first, second]);
    const { user, container, router } = await renderApp(`/proposals/${first.id}`);

    expect(await screen.findByRole('heading', { level: 1, name: 'Valeur d’une enveloppe' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'PEA' })).toHaveAttribute('href', `/investments/${first.targetItemId}`);
    expect(screen.getByRole('row', { name: /Valeur précédente/ })).toHaveTextContent(/21\s000,00\s€/);
    expect(screen.getByRole('row', { name: /Valeur proposée/ })).toHaveTextContent(/23\s500,00\s€/);
    await expectNoAxeViolations(container);

    await user.click(screen.getByRole('button', { name: 'Approuver' }));

    await waitFor(() => expect(sent.approve).toEqual({ version: 1 }));
    await waitFor(() => expect(router.state.location.pathname).toBe(`/proposals/${second.id}`));
    await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('Proposition approuvée.'));
  });

  it('sends the edited payload when the amount changed', async () => {
    const p = proposal();
    const sent = serve([p]);
    const { user } = await renderApp(`/proposals/${p.id}`);

    const amount = await screen.findByLabelText('Montant (EUR)');
    await user.clear(amount);
    await user.type(amount, '23450');
    await user.click(screen.getByRole('button', { name: 'Approuver' }));

    await waitFor(() => expect(sent.approve).toEqual({ version: 1, editedPayload: { asOf: '2025-09-30', amount: { amount: '23450', currency: 'EUR' } } }));
  });

  // Review Focus 3.
  it('sends no editedPayload when nothing really changed', async () => {
    const p = proposal();
    const sent = serve([p]);
    const { user } = await renderApp(`/proposals/${p.id}`);

    const amount = await screen.findByLabelText('Montant (EUR)');
    await user.clear(amount);
    await user.type(amount, '23500,00');
    await user.click(screen.getByRole('button', { name: 'Approuver' }));

    await waitFor(() => expect(sent.approve).toEqual({ version: 1 }));
  });

  // Review Focus 2.
  it('rejects with a note and returns to the list when the last one is decided', async () => {
    const p = proposal();
    const sent = serve([p]);
    const { user, router } = await renderApp(`/proposals/${p.id}`);

    await user.click(await screen.findByRole('button', { name: 'Rejeter' }));
    await user.type(screen.getByLabelText('Note (facultative)'), 'Doublon');
    await user.click(screen.getByRole('button', { name: 'Confirmer le rejet' }));

    await waitFor(() => expect(sent.reject).toEqual({ version: 1, note: 'Doublon' }));
    await waitFor(() => expect(router.state.location.pathname).toBe('/proposals'));
    expect(await screen.findByText('Rien à valider pour l’instant.')).toBeInTheDocument();
    await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('Proposition rejetée.'));
  });

  // Review Focus 4.
  it('offers a reload on a conflict', async () => {
    const p = proposal();
    serve([p]);
    server.use(http.post('*/api/v1/core/proposals/:id/approve', () =>
      HttpResponse.json({ status: 409, code: 'core.proposal_decided', title: 'x' }, { status: 409 })));
    const { user } = await renderApp(`/proposals/${p.id}`);

    await user.click(await screen.findByRole('button', { name: 'Approuver' }));

    expect(await screen.findByText('Cette proposition a changé.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Recharger' })).toBeInTheDocument();
  });

  it('puts a refusal at its field', async () => {
    const p = proposal();
    serve([p]);
    server.use(http.post('*/api/v1/core/proposals/:id/approve', () => HttpResponse.json(
      { status: 422, code: 'core.date_in_future', title: 'A value is dated today or before.', errors: [{ path: 'payload.asOf', code: 'core.date_in_future', message: 'x' }] },
      { status: 422 })));
    const { user } = await renderApp(`/proposals/${p.id}`);

    await user.click(await screen.findByRole('button', { name: 'Approuver' }));

    await waitFor(() => expect(screen.getByLabelText('Date')).toHaveAttribute('aria-invalid', 'true'));
  });

  // Review Focus 4.
  it('shows a decided proposal read only', async () => {
    const p = proposal({ status: 'approved', decidedAt: '2026-10-16T09:00:00+00:00', decisionNote: 'OK' });
    serve([p]);
    await renderApp(`/proposals/${p.id}`);

    expect(await screen.findByText(/Décidée le/)).toBeInTheDocument();
    expect(screen.getByText('Note : OK')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Approuver' })).toBeNull();
  });

  it('is read only without the right to decide', async () => {
    const p = proposal();
    serve([p], { canDecide: false });
    await renderApp(`/proposals/${p.id}`);

    expect(await screen.findByText('Seul un adulte qui peut modifier cet élément peut décider.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Approuver' })).toBeNull();
  });

  // Review Focus 1.
  it('shows a hostile note and warning as plain text', async () => {
    const p = proposal({
      payload: { asOf: '2025-09-30', amount: { amount: '23500.00', currency: 'EUR' }, note: '<img src=x onerror=alert(1)>' },
      warnings: [{ code: 'apply_failed', severity: 'error', message: '<b>boom</b>', field: null }],
    });
    serve([p]);
    const { container } = await renderApp(`/proposals/${p.id}`);

    expect(await screen.findByText('L’application a échoué : <b>boom</b>')).toBeInTheDocument();
    expect(screen.getByLabelText('Note')).toHaveValue('<img src=x onerror=alert(1)>');
    expect(container.querySelector('img, b')).toBeNull();
  });

  // Review Focus 5.
  it('an unknown kind shows its payload as text and can only be rejected', async () => {
    const p = proposal({ kind: 'realestate.future_kind', payload: { x: 1 } });
    serve([p], { preview: null });
    await renderApp(`/proposals/${p.id}`);

    expect(await screen.findByText('Type de proposition inconnu : realestate.future_kind')).toBeInTheDocument();
    expect(screen.getByText('{"x":1}')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Approuver' })).toBeNull();
    expect(screen.getByRole('button', { name: 'Rejeter' })).toBeInTheDocument();
  });
});
