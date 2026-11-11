import { cleanup, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { delay, http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it } from 'vitest';
import { renderApp } from '@/test/renderApp';
import { expectTrailBack } from '@/test/trail';
import { server } from '@/test/server';
import { expectHeaderActionRule, openMore } from '@/test/headerActions';
import type { ChildcareDetail } from './api';
import { link, list, summary } from '../documents/testData';
import type { StatementList } from './api';
import { ARRANGEMENT_ID, detail, eur, list as childcareList, slot, statements, summary as childcareSummary, terms, twoWeekTypes } from './testData';

const url = `/childcare/${ARRANGEMENT_ID}`;

function serve(body: ChildcareDetail, writes: { path: string; body: unknown }[] = [], answer: () => Response = () => HttpResponse.json({ id: ARRANGEMENT_ID })) {
  const record = async ({ request }: { request: Request }) => {
    writes.push({ path: new URL(request.url).pathname, body: await request.json().catch(() => null) });
    return answer();
  };
  server.use(
    http.get('*/api/v1/childcare/:id', () => HttpResponse.json(body)),
    http.get('*/api/v1/childcare/:id/statements', () => HttpResponse.json(statements())),
    http.get('*/api/v1/documents', () => HttpResponse.json(list([]))),
    http.put('*/api/v1/childcare/:id', record),
    http.post('*/api/v1/childcare/:id/revisions', record),
    http.post('*/api/v1/childcare/:id/close', record),
    http.put('*/api/v1/childcare/:id/gaps', record),
    http.put('*/api/v1/childcare/:id/revisions/:from', record),
    http.put('*/api/v1/childcare/:id/week-overrides/:monday', record),
  );
}

beforeAll(async () => { await import('./ChildcareDetailPage'); }, 30_000);

describe('ChildcareDetailPage', () => {
  it('loading and error states show no inline back link: the bar leads back to the list', async () => {
    server.use(http.get('*/api/v1/childcare/:id', async () => { await delay('infinite'); return HttpResponse.json({}); }));
    await renderApp(url);
    expect(await screen.findByLabelText('Chargement de la garde')).toBeInTheDocument();
    await waitFor(() => expectTrailBack('Garde', '/childcare'));
    cleanup();

    server.use(http.get('*/api/v1/childcare/:id', () => HttpResponse.json({ status: 404, code: null, title: 'Not found' }, { status: 404 })));
    await renderApp(url);
    expect(await screen.findByRole('heading', { level: 1, name: 'Page introuvable' })).toBeInTheDocument();
    await waitFor(() => expectTrailBack('Garde', '/childcare'));
  });

  it('shows the server figures inline and the template', async () => {
    serve(detail());
    const { user } = await renderApp(url);
    const figures = await screen.findByRole('region', { name: 'Chiffres clés' });
    expect(figures).toHaveTextContent('626,37');
    expect(figures).toHaveTextContent('38 h');
    expect(figures).toHaveTextContent('46');
    expect(figures).toHaveTextContent('Semaines sans accueil6 semainesà placer');
    await user.click(screen.getByRole('tab', { name: 'Planning' }));
    const planning = screen.getByRole('list', { name: 'Planning type' });
    expect(within(planning).getAllByRole('listitem')[0]).toHaveTextContent('Lundi 08:00 – 17:30 · déjeuner');
  });

  it('one week type shows no weeks list', async () => {
    serve(detail());
    const { user } = await renderApp(url);
    await user.click(await screen.findByRole('tab', { name: 'Planning' }));
    expect(screen.queryByRole('table', { name: 'Semaines du contrat' })).not.toBeInTheDocument();
    expect(screen.queryByText(/Semaine longue/)).not.toBeInTheDocument();
  });

  it('two week types: one block each, the allocation and a switch per week', async () => {
    const writes: { path: string; body: unknown }[] = [];
    serve(twoWeekTypes(), writes);
    const { user } = await renderApp(url);
    expect(await screen.findByRole('region', { name: 'Chiffres clés' })).toHaveTextContent('151,67 h');
    await user.click(screen.getByRole('tab', { name: 'Planning' }));
    expect(screen.getByRole('list', { name: 'Semaine longue' })).toBeInTheDocument();
    expect(screen.getByText('Semaine longue · 39 h · 23 semaines')).toBeInTheDocument();
    expect(screen.getByText('22 / 23')).toBeInTheDocument();
    const weeks = screen.getByRole('table', { name: 'Semaines du contrat' });
    const rows = within(weeks).getAllByRole('row').slice(1);
    expect(rows).toHaveLength(3);
    expect(rows[1]).toHaveTextContent('exception');
    expect(rows[2]).toHaveTextContent('sans accueil');
    await user.click(within(rows[0]!).getByRole('radio', { name: 'Semaine courte' }));
    await waitFor(() => expect(writes).toContainEqual({ path: '/api/v1/childcare/' + ARRANGEMENT_ID + '/week-overrides/2025-09-01', body: { weekType: 1 } }));
  });

  it('disables the week switch while the override is in flight', async () => {
    let release!: () => void;
    const gate = new Promise<void>((r) => { release = r; });
    serve(twoWeekTypes());
    server.use(http.put('*/api/v1/childcare/:id/week-overrides/:monday', async () => { await gate; return HttpResponse.json({ id: ARRANGEMENT_ID }); }));
    const { user } = await renderApp(url);
    await user.click(await screen.findByRole('tab', { name: 'Planning' }));
    const rows = within(screen.getByRole('table', { name: 'Semaines du contrat' })).getAllByRole('row').slice(1);
    await user.click(within(rows[0]!).getByRole('radio', { name: 'Semaine courte' }));
    await waitFor(() => expect(within(rows[0]!).getByRole('radio', { name: 'Semaine courte' })).toBeDisabled());
    expect(screen.getByRole('radiogroup', { name: 'Semaine du 1 sept. 2025' })).toBeInTheDocument();
    release();
    await waitFor(() => expect(within(rows[0]!).getByRole('radio', { name: 'Semaine courte' })).toBeEnabled());
  });

  it('opens the validated months in the explorer, and shows no link without one', async () => {
    serve(detail());
    const { user, router } = await renderApp(url);
    await user.click(await screen.findByRole('link', { name: 'Voir la série' }));
    await waitFor(() => expect(router.state.location.pathname).toBe('/series'));
    expect(router.state.location.search).toMatchObject({ s: `childcare:${ARRANGEMENT_ID}:monthly` });
    cleanup();

    server.use(http.get('*/api/v1/childcare/:id/statements', () => HttpResponse.json({ id: ARRANGEMENT_ID, months: [{ month: '2025-10', state: 'none', cost: null }] })));
    await renderApp(url);
    await screen.findByRole('list', { name: 'Relevés 2025' });
    expect(screen.queryByRole('link', { name: 'Voir la série' })).not.toBeInTheDocument();
  });

  it('statements tab shows each month with its state and cost', async () => {
    serve(detail());
    await renderApp(url);
    expect(await screen.findByRole('tab', { name: 'Relevés', selected: true })).toBeInTheDocument();
    const [october, september] = within(await screen.findByRole('list', { name: 'Relevés 2025' })).getAllByRole('listitem');
    expect(october).toHaveTextContent('octobre 2025 À préparer —');
    expect(september).toHaveTextContent('septembre 2025 Validé 660,47');
    expect(within(september!).getByRole('link', { name: /septembre 2025/ })).toHaveAttribute('href', `/childcare/${ARRANGEMENT_ID}/statements/2025-09`);
  });

  it('groups the statements by year', async () => {
    serve(detail());
    server.use(http.get('*/api/v1/childcare/:id/statements', () => HttpResponse.json({ id: ARRANGEMENT_ID, months: [
      { month: '2026-01', state: 'draft', cost: eur('610.00') },
      { month: '2025-12', state: 'validated', cost: eur('660.47') },
    ] })));
    await renderApp(url);
    expect(await screen.findByRole('list', { name: 'Relevés 2026' })).toHaveTextContent('janvier 2026 Brouillon 610,00');
    expect(screen.getByRole('list', { name: 'Relevés 2025' })).toHaveTextContent('décembre 2025');
  });

  it('the title opens the other contracts, the closed ones included', async () => {
    serve(detail());
    server.use(http.get('*/api/v1/childcare', () => HttpResponse.json(childcareList([
      childcareSummary(), childcareSummary({ id: 'b', name: 'Ancienne garde', closed: true, start: '2023-09-01', end: '2025-06-30' }),
    ]))));
    const { user } = await renderApp(url);
    await user.click(await screen.findByRole('button', { name: 'Changer de contrat' }));
    const menu = await screen.findByRole('menu');
    expect(within(menu).getByRole('menuitem', { name: /Ancienne garde/ })).toHaveAttribute('href', '/childcare/b');
    expect(within(menu).getByRole('menuitem', { name: /Garde de Léa/ })).toHaveAttribute('aria-current', 'page');
    expect(within(menu).getByRole('menuitem', { name: 'Nouvelle garde' })).toHaveAttribute('href', '/childcare/new');
  });

  it('shows the CMG estimate', async () => {
    serve(detail({ cmgEstimate: eur('120.33') }));
    await renderApp(url);
    const figures = await screen.findByRole('region', { name: 'Chiffres clés' });
    expect(figures).toHaveTextContent('CMG estimé');
    expect(figures).toHaveTextContent('120,33');
  });

  it('shows « — » and why while no validated month carries a CMG (#385)', async () => {
    serve(detail({ cmgEstimate: null }));
    await renderApp(url);
    const figures = await screen.findByRole('region', { name: 'Chiffres clés' });
    expect(figures).toHaveTextContent('CMG estimé—après un mois validé avec un CMG');
  });

  it('the documents tab lists the arrangement\'s documents', async () => {
    serve(detail());
    server.use(http.get('*/api/v1/documents', () => HttpResponse.json(list([summary({ title: 'Contrat de travail', links: [link({ targetId: ARRANGEMENT_ID, anchorItemId: ARRANGEMENT_ID })] })]))));
    const { user } = await renderApp(url);
    await user.click(await screen.findByRole('tab', { name: 'Documents' }));
    expect((await screen.findAllByText('Contrat de travail')).length).toBeGreaterThan(0);
  });

  it('a validated month without payslip says so', async () => {
    serve(detail());
    const months: StatementList = { id: ARRANGEMENT_ID, months: [
      { month: '2025-09', state: 'validated', cost: eur('660.47') },
      { month: '2025-08', state: 'validated', cost: eur('600.00') },
    ] };
    server.use(
      http.get('*/api/v1/childcare/:id/statements', () => HttpResponse.json(months)),
      http.get('*/api/v1/documents', () => HttpResponse.json(list([summary({
        title: 'Bulletin septembre', documentDate: '2025-09-30',
        links: [link({ targetKind: 'core.item', targetId: ARRANGEMENT_ID, anchorItemId: ARRANGEMENT_ID, role: 'payslip' })],
      })]))),
    );
    await renderApp(url);
    const rows = within(await screen.findByRole('list', { name: 'Relevés 2025' })).getAllByRole('listitem');
    await waitFor(() => expect(rows.find((r) => r.textContent?.includes('août 2025'))).toHaveTextContent('Pas de bulletin'));
    expect(rows.find((r) => r.textContent?.includes('septembre 2025'))).not.toHaveTextContent('Pas de bulletin');
  });

  it('shows the leave counter', async () => {
    serve(detail());
    await renderApp(url);
    expect(await screen.findByRole('region', { name: 'Chiffres clés' })).toHaveTextContent('Congés acquis3 jours');
  });

  it('a revision dated after today waits: the terms in force show, the revision reads « À venir »', async () => {
    serve(detail({ terms: [terms({ to: '2025-12-31' }), terms({ from: '2026-01-01', programmedWeeks: 44, monthlyBasePay: eur('655.50') })] }));
    await renderApp(url);
    expect(await screen.findByRole('region', { name: 'Chiffres clés' })).toHaveTextContent('Semaines programmées46');
    await userEvent.click(screen.getByRole('tab', { name: 'Révisions' }));
    const rows = within(screen.getByRole('table', { name: 'Révisions' })).getAllByRole('row');
    expect(rows[1]).toHaveTextContent('À venir');
    expect(rows[2]).not.toHaveTextContent('En cours');
  });

  it('corrects a past revision\'s unpaid absences from its row (#192)', async () => {
    const writes: { path: string; body: unknown }[] = [];
    serve(detail({ terms: [terms({ to: '2025-12-31' }), terms({ from: '2026-01-01' })] }), writes);
    const { user } = await renderApp(url);
    await user.click(await screen.findByRole('tab', { name: 'Révisions' }));
    const table = screen.getByRole('table', { name: 'Révisions' });
    expect(within(table).getAllByRole('row')[2]).toHaveTextContent("Enfant malade, Absence de l'assistante");
    await user.click(within(table).getByRole('button', { name: 'Actions sur la période du 1 sept. 2025' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Modifier les absences non payées' }));
    const dialog = await screen.findByRole('dialog', { name: 'Absences non payées' });
    expect(dialog).toHaveTextContent('Période du 1 sept. 2025 au 31 déc. 2025.');
    await user.click(within(dialog).getByRole('checkbox', { name: 'RTT employeur' }));
    await user.click(within(dialog).getByRole('button', { name: 'Enregistrer' }));
    await waitFor(() => expect(writes).toHaveLength(1));
    expect(writes[0]).toEqual({ path: `/api/v1/childcare/${ARRANGEMENT_ID}/revisions/2025-09-01`, body: { unpaidAbsences: ['child_sick', 'carer_absent', 'parent_rtt'] } });
  });

  it('a closed arrangement offers no correction of its revisions', async () => {
    serve(detail({}, { closed: true }));
    const { user } = await renderApp(url);
    await user.click(await screen.findByRole('tab', { name: 'Révisions' }));
    expect(screen.queryByRole('button', { name: /^Actions sur la période/ })).not.toBeInTheDocument();
  });

  it('revising posts the new terms from the date', async () => {
    const writes: { path: string; body: unknown }[] = [];
    serve(detail(), writes);
    const { user } = await renderApp(url);
    await user.click(await screen.findByRole('button', { name: 'Réviser' }));
    const dialog = await screen.findByRole('dialog');
    await user.type(within(dialog).getByLabelText('Date de la révision'), '01/01/2026');
    await user.clear(within(dialog).getByLabelText('Taux horaire net (€)'));
    await user.type(within(dialog).getByLabelText('Taux horaire net (€)'), '4,20');
    await user.click(within(dialog).getByRole('button', { name: 'Enregistrer la révision' }));
    await waitFor(() => expect(writes).toHaveLength(1));
    const slot = (day: number) => ({ day, arrival: '08:00', departure: '17:30', lunch: true, snack: false });
    expect(writes[0]).toEqual({ path: `/api/v1/childcare/${ARRANGEMENT_ID}/revisions`, body: { terms: {
      from: '2026-01-01', netHourlyRate: '4.20', overtimePct: '10', upkeepPer9h: '3.80', lunchPrice: '3.2', lunchProvider: 'nanny', snackProvider: 'nanny', unpaidAbsences: ['child_sick', 'carer_absent'],
      weekTypes: [{ name: 'Semaine type', weeks: 46, schedule: [1, 2, 4, 5].map(slot) }],
    } } });
  });

  it('the revision dialog is prefilled with both week types', async () => {
    serve(detail({ terms: [terms({ weekTypes: [
      { name: 'Longue', weeks: 23, schedule: [1, 2].map((d) => slot(d)), weeklyHours: '19.00' },
      { name: 'Courte', weeks: 22, schedule: [1].map((d) => slot(d)), weeklyHours: '9.50' },
    ] })] }));
    const { user } = await renderApp(url);
    await user.click(await screen.findByRole('button', { name: 'Réviser' }));
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByLabelText('Nom de la semaine type 1')).toHaveValue('Longue');
    expect(within(dialog).getByLabelText('Semaines de la semaine type 2')).toHaveValue('22');
    const second = within(dialog).getByRole('group', { name: 'Semaine type 2' });
    expect(within(second).getByRole('checkbox', { name: 'Lundi' })).toBeChecked();
    expect(within(second).getByRole('checkbox', { name: 'Mardi' })).not.toBeChecked();
  });

  it('a conflict on edit says the arrangement changed', async () => {
    serve(detail(), [], () => HttpResponse.json({ type: 'about:blank', title: 'Conflict', status: 409, code: 'childcare.conflict' }, { status: 409 }));
    const { user } = await renderApp(url);
    await user.click(await screen.findByRole('button', { name: 'Modifier' }));
    const dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByRole('button', { name: 'Enregistrer' }));
    expect(await within(dialog).findByRole('alert')).toHaveTextContent('Cette garde a changé entre-temps');
  });

  it('ending the arrangement asks for its last day', async () => {
    const writes: { path: string; body: unknown }[] = [];
    serve(detail(), writes);
    const { user } = await renderApp(url);
    await screen.findByRole('button', { name: 'Modifier' });
    await openMore(user);
    await user.click(await screen.findByRole('menuitem', { name: 'Terminer la garde' }));
    const dialog = await screen.findByRole('dialog');
    await user.type(within(dialog).getByLabelText('Dernier jour'), '31/10/2025');
    await user.click(within(dialog).getByRole('button', { name: 'Terminer' }));
    await waitFor(() => expect(writes).toEqual([{ path: `/api/v1/childcare/${ARRANGEMENT_ID}/close`, body: { on: '2025-10-31' } }]));
  });

  it('a revision refused on its date or its weeks says why', async () => {
    serve(detail(), [], () => HttpResponse.json({ type: 'about:blank', title: 'Unprocessable', status: 422, code: 'request.invalid', errors: [
      { path: 'terms.from', code: 'childcare.revision_not_after', message: 'A revision starts after the current terms\' start.' },
      { path: 'gaps', code: 'childcare.gaps_too_many', message: 'Too many.' },
    ] }, { status: 422 }));
    const { user } = await renderApp(url);
    await user.click(await screen.findByRole('button', { name: 'Réviser' }));
    const dialog = await screen.findByRole('dialog');
    await user.type(within(dialog).getByLabelText('Date de la révision'), '01/01/2026');
    await user.click(within(dialog).getByRole('button', { name: 'Enregistrer la révision' }));
    expect(await within(dialog).findByText('La révision commence après le début des conditions en cours.')).toBeInTheDocument();
    expect(within(dialog).getByText(/Plus de semaines sans accueil sur une année de contrat/)).toBeInTheDocument();
  });

  it('ending before the current terms says why', async () => {
    serve(detail(), [], () => HttpResponse.json({ type: 'about:blank', title: 'Unprocessable', status: 422, code: 'request.invalid', errors: [
      { path: 'on', code: 'childcare.end_before_terms', message: 'The end comes on or after the current terms\' start.' },
    ] }, { status: 422 }));
    const { user } = await renderApp(url);
    await screen.findByRole('button', { name: 'Modifier' });
    await openMore(user);
    await user.click(await screen.findByRole('menuitem', { name: 'Terminer la garde' }));
    const dialog = await screen.findByRole('dialog');
    await user.type(within(dialog).getByLabelText('Dernier jour'), '31/08/2025');
    await user.click(within(dialog).getByRole('button', { name: 'Terminer' }));
    expect(await within(dialog).findByText('Le dernier jour vient au plus tôt au début des conditions en cours.')).toBeInTheDocument();
  });

  it('keeps two buttons in the header and the ending in the ⋯ menu', async () => {
    serve(detail());
    const { user, container } = await renderApp(url);
    await screen.findByRole('button', { name: 'Modifier' });
    expectHeaderActionRule(container);
    expect(screen.queryByRole('button', { name: 'Terminer la garde' })).not.toBeInTheDocument();
    await openMore(user);
    await screen.findByRole('menuitem', { name: 'Terminer la garde' });
    expect(screen.getAllByRole('menuitem').map((i) => i.textContent)).toEqual(['Terminer la garde', 'Partage…', 'Supprimer…']);
  });

  it('a reader without write sees no actions', async () => {
    serve(detail({}, { canWrite: false }));
    await renderApp(url);
    expect(await screen.findByRole('region', { name: 'Chiffres clés' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Réviser' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Modifier' })).not.toBeInTheDocument();
  });

  describe('deletion', () => {
    const preview = (over: object = {}) => ({ name: 'Garde de Léa', version: 4, imported: false, removes: [], blockers: [], ...over });

    it('Supprimer… is the last menu item of an open contract', async () => {
      serve(detail());
      const { user } = await renderApp(url);
      await screen.findByRole('button', { name: 'Modifier' });
      await openMore(user);
      await waitFor(() => expect(screen.getAllByRole('menuitem').at(-1)).toHaveTextContent('Supprimer…'));
    });

    it('a closed contract keeps Supprimer… last, and its dialog has no close hint', async () => {
      serve(detail({}, { closed: true, end: '2025-10-31' }));
      server.use(http.get('*/api/v1/childcare/:id/deletion', () => HttpResponse.json(preview())));
      const { user } = await renderApp(url);
      await screen.findByRole('button', { name: 'Rouvrir' });
      await openMore(user);
      await waitFor(() => expect(screen.getAllByRole('menuitem').map((i) => i.textContent)).toEqual(['Partage…', 'Supprimer…']));
      await user.click(screen.getByRole('menuitem', { name: 'Supprimer…' }));
      const dialog = await screen.findByRole('dialog', { name: 'Supprimer Garde de Léa' });
      expect(await within(dialog).findByLabelText(/Tapez/)).toBeInTheDocument();
      expect(dialog).not.toHaveTextContent('utilisez plutôt');
    });

    it('a reader has no Supprimer…', async () => {
      serve(detail({}, { canWrite: false }));
      await renderApp(url);
      expect(await screen.findByRole('region', { name: 'Chiffres clés' })).toBeInTheDocument();
      expect(screen.queryByRole('button', { name: 'Autres actions' })).not.toBeInTheDocument();
      expect(screen.queryByText('Supprimer…')).not.toBeInTheDocument();
    });

    it('validated months block: the count and the way out, no delete button', async () => {
      serve(detail());
      server.use(http.get('*/api/v1/childcare/:id/deletion', () => HttpResponse.json(preview({ blockers: [{ key: 'validated_statements', count: 2, items: [] }] }))));
      const { user } = await renderApp(url);
      await screen.findByRole('button', { name: 'Modifier' });
      await openMore(user);
      await user.click(await screen.findByRole('menuitem', { name: 'Supprimer…' }));
      const dialog = await screen.findByRole('dialog', { name: 'Supprimer Garde de Léa' });
      expect(await within(dialog).findByText(/2 mois validés/)).toBeInTheDocument();
      expect(dialog).toHaveTextContent('Rouvrez d’abord les mois validés, du plus récent au plus ancien.');
      expect(within(dialog).queryByRole('button', { name: 'Supprimer définitivement' })).not.toBeInTheDocument();
    });

    it('deletes after the name is typed, then leaves for the list, with the close hint', async () => {
      serve(detail());
      server.use(
        http.get('*/api/v1/childcare/:id/deletion', () => HttpResponse.json(preview({ removes: [{ key: 'statements', count: 3 }] }))),
        http.delete('*/api/v1/childcare/:id', () => new HttpResponse(null, { status: 204 })),
      );
      const { user, router } = await renderApp(url);
      await screen.findByRole('button', { name: 'Modifier' });
      await openMore(user);
      await user.click(await screen.findByRole('menuitem', { name: 'Supprimer…' }));
      const dialog = await screen.findByRole('dialog', { name: 'Supprimer Garde de Léa' });
      expect(await within(dialog).findByText(/3 relevés mensuels/)).toBeInTheDocument();
      expect(dialog).toHaveTextContent('Terminer la garde');
      await user.type(await within(dialog).findByLabelText(/Tapez/), 'Garde de Léa');
      await user.click(within(dialog).getByRole('button', { name: 'Supprimer définitivement' }));
      await waitFor(() => expect(router.state.location.pathname).toBe('/childcare'));
    });
  });
});
