import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeAll, beforeEach, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { resetAnnouncement } from './announce';
import { proposal } from './testData';
import { serve } from './testServe';

const payload = {
  asOf: '2025-09-30', value: { amount: '23500.00', currency: 'EUR' },
  movements: [
    { on: '2025-09-05', kind: 'contribution', amount: '150.00', label: 'Versement', ref: 'R-1' },
    { on: '2025-09-12', kind: 'withdrawal', amount: '60.00', label: '<b>Retrait</b>' },
    { on: '2025-09-20', kind: 'contribution', amount: '75.00' },
  ],
};
const statementPreview = {
  value: {
    current: null,
    previous: { asOf: '2025-01-31', amount: { amount: '21000.0000', currency: 'EUR' } },
    proposed: { asOf: '2025-09-30', amount: { amount: '23500.00', currency: 'EUR' } },
  },
  movements: payload.movements.map((m, index) => ({
    index, on: m.on, kind: m.kind, amount: { amount: m.amount, currency: 'EUR' }, label: m.label ?? null, recorded: index === 0,
  })),
};
const statement = () => proposal({
  kind: 'investments.record_statement', payload, subjectKey: '2025-09-30',
  warnings: [{ code: 'already_recorded', severity: 'info', message: 'Movement 1 is already recorded.', field: 'payload.movements[0]' }],
});

describe('Un relevé proposé', () => {
  beforeEach(resetAnnouncement);
  beforeAll(async () => { await import('./ProposalDetailPage'); await import('./ProposalsPage'); });

  it('shows the value and the movements, the recorded one unchecked', async () => {
    const p = statement();
    serve([p], { preview: statementPreview });
    const { container } = await renderApp(`/proposals/${p.id}`);

    expect(await screen.findByRole('heading', { level: 1, name: 'Relevé d’une enveloppe' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'PEA' })).toHaveAttribute('href', `/investments/${p.targetItemId}`);
    expect(screen.getByRole('row', { name: /Valeur proposée/ })).toHaveTextContent(/23\s500,00\s€/);
    const table = screen.getByRole('table', { name: 'Mouvements du relevé' });
    expect(within(table).getAllByRole('row')[1]).toHaveTextContent('Déjà enregistré');
    expect(within(table).getByText('<b>Retrait</b>')).toBeInTheDocument();
    expect(container.querySelector('b')).toBeNull();
    expect(screen.queryByText('Movement 1 is already recorded.')).toBeNull();
    expect(screen.getByRole('checkbox', { name: /Mouvement 1/ })).not.toBeChecked();
    expect(screen.getByRole('checkbox', { name: /Mouvement 2/ })).toBeChecked();
    await expectNoAxeViolations(container);
  });

  it('sends no edit when nothing changed', async () => {
    const p = statement();
    const sent = serve([p], { preview: statementPreview });
    const { user } = await renderApp(`/proposals/${p.id}`);

    await user.click(await screen.findByRole('button', { name: 'Approuver' }));

    await waitFor(() => expect(sent.approve).toEqual({ version: 1 }));
  });

  it('sends the unchecked movements as excluded', async () => {
    const p = statement();
    const sent = serve([p], { preview: statementPreview });
    const { user } = await renderApp(`/proposals/${p.id}`);

    await user.click(await screen.findByRole('checkbox', { name: /Mouvement 2/ }));
    await user.click(screen.getByRole('button', { name: 'Approuver' }));

    await waitFor(() => expect(sent.approve).toEqual({ version: 1, editedPayload: { ...payload, excluded: [0, 1] } }));
  });

  it('includes a recorded movement once re-checked', async () => {
    const p = statement();
    const sent = serve([p], { preview: statementPreview });
    const { user } = await renderApp(`/proposals/${p.id}`);

    await user.click(await screen.findByRole('checkbox', { name: /Mouvement 1/ }));
    await user.click(screen.getByRole('button', { name: 'Approuver' }));

    await waitFor(() => expect(sent.approve).toEqual({ version: 1, editedPayload: { ...payload, excluded: [] } }));
  });

  it('sends an edited value with the movements as checked', async () => {
    const p = statement();
    const sent = serve([p], { preview: statementPreview });
    const { user } = await renderApp(`/proposals/${p.id}`);

    const amount = await screen.findByLabelText('Montant (EUR)');
    await user.clear(amount);
    await user.type(amount, '23450,5');
    await user.click(screen.getByRole('button', { name: 'Approuver' }));

    await waitFor(() => expect(sent.approve).toEqual({
      version: 1, editedPayload: { ...payload, value: { amount: '23450.5', currency: 'EUR' }, excluded: [0] },
    }));
  });

  // Review Focus 2.
  it('shows a refused movement above the form, without a reload', async () => {
    const p = statement();
    serve([p], { preview: statementPreview });
    server.use(http.post('*/api/v1/core/proposals/:id/approve', () => HttpResponse.json(
      { status: 409, code: 'core.external_ref_taken', title: 'This reference is already recorded: leave this movement out.',
        errors: [{ path: 'payload.movements[0].ref', code: 'core.external_ref_taken', message: 'x' }] },
      { status: 409 })));
    const { user } = await renderApp(`/proposals/${p.id}`);

    await user.click(await screen.findByRole('checkbox', { name: /Mouvement 1/ }));
    await user.click(screen.getByRole('button', { name: 'Approuver' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Le mouvement 1 est déjà enregistré avec cette référence : décochez-le.');
    expect(screen.queryByRole('button', { name: 'Recharger' })).toBeNull();
  });

  it('shows a decided statement without a form', async () => {
    const p = { ...statement(), status: 'approved' as const, decidedAt: '2026-10-16T09:00:00+00:00', decidedByPersonId: 'a' };
    serve([p], { preview: statementPreview });
    const { container } = await renderApp(`/proposals/${p.id}`);

    expect(await screen.findByRole('link', { name: 'Voir l’élément' })).toBeInTheDocument();
    expect(screen.queryByRole('checkbox')).toBeNull();
    await expectNoAxeViolations(container);
  });

  it('shows a statement read only to someone who cannot decide', async () => {
    const p = statement();
    serve([p], { preview: statementPreview, canDecide: false });
    const { container } = await renderApp(`/proposals/${p.id}`);

    expect(await screen.findByText('Seul un adulte qui peut modifier cet élément peut décider.')).toBeInTheDocument();
    expect(screen.queryByRole('checkbox')).toBeNull();
    await expectNoAxeViolations(container);
  });

  it('disables the approval when no value and no movement would be recorded', async () => {
    const p = proposal({ kind: 'investments.record_statement', payload: { asOf: '2025-09-30', movements: [payload.movements[0]] } });
    serve([p], { preview: { movements: [statementPreview.movements[0]] } });
    const { user, container } = await renderApp(`/proposals/${p.id}`);

    expect(await screen.findByText(/il n’y aurait rien à enregistrer/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Approuver' })).toBeDisabled();
    await expectNoAxeViolations(container);

    await user.click(screen.getByRole('checkbox', { name: /Mouvement 1/ }));
    expect(screen.getByRole('button', { name: 'Approuver' })).toBeEnabled();
    expect(screen.queryByText(/il n’y aurait rien à enregistrer/)).toBeNull();
  });
});
