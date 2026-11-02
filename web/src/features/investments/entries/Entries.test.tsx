import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { pick } from '@/test/controls';
import { serve, type Call } from '../serveEnvelope';
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

type User = Awaited<ReturnType<typeof renderApp>>['user'];

const openValue = async (user: User) => {
  await user.click(await screen.findByRole('button', { name: 'Saisir une valeur' }));
  return within(await screen.findByRole('dialog', { name: 'Nouvelle valeur' }));
};
const openFlow = async (user: User) => {
  await user.click(await screen.findByRole('button', { name: 'Ajouter un flux' }));
  return within(await screen.findByRole('dialog', { name: 'Nouveau flux' }));
};
/** The row's ⋯ menu, opened, then the item picked. */
const rowAction = async (user: User, table: HTMLElement, date: string, item: string) => {
  await user.click(within(table).getByRole('button', { name: new RegExp(`^Actions pour le ${date}`) }));
  await user.click(await screen.findByRole('menuitem', { name: item }));
};
const FLOW_ROW = '15 juin 2025';
const OPENING_ROW = '31 décembre 2024';
const VALUE_ROW = '30 septembre 2025';
const sectionOf = async (name: string) => {
  const heading = await screen.findByRole('heading', { name });
  const section = heading.closest('section');
  if (!section) throw new Error('heading is not inside a section');
  return { heading, section };
};

