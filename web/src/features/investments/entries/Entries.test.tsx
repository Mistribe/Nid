import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { todayIso } from '../../subscriptions/today';
import type { EnvelopeDetail } from '../api';
import { detail, eur, PEA } from '../testData';

/**
 * The section's own live region — a direct child of `<section>`, right after its heading — as opposed to a row
 * correction's own status paragraph, which lives inside the very same section (in its table) while a row is being
 * corrected: `within(section).getByRole('status')` would then find both and fail on the ambiguity.
 */
function sectionStatus(section: HTMLElement): HTMLElement {
  const el = section.querySelector(':scope > p[role="status"]');
  if (!el) throw new Error('section has no status live region');
  return el as HTMLElement;
}

/**
 * Mocks the server's real behaviour, not just an id echo: a value on an already-known date replaces that row (a
 * new id), a flow correction supersedes the old one (a new id, the server's `SupersedeAsync`), and a deletion
 * removes the row — so a test can assert on the table and the section's live region after the refetch, the way the
 * real page reacts.
 */
function serve(current: EnvelopeDetail, calls: { method: string; path: string; body?: unknown }[]) {
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

describe('recording values and flows', () => {
  // The lazy route chunk loaded cold inside the first test races its timeouts under load.
  beforeAll(async () => { await import('../EnvelopeDetailPage'); }, 30_000);

  it('records a new value, prefilled with the last one, then with the one just saved', async () => {
    const calls: { method: string; path: string; body?: unknown }[] = [];
    serve(detail(), calls);
    const { user, container } = await renderApp(`/investments/${PEA}`);

    const form = await screen.findByRole('form', { name: 'Nouvelle valeur' });
    expect(within(form).getByLabelText('Montant')).toHaveValue('25000');
    await user.clear(within(form).getByLabelText('Montant'));
    await user.type(within(form).getByLabelText('Montant'), '25400,10');
    await user.click(within(form).getByRole('button', { name: 'Enregistrer la valeur' }));

    expect(await within(form).findByRole('status')).toHaveTextContent('Valeur enregistrée.');
    expect(within(form).getByLabelText('Montant')).toHaveValue('25400.1');
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

    await within(form).findByRole('status');
    const rows = within(screen.getByRole('table', { name: 'Valeurs de l’enveloppe' })).getAllByRole('row').slice(1);
    expect(rows.filter((r) => r.textContent?.includes('30 septembre 2025'))).toHaveLength(1);
  });

  it.each(['0', '0,00'])('refuses a zero flow amount (%s)', async (zero) => {
    const calls: { method: string; path: string; body?: unknown }[] = [];
    serve(detail(), calls);
    const { user } = await renderApp(`/investments/${PEA}`);

    const flow = await screen.findByRole('form', { name: 'Nouveau flux' });
    await user.type(within(flow).getByLabelText('Montant'), zero);
    await user.click(within(flow).getByRole('button', { name: 'Ajouter le flux' }));

    expect(await within(flow).findByText('Saisissez un montant positif comme 1234,56.')).toBeInTheDocument();
    expect(calls).toEqual([]);
  });

  it('gives the date inputs a max of today, so no future date can be picked', async () => {
    serve(detail(), []);
    const { user } = await renderApp(`/investments/${PEA}`);

    const value = await screen.findByRole('form', { name: 'Nouvelle valeur' });
    expect(within(value).getByLabelText('Date')).toHaveAttribute('max', todayIso());
    const flow = screen.getByRole('form', { name: 'Nouveau flux' });
    expect(within(flow).getByLabelText('Date')).toHaveAttribute('max', todayIso());

    const table = await screen.findByRole('table', { name: 'Flux de l’enveloppe' });
    const row = within(table).getAllByRole('row').slice(1)[0]!;
    await user.click(within(row).getByRole('button', { name: 'Corriger' }));
    const edit = screen.getByRole('form', { name: 'Corriger le flux' });
    expect(within(edit).getByLabelText('Date')).toHaveAttribute('max', todayIso());
  });

  it('accepts a negative value (an overdrawn account) but refuses a negative flow', async () => {
    const calls: { method: string; path: string; body?: unknown }[] = [];
    serve(detail({ kind: 'checking' }), calls);
    const { user } = await renderApp(`/investments/${PEA}`);

    const value = await screen.findByRole('form', { name: 'Nouvelle valeur' });
    await user.clear(within(value).getByLabelText('Montant'));
    await user.type(within(value).getByLabelText('Montant'), '-120,50');
    await user.click(within(value).getByRole('button', { name: 'Enregistrer la valeur' }));
    await within(value).findByRole('status');

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

  it('offers versements antérieurs when correcting the opening itself', async () => {
    serve(detail(), []);
    const { user } = await renderApp(`/investments/${PEA}`);

    const table = await screen.findByRole('table', { name: 'Flux de l’enveloppe' });
    const rows = within(table).getAllByRole('row').slice(1);
    const openingRow = rows.find((r) => r.textContent?.includes('Versements antérieurs'));
    if (!openingRow) throw new Error('no opening row in the fixture');
    await user.click(within(openingRow).getByRole('button', { name: 'Corriger' }));
    const edit = screen.getByRole('form', { name: 'Corriger le flux' });

    expect(within(edit).getByLabelText('Nature')).toHaveValue('opening');
    expect(within(within(edit).getByLabelText('Nature')).getByRole('option', { name: 'Versements antérieurs' })).toBeInTheDocument();
  });

  it('corrects a flow on its row, announcing it and moving focus to its section', async () => {
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

    const heading = await screen.findByRole('heading', { name: 'Flux' });
    const section = heading.closest('section');
    if (!section) throw new Error('heading is not inside a section');
    await waitFor(() => expect(sectionStatus(section)).toHaveTextContent('Flux corrigé.'));
    await within(table).findByText('2 500,00 €');
    expect(document.activeElement).toBe(heading);
    expect(calls).toEqual([{ method: 'PUT', path: 'flows/f2', body: expect.objectContaining({ amount: { amount: '2500', currency: 'EUR' } }) as unknown }]);
  });

  it('corrects a value on its row, announcing it and moving focus to its section', async () => {
    const calls: { method: string; path: string; body?: unknown }[] = [];
    serve(detail(), calls);
    const { user } = await renderApp(`/investments/${PEA}`);

    const table = await screen.findByRole('table', { name: 'Valeurs de l’enveloppe' });
    const row = within(table).getAllByRole('row').slice(1)[0]!;
    await user.click(within(row).getByRole('button', { name: 'Corriger' }));
    const edit = screen.getByRole('form', { name: 'Corriger la valeur' });
    await user.clear(within(edit).getByLabelText('Montant'));
    await user.type(within(edit).getByLabelText('Montant'), '26000');
    await user.click(within(edit).getByRole('button', { name: 'Enregistrer' }));

    const heading = await screen.findByRole('heading', { name: 'Valeurs' });
    const section = heading.closest('section');
    if (!section) throw new Error('heading is not inside a section');
    await waitFor(() => expect(sectionStatus(section)).toHaveTextContent('Valeur enregistrée.'));
    await within(table).findByText('26 000,00 €');
    expect(document.activeElement).toBe(heading);
    expect(calls).toEqual([{ method: 'POST', path: 'valuations', body: expect.objectContaining({ amount: { amount: '26000', currency: 'EUR' } }) as unknown }]);
  });

  it('keeps a flow\'s category and external reference when correcting it', async () => {
    const calls: { method: string; path: string; body?: unknown }[] = [];
    serve(detail({ flows: [{ id: 'f3', occurredOn: '2025-06-15', kind: 'contribution', amount: eur('50.0000'), category: 'import', externalRef: 'import:42', note: null }] }), calls);
    const { user } = await renderApp(`/investments/${PEA}`);

    const table = await screen.findByRole('table', { name: 'Flux de l’enveloppe' });
    const row = within(table).getAllByRole('row').slice(1)[0]!;
    await user.click(within(row).getByRole('button', { name: 'Corriger' }));
    const edit = screen.getByRole('form', { name: 'Corriger le flux' });
    await user.clear(within(edit).getByLabelText('Montant'));
    await user.type(within(edit).getByLabelText('Montant'), '75');
    await user.click(within(edit).getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(calls).toEqual([
      { method: 'PUT', path: 'flows/f3', body: expect.objectContaining({ category: 'import', externalRef: 'import:42' }) as unknown },
    ]));
  });

  it('returns focus to the row\'s « Corriger » button when a flow correction is cancelled', async () => {
    serve(detail(), []);
    const { user } = await renderApp(`/investments/${PEA}`);

    const table = await screen.findByRole('table', { name: 'Flux de l’enveloppe' });
    const row = within(table).getAllByRole('row').slice(1)[0]!;
    await user.click(within(row).getByRole('button', { name: 'Corriger' }));
    const edit = screen.getByRole('form', { name: 'Corriger le flux' });
    await user.click(within(edit).getByRole('button', { name: 'Annuler' }));

    await waitFor(() => expect(document.activeElement).toBe(within(row).getByRole('button', { name: 'Corriger' })));
  });

  it('returns focus to the row\'s « Corriger » button when a value correction is cancelled', async () => {
    serve(detail(), []);
    const { user } = await renderApp(`/investments/${PEA}`);

    const table = await screen.findByRole('table', { name: 'Valeurs de l’enveloppe' });
    const row = within(table).getAllByRole('row').slice(1)[0]!;
    await user.click(within(row).getByRole('button', { name: 'Corriger' }));
    const edit = screen.getByRole('form', { name: 'Corriger la valeur' });
    await user.click(within(edit).getByRole('button', { name: 'Annuler' }));

    await waitFor(() => expect(document.activeElement).toBe(within(row).getByRole('button', { name: 'Corriger' })));
  });

  it('deletes a value, announcing « Valeur supprimée. » in its own section', async () => {
    const calls: { method: string; path: string; body?: unknown }[] = [];
    serve(detail(), calls);
    const { user } = await renderApp(`/investments/${PEA}`);

    const table = await screen.findByRole('table', { name: 'Valeurs de l’enveloppe' });
    const heading = await screen.findByRole('heading', { name: 'Valeurs' });
    const section = heading.closest('section');
    if (!section) throw new Error('heading is not inside a section');
    const row = within(table).getAllByRole('row').slice(1)[0]!;
    await user.click(within(row).getByRole('button', { name: 'Supprimer' }));
    await user.click(screen.getByRole('button', { name: 'Confirmer' }));

    await waitFor(() => expect(sectionStatus(section)).toHaveTextContent('Valeur supprimée.'));
    expect(calls).toEqual([{ method: 'DELETE', path: 'valuations/v2' }]);
  });

  it('hides the versements-antérieurs button once an opening already exists, even while it is reported missing', async () => {
    serve(detail({ figures: { ...detail().figures, missingOpening: true } }), []);
    await renderApp(`/investments/${PEA}`);

    expect(await screen.findByText('Le gain comprend peut-être des versements faits avant le début du suivi.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Saisir les versements antérieurs' })).not.toBeInTheDocument();
  });

  it('deletes a transfer leg after saying the other envelope keeps its own, announcing it and moving focus', async () => {
    const calls: { method: string; path: string; body?: unknown }[] = [];
    serve(detail({ flows: [{ id: 't1', occurredOn: '2025-06-15', kind: 'withdrawal', amount: eur('100.0000'), category: 'transfer', externalRef: 'transfer:x', note: null }] }), calls);
    const { user } = await renderApp(`/investments/${PEA}`);

    const table = await screen.findByRole('table', { name: 'Flux de l’enveloppe' });
    const row = within(table).getAllByRole('row').slice(1)[0]!;
    expect(within(row).queryByRole('button', { name: 'Corriger' })).not.toBeInTheDocument();
    await user.click(within(row).getByRole('button', { name: 'Supprimer' }));
    expect(screen.getByText('Seule cette jambe du virement sera supprimée : l’autre enveloppe garde la sienne.')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Confirmer' }));

    const heading = await screen.findByRole('heading', { name: 'Flux' });
    const section = heading.closest('section');
    if (!section) throw new Error('heading is not inside a section');
    await waitFor(() => expect(sectionStatus(section)).toHaveTextContent('Flux supprimé.'));
    await waitFor(() => expect(within(table).getByText('Aucun flux pour l’instant')).toBeInTheDocument());
    expect(document.activeElement).toBe(heading);
    expect(calls).toEqual([{ method: 'DELETE', path: 'flows/t1' }]);
  });

  it('announces each deletion in the Flux section again, even when the message repeats', async () => {
    serve(detail(), []);
    const { user } = await renderApp(`/investments/${PEA}`);

    const table = await screen.findByRole('table', { name: 'Flux de l’enveloppe' });
    const heading = await screen.findByRole('heading', { name: 'Flux' });
    const section = heading.closest('section');
    if (!section) throw new Error('heading is not inside a section');

    const row = within(table).getAllByRole('row').slice(1)[0]!;
    await user.click(within(row).getByRole('button', { name: 'Supprimer' }));
    await user.click(screen.getByRole('button', { name: 'Confirmer' }));
    await waitFor(() => expect(sectionStatus(section)).toHaveTextContent('Flux supprimé.'));
    await waitFor(() => expect(within(table).getAllByRole('row')).toHaveLength(2));

    // The second deletion's request is held back (MSW mocks respond near-instantly otherwise, so `user.click`'s
    // own microtask flushing would race straight past the emptied status without a test ever observing it): the
    // status must already read empty before the request even resolves, otherwise an unchanged « Flux supprimé. »
    // would never be announced again.
    let resolveDelete!: () => void;
    const pending = new Promise<void>((resolve) => { resolveDelete = resolve; });
    server.use(
      http.delete('*/api/v1/investments/envelopes/:id/flows/:flowId', async ({ params }) => {
        await pending;
        return HttpResponse.json({ id: String(params.flowId) });
      }),
    );
    const secondRow = within(table).getAllByRole('row').slice(1)[0]!;
    await user.click(within(secondRow).getByRole('button', { name: 'Supprimer' }));
    await user.click(screen.getByRole('button', { name: 'Confirmer' }));
    expect(sectionStatus(section)).toBeEmptyDOMElement();

    resolveDelete();
    await waitFor(() => expect(sectionStatus(section)).toHaveTextContent('Flux supprimé.'));
  });

  it('a failed correction after a successful one leaves the section status empty', async () => {
    serve(detail(), []);
    const { user } = await renderApp(`/investments/${PEA}`);

    const table = await screen.findByRole('table', { name: 'Flux de l’enveloppe' });
    const heading = await screen.findByRole('heading', { name: 'Flux' });
    const section = heading.closest('section');
    if (!section) throw new Error('heading is not inside a section');

    // A successful correction first, so the section has something to say.
    let row = within(table).getAllByRole('row').slice(1)[0]!;
    await user.click(within(row).getByRole('button', { name: 'Corriger' }));
    let edit = screen.getByRole('form', { name: 'Corriger le flux' });
    await user.clear(within(edit).getByLabelText('Montant'));
    await user.type(within(edit).getByLabelText('Montant'), '2500');
    await user.click(within(edit).getByRole('button', { name: 'Enregistrer' }));
    await waitFor(() => expect(sectionStatus(section)).toHaveTextContent('Flux corrigé.'));
    await within(table).findByText('2 500,00 €');

    // Opening a correction on another row clears the section; a failed correction never re-sets it.
    server.use(
      http.put('*/api/v1/investments/envelopes/:id/flows/:flowId', () =>
        HttpResponse.json({ status: 409, code: 'core.fact_conflict', title: 'Someone changed this meanwhile.' }, { status: 409 })),
    );
    row = within(table).getAllByRole('row').slice(1)[1]!;
    await user.click(within(row).getByRole('button', { name: 'Corriger' }));
    expect(sectionStatus(section)).toBeEmptyDOMElement();
    edit = screen.getByRole('form', { name: 'Corriger le flux' });
    await user.clear(within(edit).getByLabelText('Montant'));
    await user.type(within(edit).getByLabelText('Montant'), '999');
    await user.click(within(edit).getByRole('button', { name: 'Enregistrer' }));

    await within(edit).findByRole('button', { name: /Recharger/ });
    expect(sectionStatus(section)).toBeEmptyDOMElement();
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

  it('reloads the envelope on Recharger, and clears the conflict', async () => {
    let getCalls = 0;
    server.use(
      http.get('*/api/v1/investments/envelopes/:id', () => { getCalls += 1; return HttpResponse.json(detail()); }),
      http.delete('*/api/v1/investments/envelopes/:id/valuations/:valuationId', () =>
        HttpResponse.json({ status: 409, code: 'core.fact_retired', title: 'This value was already corrected or voided.' }, { status: 409 })),
    );
    const { user } = await renderApp(`/investments/${PEA}`);

    const row = within(await screen.findByRole('table', { name: 'Valeurs de l’enveloppe' })).getAllByRole('row').slice(1)[0]!;
    await user.click(within(row).getByRole('button', { name: 'Supprimer' }));
    await user.click(screen.getByRole('button', { name: 'Confirmer' }));
    await screen.findByRole('button', { name: /Recharger/ });

    const before = getCalls;
    await user.click(screen.getByRole('button', { name: /Recharger/ }));

    await waitFor(() => expect(getCalls).toBeGreaterThan(before));
    await waitFor(() => expect(screen.queryByRole('alert')).not.toBeInTheDocument());
  });

  it('shows a known conflict\'s own sentence on the flow form, with a way to reload', async () => {
    serve(detail(), []);
    server.use(
      http.post('*/api/v1/investments/envelopes/:id/flows', () =>
        HttpResponse.json({ status: 409, code: 'core.opening_exists', title: 'The opening is already recorded.' }, { status: 409 })),
    );
    const { user } = await renderApp(`/investments/${PEA}`);

    const flow = await screen.findByRole('form', { name: 'Nouveau flux' });
    await user.type(within(flow).getByLabelText('Montant'), '10');
    await user.click(within(flow).getByRole('button', { name: 'Ajouter le flux' }));

    expect(await within(flow).findByText('Les versements antérieurs sont déjà saisis : corrigez-les.')).toBeInTheDocument();
    expect(within(flow).getByRole('button', { name: /Recharger/ })).toBeInTheDocument();
  });

  it('places a pathless core.date_in_future on the value form date field', async () => {
    serve(detail(), []);
    server.use(
      http.post('*/api/v1/investments/envelopes/:id/valuations', () =>
        HttpResponse.json({ status: 422, code: 'core.date_in_future', title: 'A value or a flow is not dated after today.' }, { status: 422 })),
    );
    const { user } = await renderApp(`/investments/${PEA}`);

    const form = await screen.findByRole('form', { name: 'Nouvelle valeur' });
    await user.click(within(form).getByRole('button', { name: 'Enregistrer la valeur' }));

    expect(await within(form).findByText('Cette date est dans le futur.')).toBeInTheDocument();
  });

  it('opens the flow form on versements antérieurs from the missing-opening warning', async () => {
    serve(detail({ flows: [], figures: { ...detail().figures, missingOpening: true } }), []);
    const { user } = await renderApp(`/investments/${PEA}`);

    await user.click(await screen.findByRole('button', { name: 'Saisir les versements antérieurs' }));

    expect(within(screen.getByRole('form', { name: 'Nouveau flux' })).getByLabelText('Nature')).toHaveValue('opening');
  });

  it('goes back to versement after saving the versements antérieurs', async () => {
    serve(detail({ flows: [], figures: { ...detail().figures, missingOpening: true } }), []);
    const { user } = await renderApp(`/investments/${PEA}`);

    await user.click(await screen.findByRole('button', { name: 'Saisir les versements antérieurs' }));
    const flow = screen.getByRole('form', { name: 'Nouveau flux' });
    expect(within(flow).getByLabelText('Nature')).toHaveValue('opening');
    await user.type(within(flow).getByLabelText('Montant'), '20000');
    await user.click(within(flow).getByRole('button', { name: 'Ajouter le flux' }));

    await within(flow).findByRole('status');
    expect(within(flow).getByLabelText('Nature')).toHaveValue('contribution');
  });

  it('sets the flow kind to opening again on a second click, even after it was changed', async () => {
    serve(detail({ flows: [], figures: { ...detail().figures, missingOpening: true } }), []);
    const { user } = await renderApp(`/investments/${PEA}`);

    const button = await screen.findByRole('button', { name: 'Saisir les versements antérieurs' });
    await user.click(button);
    const flow = screen.getByRole('form', { name: 'Nouveau flux' });
    await user.selectOptions(within(flow).getByLabelText('Nature'), 'contribution');
    expect(within(flow).getByLabelText('Nature')).toHaveValue('contribution');

    await user.click(button);
    expect(within(flow).getByLabelText('Nature')).toHaveValue('opening');
  });
});
