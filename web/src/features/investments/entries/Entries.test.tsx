import { screen, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { EnvelopeDetail } from '../api';
import { detail, eur, PEA } from '../testData';

function serve(current: EnvelopeDetail, calls: { method: string; path: string; body?: unknown }[]) {
  let state = current;
  server.use(
    http.get('*/api/v1/investments/envelopes/:id', () => HttpResponse.json(state)),
    http.post('*/api/v1/investments/envelopes/:id/valuations', async ({ request }) => {
      const body = await request.json() as { asOf: string; amount: { amount: string; currency: string } };
      calls.push({ method: 'POST', path: 'valuations', body });
      state = { ...state, valuations: [...state.valuations.filter((v) => v.asOf !== body.asOf), { id: 'new', asOf: body.asOf, amount: body.amount, note: null }] };
      return HttpResponse.json({ id: 'new' }, { status: 201 });
    }),
    http.post('*/api/v1/investments/envelopes/:id/flows', async ({ request }) => {
      calls.push({ method: 'POST', path: 'flows', body: await request.json() });
      return HttpResponse.json({ id: 'nf' }, { status: 201 });
    }),
    http.put('*/api/v1/investments/envelopes/:id/flows/:flowId', async ({ request, params }) => {
      calls.push({ method: 'PUT', path: `flows/${String(params.flowId)}`, body: await request.json() });
      return HttpResponse.json({ id: 'nf2' });
    }),
    http.delete('*/api/v1/investments/envelopes/:id/flows/:flowId', ({ params }) => {
      calls.push({ method: 'DELETE', path: `flows/${String(params.flowId)}` });
      return HttpResponse.json({ id: String(params.flowId) });
    }),
    http.delete('*/api/v1/investments/envelopes/:id/valuations/:valuationId', ({ params }) => {
      calls.push({ method: 'DELETE', path: `valuations/${String(params.valuationId)}` });
      return HttpResponse.json({ id: String(params.valuationId) });
    }),
  );
}

describe('recording values and flows', () => {
  it('records a new value, prefilled with the last one', async () => {
    const calls: { method: string; path: string; body?: unknown }[] = [];
    serve(detail(), calls);
    const { user, container } = await renderApp(`/investments/${PEA}`);

    const form = await screen.findByRole('form', { name: 'Nouvelle valeur' });
    expect(within(form).getByLabelText('Montant')).toHaveValue('25000');
    await user.clear(within(form).getByLabelText('Montant'));
    await user.type(within(form).getByLabelText('Montant'), '25400,10');
    await user.click(within(form).getByRole('button', { name: 'Enregistrer la valeur' }));

    expect(await screen.findByRole('status')).toHaveTextContent('Valeur enregistrée.');
    expect(calls).toEqual([{ method: 'POST', path: 'valuations', body: expect.objectContaining({ amount: { amount: '25400.10', currency: 'EUR' } }) as unknown }]);
    await expectNoAxeViolations(container);
  });

  it('says a value on a date that already has one replaces it, and leaves one row for that date', async () => {
    const calls: { method: string; path: string; body?: unknown }[] = [];
    serve(detail(), calls);
    const { user } = await renderApp(`/investments/${PEA}`);

    const form = await screen.findByRole('form', { name: 'Nouvelle valeur' });
    await user.clear(within(form).getByLabelText('Date'));
    await user.type(within(form).getByLabelText('Date'), '2025-09-30');
    expect(within(form).getByText('Une valeur existe déjà à cette date : elle sera remplacée.')).toBeInTheDocument();
    await user.click(within(form).getByRole('button', { name: 'Enregistrer la valeur' }));

    await screen.findByRole('status');
    const rows = within(screen.getByRole('table', { name: 'Valeurs de l’enveloppe' })).getAllByRole('row').slice(1);
    expect(rows.filter((r) => r.textContent?.includes('30 septembre 2025'))).toHaveLength(1);
  });

  it('accepts a negative value (an overdrawn account) but refuses a negative flow', async () => {
    const calls: { method: string; path: string; body?: unknown }[] = [];
    serve(detail({ kind: 'checking' }), calls);
    const { user } = await renderApp(`/investments/${PEA}`);

    const value = await screen.findByRole('form', { name: 'Nouvelle valeur' });
    await user.clear(within(value).getByLabelText('Montant'));
    await user.type(within(value).getByLabelText('Montant'), '-120,50');
    await user.click(within(value).getByRole('button', { name: 'Enregistrer la valeur' }));
    await screen.findByRole('status');

    const flow = screen.getByRole('form', { name: 'Nouveau flux' });
    await user.type(within(flow).getByLabelText('Montant'), '-10');
    await user.click(within(flow).getByRole('button', { name: 'Ajouter le flux' }));
    expect(await within(flow).findByText('Saisissez un montant positif comme 1234,56.')).toBeInTheDocument();
    expect(calls.map((c) => c.path)).toEqual(['valuations']);
  });

  it('adds a versement', async () => {
    const calls: { method: string; path: string; body?: unknown }[] = [];
    serve(detail(), calls);
    const { user } = await renderApp(`/investments/${PEA}`);

    const flow = await screen.findByRole('form', { name: 'Nouveau flux' });
    await user.selectOptions(within(flow).getByLabelText('Nature'), 'contribution');
    await user.type(within(flow).getByLabelText('Montant'), '150');
    await user.click(within(flow).getByRole('button', { name: 'Ajouter le flux' }));

    expect(await within(flow).findByRole('status')).toHaveTextContent('Flux ajouté.');
    expect(calls).toEqual([{ method: 'POST', path: 'flows', body: expect.objectContaining({ kind: 'contribution', amount: { amount: '150', currency: 'EUR' } }) as unknown }]);
  });

  it('offers versements antérieurs only while there is none', async () => {
    serve(detail(), []);
    await renderApp(`/investments/${PEA}`);

    const flow = await screen.findByRole('form', { name: 'Nouveau flux' });
    expect(within(within(flow).getByLabelText('Nature')).queryByRole('option', { name: 'Versements antérieurs' })).not.toBeInTheDocument();
  });

  it('corrects a flow on its row', async () => {
    const calls: { method: string; path: string; body?: unknown }[] = [];
    serve(detail(), calls);
    const { user } = await renderApp(`/investments/${PEA}`);

    const table = await screen.findByRole('table', { name: 'Flux de l’enveloppe' });
    const row = within(table).getAllByRole('row').slice(1)[0]!;
    await user.click(within(row).getByRole('button', { name: 'Corriger' }));
    const edit = screen.getByRole('form', { name: 'Corriger le flux' });
    await user.clear(within(edit).getByLabelText('Montant'));
    await user.type(within(edit).getByLabelText('Montant'), '2500');
    await user.click(within(edit).getByRole('button', { name: 'Enregistrer' }));

    await screen.findByRole('status');
    expect(calls).toEqual([{ method: 'PUT', path: 'flows/f2', body: expect.objectContaining({ amount: { amount: '2500', currency: 'EUR' } }) as unknown }]);
  });

  it('deletes a transfer leg after saying the other envelope keeps its own', async () => {
    const calls: { method: string; path: string; body?: unknown }[] = [];
    serve(detail({ flows: [{ id: 't1', occurredOn: '2025-06-15', kind: 'withdrawal', amount: eur('100.0000'), category: 'transfer', externalRef: 'transfer:x', note: null }] }), calls);
    const { user } = await renderApp(`/investments/${PEA}`);

    const row = within(await screen.findByRole('table', { name: 'Flux de l’enveloppe' })).getAllByRole('row').slice(1)[0]!;
    expect(within(row).queryByRole('button', { name: 'Corriger' })).not.toBeInTheDocument();
    await user.click(within(row).getByRole('button', { name: 'Supprimer' }));
    expect(screen.getByText('Seule cette jambe du virement sera supprimée : l’autre enveloppe garde la sienne.')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Confirmer' }));

    await screen.findByRole('status');
    expect(calls).toEqual([{ method: 'DELETE', path: 'flows/t1' }]);
  });

  it('shows a conflict with a way to reload', async () => {
    server.use(
      http.get('*/api/v1/investments/envelopes/:id', () => HttpResponse.json(detail())),
      http.delete('*/api/v1/investments/envelopes/:id/valuations/:valuationId', () =>
        HttpResponse.json({ status: 409, code: 'core.fact_retired', title: 'This value was already corrected or voided.' }, { status: 409 })),
    );
    const { user } = await renderApp(`/investments/${PEA}`);

    const row = within(await screen.findByRole('table', { name: 'Valeurs de l’enveloppe' })).getAllByRole('row').slice(1)[0]!;
    await user.click(within(row).getByRole('button', { name: 'Supprimer' }));
    await user.click(screen.getByRole('button', { name: 'Confirmer' }));

    expect(await screen.findByRole('button', { name: /Recharger/ })).toBeInTheDocument();
  });

  it('opens the flow form on versements antérieurs from the missing-opening warning', async () => {
    serve(detail({ flows: [], figures: { ...detail().figures, missingOpening: true } }), []);
    const { user } = await renderApp(`/investments/${PEA}`);

    await user.click(await screen.findByRole('button', { name: 'Saisir les versements antérieurs' }));

    expect(within(screen.getByRole('form', { name: 'Nouveau flux' })).getByLabelText('Nature')).toHaveValue('opening');
  });
});