describe('recording values and flows', () => {
  // The lazy route chunk loaded cold inside the first test races its timeouts under load.
  beforeAll(async () => { await import('../EnvelopeDetailPage'); }, 30_000);

  it('records a new value, prefilled with the last one, then with the one just saved', async () => {
    const calls: Call[] = [];
    serve(detail(), calls);
    const { user, container } = await renderApp(`/investments/${PEA}`);

    const dialog = await openValue(user);
    expect(dialog.getByLabelText('Montant')).toHaveValue('25000');
    await user.clear(dialog.getByLabelText('Montant'));
    await user.type(dialog.getByLabelText('Montant'), '25400,10');
    await user.click(dialog.getByRole('button', { name: 'Enregistrer la valeur' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(screen.getByText('Valeur enregistrée.')).toHaveAttribute('role', 'status');
    expect(calls).toEqual([{ method: 'POST', path: 'valuations', body: expect.objectContaining({ amount: { amount: '25400.10', currency: 'EUR' } }) as unknown }]);
    await waitFor(() => expect(screen.getByRole('button', { name: 'Saisir une valeur' })).toHaveFocus());
    await expectNoAxeViolations(container);

    expect((await openValue(user)).getByLabelText('Montant')).toHaveValue('25400.1');
  });

  it('says a value on a date that already has one replaces it, and leaves one row for that date', async () => {
    const calls: Call[] = [];
    serve(detail(), calls);
    const { user } = await renderApp(`/investments/${PEA}`);

    const dialog = await openValue(user);
    await user.clear(dialog.getByLabelText('Date'));
    await user.type(dialog.getByLabelText('Date'), '30/09/2025');
    expect(dialog.getByText('Une valeur existe déjà à cette date : elle sera remplacée.')).toBeInTheDocument();
    await user.click(dialog.getByRole('button', { name: 'Enregistrer la valeur' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    await user.click(screen.getByRole('tab', { name: 'Valeurs (2)' }));
    const rows = within(await screen.findByRole('table', { name: 'Valeurs de l’enveloppe' })).getAllByRole('row').slice(1);
    expect(rows.filter((r) => r.textContent?.includes('30 septembre 2025'))).toHaveLength(1);
  });

  it.each(['0', '0,00'])('refuses a zero flow amount (%s)', async (zero) => {
    const calls: Call[] = [];
    serve(detail(), calls);
    const { user } = await renderApp(`/investments/${PEA}`);

    const flow = await openFlow(user);
    await user.type(flow.getByLabelText('Montant'), zero);
    await user.click(flow.getByRole('button', { name: 'Ajouter le flux' }));

    expect(await flow.findByText('Saisissez un montant positif comme 1234,56.')).toBeInTheDocument();
    expect(calls).toEqual([]);
  });

  it.each([['value', 'Saisir une valeur', 'Enregistrer la valeur'], ['flow', 'Ajouter un flux', 'Ajouter le flux']])('refuses a future %s date before sending', async (_kind, open, submit) => {
    const calls: Call[] = [];
    serve(detail(), calls);
    const { user } = await renderApp(`/investments/${PEA}`);

    await user.click(await screen.findByRole('button', { name: open }));
    const dialog = within(await screen.findByRole('dialog'));
    await user.clear(dialog.getByLabelText('Date'));
    await user.type(dialog.getByLabelText('Date'), '01/01/2999');
    await user.type(dialog.getByLabelText('Montant'), '10');
    await user.click(dialog.getByRole('button', { name: submit }));

    expect(await dialog.findByText('Cette date est dans le futur.')).toBeInTheDocument();
    expect(dialog.getByLabelText('Date')).toBeInvalid();
    expect(calls).toEqual([]);
  });

  it('refuses a date that is not a date before sending', async () => {
    const calls: Call[] = [];
    serve(detail(), calls);
    const { user } = await renderApp(`/investments/${PEA}`);

    const value = await openValue(user);
    await user.clear(value.getByLabelText('Date'));
    await user.type(value.getByLabelText('Date'), '31/02/2025');
    await user.click(value.getByRole('button', { name: 'Enregistrer la valeur' }));

    expect(await value.findByText('Ce champ est obligatoire.')).toBeInTheDocument();
    expect(value.getByLabelText('Date')).toBeInvalid();
    expect(calls).toEqual([]);
  });

  it('accepts a negative value (an overdrawn account) but refuses a negative flow', async () => {
    const calls: Call[] = [];
    serve(detail({ kind: 'checking' }), calls);
    const { user } = await renderApp(`/investments/${PEA}`);

    const value = await openValue(user);
    await user.clear(value.getByLabelText('Montant'));
    await user.type(value.getByLabelText('Montant'), '-120,50');
    await user.click(value.getByRole('button', { name: 'Enregistrer la valeur' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());

    const flow = await openFlow(user);
    await user.type(flow.getByLabelText('Montant'), '-10');
    await user.click(flow.getByRole('button', { name: 'Ajouter le flux' }));
    expect(await flow.findByText('Saisissez un montant positif comme 1234,56.')).toBeInTheDocument();
    expect(calls.map((c) => c.path)).toEqual(['valuations']);
  });

  it('adds a versement', async () => {
    const calls: Call[] = [];
    serve(detail(), calls);
    const { user } = await renderApp(`/investments/${PEA}`);

    const flow = await openFlow(user);
    await pick(user, 'Nature', 'Retrait');
    await pick(user, 'Nature', 'Versement');
    await user.type(flow.getByLabelText('Montant'), '150');
    await user.click(flow.getByRole('button', { name: 'Ajouter le flux' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(screen.getByText('Flux ajouté.')).toHaveAttribute('role', 'status');
    expect(calls).toEqual([{ method: 'POST', path: 'flows', body: expect.objectContaining({ kind: 'contribution', amount: { amount: '150', currency: 'EUR' } }) as unknown }]);
    await waitFor(() => expect(screen.getByRole('button', { name: 'Ajouter un flux' })).toHaveFocus());
  });

  it('offers versements antérieurs only while there is none', async () => {
    serve(detail(), []);
    const { user } = await renderApp(`/investments/${PEA}`);

    await openFlow(user);
    await user.click(screen.getByRole('combobox', { name: 'Nature' }));
    const list = await screen.findByRole('listbox');
    expect(within(list).getByRole('option', { name: 'Versement' })).toBeInTheDocument();
    expect(within(list).queryByRole('option', { name: 'Versements antérieurs' })).not.toBeInTheDocument();
  });

  it('offers versements antérieurs when correcting the opening itself', async () => {
    serve(detail(), []);
    const { user } = await renderApp(`/investments/${PEA}?tab=flows`);

    await rowAction(user, await screen.findByRole('table', { name: 'Flux de l’enveloppe' }), OPENING_ROW, 'Corriger');
    await screen.findByRole('dialog', { name: 'Corriger le flux' });

    expect(screen.getByRole('combobox', { name: 'Nature' })).toHaveTextContent('Versements antérieurs');
    await user.click(screen.getByRole('combobox', { name: 'Nature' }));
    expect(within(await screen.findByRole('listbox')).getByRole('option', { name: 'Versements antérieurs' })).toBeInTheDocument();
  });

  it('corrects a flow from its row menu, announcing it and moving focus to its section', async () => {
    const calls: Call[] = [];
    serve(detail(), calls);
    const { user } = await renderApp(`/investments/${PEA}?tab=flows`);

    const table = await screen.findByRole('table', { name: 'Flux de l’enveloppe' });
    await rowAction(user, table, FLOW_ROW, 'Corriger');
    const edit = within(await screen.findByRole('dialog', { name: 'Corriger le flux' }));
    await user.clear(edit.getByLabelText('Montant'));
    await user.type(edit.getByLabelText('Montant'), '2500');
    await user.click(edit.getByRole('button', { name: 'Enregistrer' }));

    const { heading, section } = await sectionOf('Flux');
    await waitFor(() => expect(sectionStatus(section)).toHaveTextContent('Flux corrigé.'));
    await within(table).findByText('2 500,00 €');
    await waitFor(() => expect(document.activeElement).toBe(heading));
    expect(calls).toEqual([{ method: 'PUT', path: 'flows/f2', body: expect.objectContaining({ amount: { amount: '2500', currency: 'EUR' } }) as unknown }]);
  });

  it('corrects a value from its row menu, announcing it and moving focus to its section', async () => {
    const calls: Call[] = [];
    serve(detail(), calls);
    const { user } = await renderApp(`/investments/${PEA}?tab=values`);

    const table = await screen.findByRole('table', { name: 'Valeurs de l’enveloppe' });
    await rowAction(user, table, VALUE_ROW, 'Corriger');
    const edit = within(await screen.findByRole('dialog', { name: 'Corriger la valeur' }));
    await user.clear(edit.getByLabelText('Montant'));
    await user.type(edit.getByLabelText('Montant'), '26000');
    await user.click(edit.getByRole('button', { name: 'Enregistrer' }));

    const { heading, section } = await sectionOf('Valeurs');
    await waitFor(() => expect(sectionStatus(section)).toHaveTextContent('Valeur enregistrée.'));
    await within(table).findByText('26 000,00 €');
    await waitFor(() => expect(document.activeElement).toBe(heading));
    expect(calls).toEqual([{ method: 'POST', path: 'valuations', body: expect.objectContaining({ amount: { amount: '26000', currency: 'EUR' } }) as unknown }]);
  });

  it('keeps a flow\'s category and external reference when correcting it', async () => {
    const calls: Call[] = [];
    serve(detail({ flows: [{ id: 'f3', occurredOn: '2025-06-15', kind: 'contribution', amount: eur('50.0000'), category: 'import', externalRef: 'import:42', note: null }] }), calls);
    const { user } = await renderApp(`/investments/${PEA}?tab=flows`);

    await rowAction(user, await screen.findByRole('table', { name: 'Flux de l’enveloppe' }), FLOW_ROW, 'Corriger');
    const edit = within(await screen.findByRole('dialog', { name: 'Corriger le flux' }));
    await user.clear(edit.getByLabelText('Montant'));
    await user.type(edit.getByLabelText('Montant'), '75');
    await user.click(edit.getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(calls).toEqual([
      { method: 'PUT', path: 'flows/f3', body: expect.objectContaining({ category: 'import', externalRef: 'import:42' }) as unknown },
    ]));
  });

  it.each([
    ['flow', 'flows', 'Flux de l’enveloppe', 'Corriger le flux', FLOW_ROW],
    ['value', 'values', 'Valeurs de l’enveloppe', 'Corriger la valeur', VALUE_ROW],
  ])('closes a %s correction on « Annuler », sending nothing and keeping focus on the page', async (_kind, tab, tableName, dialogName, date) => {
    const calls: Call[] = [];
    serve(detail(), calls);
    const { user } = await renderApp(`/investments/${PEA}?tab=${tab}`);

    const table = await screen.findByRole('table', { name: tableName });
    await rowAction(user, table, date, 'Corriger');
    const edit = within(await screen.findByRole('dialog', { name: dialogName }));
    await user.click(edit.getByRole('button', { name: 'Annuler' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    await waitFor(() => expect(within(table).getByRole('button', { name: new RegExp(`^Actions pour le ${date}`) })).toHaveFocus());
    expect(calls).toEqual([]);
  });

  it('deletes a value, announcing « Valeur supprimée. » in its own section', async () => {
    const calls: Call[] = [];
    serve(detail(), calls);
    const { user } = await renderApp(`/investments/${PEA}?tab=values`);

    const table = await screen.findByRole('table', { name: 'Valeurs de l’enveloppe' });
    const { section } = await sectionOf('Valeurs');
    await rowAction(user, table, VALUE_ROW, 'Supprimer');
    await user.click(await screen.findByRole('button', { name: 'Confirmer' }));

    await waitFor(() => expect(sectionStatus(section)).toHaveTextContent('Valeur supprimée.'));
    expect(calls).toEqual([{ method: 'DELETE', path: 'valuations/v2' }]);
  });

  it('hides the versements-antérieurs button once an opening already exists, even while it is reported missing', async () => {
    serve(detail({ figures: { ...detail().figures, missingOpening: true } }), []);
    await renderApp(`/investments/${PEA}`);

    expect(await screen.findByText('Le gain comprend peut-être des versements faits avant le début du suivi.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Saisir les versements antérieurs' })).not.toBeInTheDocument();
  });

  it('names each flow menu after its kind when two flows share a date', async () => {
    const flow = (id: string, kind: string) => ({ id, occurredOn: '2025-06-15', kind, amount: eur('100.0000'), category: null, externalRef: null, note: null });
    serve(detail({ flows: [flow('c1', 'contribution'), flow('c2', 'withdrawal')] }), []);
    await renderApp(`/investments/${PEA}?tab=flows`);

    const table = await screen.findByRole('table', { name: 'Flux de l’enveloppe' });
    expect(within(table).getByRole('button', { name: /^Actions pour le 15 juin 2025 : Versement/ })).toBeInTheDocument();
    expect(within(table).getByRole('button', { name: /^Actions pour le 15 juin 2025 : Retrait/ })).toBeInTheDocument();
  });

  it('deletes a transfer leg after saying the other envelope keeps its own, announcing it and moving focus', async () => {
    const calls: Call[] = [];
    serve(detail({ flows: [{ id: 't1', occurredOn: '2025-06-15', kind: 'withdrawal', amount: eur('100.0000'), category: 'transfer', externalRef: 'transfer:x', note: null }] }), calls);
    const { user } = await renderApp(`/investments/${PEA}?tab=flows`);

    const table = await screen.findByRole('table', { name: 'Flux de l’enveloppe' });
    await user.click(within(table).getByRole('button', { name: new RegExp(`^Actions pour le ${FLOW_ROW}`) }));
    expect(screen.queryByRole('menuitem', { name: 'Corriger' })).not.toBeInTheDocument();
    await user.click(await screen.findByRole('menuitem', { name: 'Supprimer' }));
    expect(await screen.findByText('Seule cette jambe du virement sera supprimée : l’autre enveloppe garde la sienne.')).toBeInTheDocument();
    await user.click(await screen.findByRole('button', { name: 'Confirmer' }));

    const { heading, section } = await sectionOf('Flux');
    await waitFor(() => expect(sectionStatus(section)).toHaveTextContent('Flux supprimé.'));
    await waitFor(() => expect(within(table).getByText('Aucun flux pour l’instant')).toBeInTheDocument());
    await waitFor(() => expect(document.activeElement).toBe(heading));
    expect(calls).toEqual([{ method: 'DELETE', path: 'flows/t1' }]);
  });

  it('announces each deletion in the Flux section again, even when the message repeats', async () => {
    serve(detail(), []);
    const { user } = await renderApp(`/investments/${PEA}?tab=flows`);

    const table = await screen.findByRole('table', { name: 'Flux de l’enveloppe' });
    const { section } = await sectionOf('Flux');

    await rowAction(user, table, FLOW_ROW, 'Supprimer');
    await user.click(await screen.findByRole('button', { name: 'Confirmer' }));
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
    await rowAction(user, table, OPENING_ROW, 'Supprimer');
    await user.click(await screen.findByRole('button', { name: 'Confirmer' }));
    expect(sectionStatus(section)).toBeEmptyDOMElement();

    resolveDelete();
    await waitFor(() => expect(sectionStatus(section)).toHaveTextContent('Flux supprimé.'));
  });

  it('a failed correction after a successful one leaves the section status empty', async () => {
    serve(detail(), []);
    const { user } = await renderApp(`/investments/${PEA}?tab=flows`);

    const table = await screen.findByRole('table', { name: 'Flux de l’enveloppe' });
    const { section } = await sectionOf('Flux');

    // A successful correction first, so the section has something to say.
    await rowAction(user, table, FLOW_ROW, 'Corriger');
    let edit = within(await screen.findByRole('dialog', { name: 'Corriger le flux' }));
    await user.clear(edit.getByLabelText('Montant'));
    await user.type(edit.getByLabelText('Montant'), '2500');
    await user.click(edit.getByRole('button', { name: 'Enregistrer' }));
    await waitFor(() => expect(sectionStatus(section)).toHaveTextContent('Flux corrigé.'));
    await within(table).findByText('2 500,00 €');

    // Opening a correction on another row clears the section; a failed correction never re-sets it.
    server.use(
      http.put('*/api/v1/investments/envelopes/:id/flows/:flowId', () =>
        HttpResponse.json({ status: 409, code: 'core.fact_conflict', title: 'Someone changed this meanwhile.' }, { status: 409 })),
    );
    await rowAction(user, table, OPENING_ROW, 'Corriger');
    expect(sectionStatus(section)).toBeEmptyDOMElement();
    edit = within(await screen.findByRole('dialog', { name: 'Corriger le flux' }));
    await user.clear(edit.getByLabelText('Montant'));
    await user.type(edit.getByLabelText('Montant'), '999');
    await user.click(edit.getByRole('button', { name: 'Enregistrer' }));

    await edit.findByRole('button', { name: /Recharger/ });
    expect(sectionStatus(section)).toBeEmptyDOMElement();
  });

  it('shows a conflict with a way to reload', async () => {
    server.use(
      http.get('*/api/v1/investments/envelopes/:id', () => HttpResponse.json(detail())),
      http.delete('*/api/v1/investments/envelopes/:id/valuations/:valuationId', () =>
        HttpResponse.json({ status: 409, code: 'core.fact_retired', title: 'This value was already corrected or voided.' }, { status: 409 })),
    );
    const { user } = await renderApp(`/investments/${PEA}?tab=values`);

    await rowAction(user, await screen.findByRole('table', { name: 'Valeurs de l’enveloppe' }), VALUE_ROW, 'Supprimer');
    await user.click(await screen.findByRole('button', { name: 'Confirmer' }));

    expect(await screen.findByRole('button', { name: /Recharger/ })).toBeInTheDocument();
  });

  it('reloads the envelope on Recharger, and clears the conflict', async () => {
    let getCalls = 0;
    server.use(
      http.get('*/api/v1/investments/envelopes/:id', () => { getCalls += 1; return HttpResponse.json(detail()); }),
      http.delete('*/api/v1/investments/envelopes/:id/valuations/:valuationId', () =>
        HttpResponse.json({ status: 409, code: 'core.fact_retired', title: 'This value was already corrected or voided.' }, { status: 409 })),
    );
    const { user } = await renderApp(`/investments/${PEA}?tab=values`);

    await rowAction(user, await screen.findByRole('table', { name: 'Valeurs de l’enveloppe' }), VALUE_ROW, 'Supprimer');
    await user.click(await screen.findByRole('button', { name: 'Confirmer' }));
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

    const flow = await openFlow(user);
    await user.type(flow.getByLabelText('Montant'), '10');
    await user.click(flow.getByRole('button', { name: 'Ajouter le flux' }));

    expect(await flow.findByText('Les versements antérieurs sont déjà saisis : corrigez-les.')).toBeInTheDocument();
    expect(flow.getByRole('button', { name: /Recharger/ })).toBeInTheDocument();
  });

  it('places a pathless core.date_in_future on the value form date field', async () => {
    serve(detail(), []);
    server.use(
      http.post('*/api/v1/investments/envelopes/:id/valuations', () =>
        HttpResponse.json({ status: 422, code: 'core.date_in_future', title: 'A value or a flow is not dated after today.' }, { status: 422 })),
    );
    const { user } = await renderApp(`/investments/${PEA}`);

    const form = await openValue(user);
    await user.click(form.getByRole('button', { name: 'Enregistrer la valeur' }));

    expect(await form.findByText('Cette date est dans le futur.')).toBeInTheDocument();
    expect(form.getByLabelText('Date')).toBeInvalid();
  });

  it('opens the flow dialog on versements antérieurs from the missing-opening warning, every time', async () => {
    serve(detail({ flows: [], figures: { ...detail().figures, missingOpening: true } }), []);
    const { user } = await renderApp(`/investments/${PEA}`);

    const warning = await screen.findByRole('button', { name: 'Saisir les versements antérieurs' });
    await user.click(warning);
    expect(await screen.findByRole('combobox', { name: 'Nature' })).toHaveTextContent('Versements antérieurs');
    await user.click(screen.getByRole('button', { name: 'Annuler' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());

    await user.click(screen.getByRole('button', { name: 'Ajouter un flux' }));
    expect(await screen.findByRole('combobox', { name: 'Nature' })).toHaveTextContent('Versement');
    expect(screen.getByRole('combobox', { name: 'Nature' })).not.toHaveTextContent('antérieurs');
  });

  it('goes back to versement after saving the versements antérieurs', async () => {
    const calls: Call[] = [];
    serve(detail({ flows: [], figures: { ...detail().figures, missingOpening: true } }), calls);
    const { user } = await renderApp(`/investments/${PEA}`);

    await user.click(await screen.findByRole('button', { name: 'Saisir les versements antérieurs' }));
    const flow = within(await screen.findByRole('dialog', { name: 'Nouveau flux' }));
    await user.type(flow.getByLabelText('Montant'), '20000');
    await user.click(flow.getByRole('button', { name: 'Ajouter le flux' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(calls).toEqual([{ method: 'POST', path: 'flows', body: expect.objectContaining({ kind: 'opening' }) as unknown }]);
    await user.click(screen.getByRole('button', { name: 'Ajouter un flux' }));
    expect(await screen.findByRole('combobox', { name: 'Nature' })).toHaveTextContent('Versement');
  });
});
