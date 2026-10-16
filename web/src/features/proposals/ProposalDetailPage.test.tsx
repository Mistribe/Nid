import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { resetAnnouncement } from './announce';
import { proposal } from './testData';
import { serve } from './testServe';

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

  it('says a decision once: the next proposal opened by hand shows no old announcement', async () => {
    const [a, b, c] = [proposal(), proposal({ targetName: 'Assurance-vie' }), proposal({ targetName: 'Livret' })];
    serve([a, b, c]);
    const { user, router } = await renderApp(`/proposals/${a.id}`);

    await user.click(await screen.findByRole('button', { name: 'Approuver' }));
    await waitFor(() => expect(router.state.location.pathname).toBe(`/proposals/${b.id}`));
    await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('Proposition approuvée.'));

    await user.click(screen.getByRole('link', { name: 'Proposition suivante' }));
    await waitFor(() => expect(router.state.location.pathname).not.toBe(`/proposals/${b.id}`));
    expect(await screen.findByRole('link', { name: 'Livret' })).toBeInTheDocument();
    await waitFor(() => expect(screen.getByRole('status')).toBeEmptyDOMElement());
  });

  it('refreshes every query after an approval, only the proposals after a rejection', async () => {
    const p = proposal();
    const q = proposal({ targetName: 'Assurance-vie' });
    serve([p, q]);
    const { user, queryClient } = await renderApp(`/proposals/${p.id}`);
    const spy = vi.spyOn(queryClient, 'invalidateQueries');

    await user.click(await screen.findByRole('button', { name: 'Approuver' }));
    await waitFor(() => expect(spy).toHaveBeenCalledWith());

    spy.mockClear();
    await user.click(await screen.findByRole('button', { name: 'Rejeter' }));
    await user.click(screen.getByRole('button', { name: 'Confirmer le rejet' }));
    await waitFor(() => expect(spy).toHaveBeenCalled());
    expect(spy).not.toHaveBeenCalledWith();
  });

  it('moves focus to the heading once a decision opens the next proposal, and to the note when rejecting', async () => {
    const first = proposal();
    const second = proposal({ targetName: 'Assurance-vie' });
    serve([first, second]);
    const { user, router } = await renderApp(`/proposals/${first.id}`);

    await user.click(await screen.findByRole('button', { name: 'Rejeter' }));
    expect(screen.getByLabelText('Note (facultative)')).toHaveFocus();
    await user.click(screen.getByRole('button', { name: 'Annuler' }));
    await user.click(screen.getByRole('button', { name: 'Approuver' }));

    await waitFor(() => expect(router.state.location.pathname).toBe(`/proposals/${second.id}`));
    await waitFor(() => expect(screen.getByRole('heading', { level: 1 })).toHaveFocus());
  });

  it('moves focus to the heading when the next proposal was already open (cached)', async () => {
    const first = proposal();
    const second = proposal({ targetName: 'Assurance-vie' });
    serve([first, second]);
    const { user, router } = await renderApp(`/proposals/${first.id}`);

    await user.click(await screen.findByRole('link', { name: 'Proposition suivante' }));
    expect(await screen.findByRole('link', { name: 'Assurance-vie' })).toBeInTheDocument();
    await router.navigate({ to: '/proposals/$id', params: { id: first.id } });
    expect(await screen.findByRole('link', { name: 'PEA' })).toBeInTheDocument();
    await user.click(await screen.findByRole('button', { name: 'Approuver' }));

    await waitFor(() => expect(router.state.location.pathname).toBe(`/proposals/${second.id}`));
    await waitFor(() => expect(screen.getByRole('heading', { level: 1 })).toHaveFocus());
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
    const nav = screen.getByRole('navigation', { name: 'Navigation principale' });
    await waitFor(() => expect(within(nav).getByRole('link', { name: /À valider/ })).not.toHaveAccessibleName(/en attente/));
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
    const p = proposal({ status: 'approved', decidedAt: '2026-10-16T09:00:00+00:00', decisionNote: 'OK', decidedByPersonId: 'a' });
    serve([p]);
    const { container } = await renderApp(`/proposals/${p.id}`);

    expect(await screen.findByText(/Approuvée le/)).toBeInTheDocument();
    expect(await screen.findByText('Décision de Camille')).toBeInTheDocument();
    expect(screen.getByText('Note : OK')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Voir l’élément' })).toHaveAttribute('href', `/investments/${p.targetItemId}`);
    expect(screen.queryByRole('button', { name: 'Approuver' })).toBeNull();
    await expectNoAxeViolations(container);
  });

  it('offers no link to the target of a rejected proposal', async () => {
    const p = proposal({ status: 'rejected', decidedAt: '2026-10-16T09:00:00+00:00' });
    serve([p]);
    await renderApp(`/proposals/${p.id}`);

    expect(await screen.findByText(/Rejetée le/)).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Voir l’élément' })).toBeNull();
  });

  it('is read only without the right to decide', async () => {
    const p = proposal();
    serve([p], { canDecide: false });
    const { container } = await renderApp(`/proposals/${p.id}`);

    expect(await screen.findByText('Seul un adulte qui peut modifier cet élément peut décider.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Approuver' })).toBeNull();
    await expectNoAxeViolations(container);
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

  it('does not carry one proposal’s form into another', async () => {
    const a = proposal();
    const b = proposal({ targetName: 'Assurance-vie', payload: { asOf: '2025-09-30', amount: { amount: '999.00', currency: 'EUR' } } });
    const sent = serve([a, b]);
    const { user, router } = await renderApp(`/proposals/${a.id}`);

    await screen.findByLabelText('Montant (EUR)');
    await user.click(screen.getByRole('link', { name: 'Proposition suivante' }));
    await waitFor(() => expect(router.state.location.pathname).toBe(`/proposals/${b.id}`));
    await waitFor(() => expect(screen.getByLabelText('Montant (EUR)')).toHaveValue('999'));
    await user.click(screen.getByRole('link', { name: 'Proposition suivante' }));
    await waitFor(() => expect(router.state.location.pathname).toBe(`/proposals/${a.id}`));
    await waitFor(() => expect(screen.getByLabelText('Montant (EUR)')).toHaveValue('23500'));
    await user.click(screen.getByRole('button', { name: 'Approuver' }));

    await waitFor(() => expect(sent.approve).toEqual({ version: 1 }));
  });

  it('tells a conflict on a rejection', async () => {
    const p = proposal();
    serve([p]);
    server.use(http.post('*/api/v1/core/proposals/:id/reject', () =>
      HttpResponse.json({ status: 409, code: 'core.proposal_decided', title: 'x' }, { status: 409 })));
    const { user } = await renderApp(`/proposals/${p.id}`);

    await user.click(await screen.findByRole('button', { name: 'Rejeter' }));
    await user.click(screen.getByRole('button', { name: 'Confirmer le rejet' }));

    expect(await screen.findByText('Cette proposition a changé.')).toBeInTheDocument();
  });

  it('shows a failed rejection', async () => {
    const p = proposal();
    serve([p]);
    server.use(http.post('*/api/v1/core/proposals/:id/reject', () =>
      HttpResponse.json({ status: 500, title: 'x' }, { status: 500 })));
    const { user } = await renderApp(`/proposals/${p.id}`);

    await user.click(await screen.findByRole('button', { name: 'Rejeter' }));
    await user.click(screen.getByRole('button', { name: 'Confirmer le rejet' }));

    expect(await screen.findByRole('alert')).toBeInTheDocument();
  });

  it('puts an amount refusal at the amount field', async () => {
    const p = proposal();
    serve([p]);
    server.use(http.post('*/api/v1/core/proposals/:id/approve', () => HttpResponse.json(
      { status: 422, code: 'core.invalid', title: 'x', errors: [{ path: 'payload.amount.amount', code: 'core.invalid', message: 'x' }] },
      { status: 422 })));
    const { user } = await renderApp(`/proposals/${p.id}`);

    await user.click(await screen.findByRole('button', { name: 'Approuver' }));

    await waitFor(() => expect(screen.getByLabelText('Montant (EUR)')).toHaveAttribute('aria-invalid', 'true'));
  });

  it('an unsupported payload version can only be rejected', async () => {
    const p = proposal({ warnings: [{ code: 'payload_version_unsupported', severity: 'error', message: 'v9', field: null }] });
    serve([p]);
    await renderApp(`/proposals/${p.id}`);

    expect(await screen.findByText('Cette proposition ne peut plus être appliquée : rejetez-la.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Approuver' })).toBeNull();
    expect(screen.getByRole('button', { name: 'Rejeter' })).toBeInTheDocument();
    // The server's English message is not repeated next to the French line.
    expect(screen.queryByText('v9')).toBeNull();
  });
});
