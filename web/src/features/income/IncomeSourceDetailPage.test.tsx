import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it } from 'vitest';
import { reduceMotion } from '@/test/motion';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { todayIso } from '../subscriptions/today';
import type { IncomeSourceDetail, IncomeSourceOverview } from './api';
import { detail, eur, phase, SALARY, sourceOverview } from './testData';

const serve = (body: IncomeSourceDetail, overview?: IncomeSourceOverview) => server.use(
  http.get('*/api/v1/income/sources/:id', () => HttpResponse.json(body)),
  ...(overview ? [http.get('*/api/v1/income/sources/:id/overview', () => HttpResponse.json(overview))] : []),
);
/** Every month asked for pays p1 on the 1st and p2 on the 15th: the change form has dates to offer. */
const serveDates = () => server.use(http.get('*/api/v1/income/sources/:id/expected', ({ request, params }) => {
  const url = new URL(request.url);
  const from = url.searchParams.get('from')!;
  const on = (day: string) => `${from.slice(0, 8)}${day}`;
  return HttpResponse.json({
    id: params.id, from, to: url.searchParams.get('to'),
    months: [{ year: Number(from.slice(0, 4)), month: Number(from.slice(5, 7)), oneOffs: [], total: eur('0'), occurrences: [
      { phaseId: 'p1', label: 'Salaire de base', on: on('01'), amount: eur('2300.00') },
      { phaseId: 'p2', label: 'Prime annuelle', on: on('15'), amount: eur('1800.00') },
    ] }],
  });
}));
const timeline = () => within(screen.getByRole('list', { name: 'Changements, du plus récent au plus ancien' })).getAllByRole('listitem');
const text = (el: HTMLElement) => el.textContent?.replace(/\s/g, ' ');

describe('IncomeSourceDetailPage', () => {
  reduceMotion();
  // The lazy route chunk loaded cold inside the first test races its timeouts under load.
  beforeAll(async () => { await import('./IncomeSourceDetailPage'); }, 30_000);

  it('shows the figures, the chart and the latest payments first', async () => {
    serve(detail());
    const { container } = await renderApp(`/income/${SALARY}`);

    const title = await screen.findByRole('heading', { name: 'Salaire Alice', level: 1 });
    // The settings form also offers « Salaire » and « Employeur »: the header's own texts are looked up in the header.
    const header = within(title.parentElement as HTMLElement);
    expect(header.getByText('Salaire')).toBeInTheDocument();
    expect(header.getByText('Employeur')).toBeInTheDocument();
    expect(header.getByText('Privé')).toBeInTheDocument();
    const figures = await screen.findByRole('region', { name: 'Chiffres clés' });
    expect(figures).toHaveTextContent('Net versé / mois2 450,00 €lissé sur l’année');
    const parts = within(within(figures).getByRole('list', { name: 'Composantes en cours' })).getAllByRole('listitem');
    expect(parts.map(text)).toEqual(['Prime annuelle — 150,00 € par mois', 'Salaire de base — 2 300,00 € par mois']);
    expect(text(figures)).toContain('Prochain versement1 novembre 2026 · 2 300,00 €Salaire de base');
    expect(text(figures)).toContain('+4,5 %');
    expect(text(figures)).toContain('depuis le début : +11,4 %');
    expect(text(figures)).toContain(`Perçu en ${todayIso().slice(0, 4)}23 400,00 €dont 400,00 € d’exceptionnels`);

    const chart = screen.getByRole('region', { name: 'Évolution' });
    // One stacked layer per component, then the one-offs; the upcoming January is dimmed.
    const layers = chart.querySelectorAll('.recharts-bar');
    expect(layers).toHaveLength(3);
    const base = [...layers[1]!.querySelectorAll('.recharts-bar-rectangle path')];
    expect(base.map((b) => b.getAttribute('fill-opacity'))).toEqual(['1', '0.4']);
    const payments = screen.getByRole('region', { name: 'Derniers versements' });
    expect(within(payments).getAllByRole('row').map(text)).toEqual(['1 octobre 2026Salaire de base2 300,00 €', '1 septembre 2026Salaire de base2 300,00 €']);
    expect(within(payments).getByRole('link', { name: 'Voir tout l’historique' })).toHaveAttribute('href', `/income/${SALARY}?tab=history`);
    expect(within(screen.getByRole('region', { name: 'Derniers exceptionnels' })).getByRole('row')).toHaveTextContent('3 septembre 2026Prime de projet400,00 €');
    // The evolution and the last items come before the timeline.
    expect(chart.compareDocumentPosition(screen.getByRole('heading', { name: 'Changements' })) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    await expectNoAxeViolations(container);
  });

  it('the chart’s table gives the server figures by component, and the period goes to the URL', async () => {
    const seen: string[] = [];
    serve(detail());
    server.use(http.get('*/api/v1/income/sources/:id/overview', ({ request }) => { seen.push(new URL(request.url).search); return HttpResponse.json(sourceOverview()); }));
    const { user, router } = await renderApp(`/income/${SALARY}`);

    const chart = await screen.findByRole('region', { name: 'Évolution' });
    await user.click(within(chart).getByRole('button', { name: 'Afficher en tableau' }));
    const rows = within(within(chart).getByRole('table', { name: 'Revenus de la source, mois par mois' })).getAllByRole('row');
    expect(rows.map(text)).toEqual([
      'MoisPrime annuelleSalaire de baseExceptionnelsTotal',
      'décembre 20241 800,00 €2 200,00 €0,00 €4 000,00 €',
      'janvier 2025 · à venir—2 300,00 €400,00 €2 700,00 €',
    ]);
    expect(seen).toEqual(['?period=12m']);

    await user.click(within(chart).getByRole('radio', { name: '3 ans' }));
    await waitFor(() => expect(router.state.location.search).toMatchObject({ period: '3y' }));
    await waitFor(() => expect(seen).toContain('?period=3y'));
    await user.click(within(chart).getByRole('radio', { name: '12 mois' }));
    await waitFor(() => expect(router.state.location.search).not.toHaveProperty('period'));
  });

  it('a raise appears in the timeline with its previous amount and percent', async () => {
    const overview = sourceOverview();
    serve(detail(), sourceOverview({
      changes: [{ on: '2027-01-01', kind: 'change', phaseId: 'p9', label: 'Salaire de base', before: eur('2300.00'), after: eur('2400.00'), percent: 4.3, upcoming: true }, ...overview.changes],
    }));
    await renderApp(`/income/${SALARY}`);

    await screen.findByRole('list', { name: 'Changements, du plus récent au plus ancien' });
    expect(timeline().map(text)).toEqual([
      '1 janvier 2027·Salaire de base·2 300,00 €→puis2 400,00 €hausse+4,3 %(à venir)',
      '1 janvier 2025·Salaire de base·2 200,00 €→puis2 300,00 €hausse+4,5 %',
      '15 décembre 2024·Prime annuelle·nouvelle composante·1 800,00 €(chaque année)',
      '1 janvier 2024·Salaire de base·début·2 200,00 €(chaque mois)',
    ].map((s) => expect.stringContaining(s) as string));
    // A step of a phase the page does not list (p9 here) has no menu; every listed phase has one, on its own row.
    expect(within(timeline()[0]!).queryByRole('button')).not.toBeInTheDocument();
    expect(within(timeline()[1]!).getByRole('button', { name: 'Actions de la période Salaire de base du 1 janvier 2025' })).toBeInTheDocument();
    expect(within(timeline()[3]!).getByRole('button', { name: 'Actions de la période Salaire de base du 1 janvier 2024' })).toBeInTheDocument();
  });

  it('shows the net taxable income of a phase on the row that starts it', async () => {
    const base = detail();
    serve({ ...base, phases: base.phases.map((p) => (p.id === 'p2' ? { ...p, taxableAmount: eur('1500.00') } : p)) }, sourceOverview());
    await renderApp(`/income/${SALARY}`);

    await screen.findByRole('list', { name: 'Changements, du plus récent au plus ancien' });
    const row = timeline().find((li) => li.textContent?.includes('Prime annuelle'))!;
    expect(row).toHaveTextContent('net imposable 1 500,00 €');
    expect(timeline().find((li) => li.textContent?.includes('Salaire de base'))).not.toHaveTextContent('net imposable');
  });

  it('changing the amount with two running components asks which one', async () => {
    serve(detail());
    serveDates();
    const { user } = await renderApp(`/income/${SALARY}`);

    await user.click(await screen.findByRole('button', { name: 'Changer le montant' }));
    expect((await screen.findAllByRole('menuitem')).map((m) => m.textContent)).toEqual(['Prime annuelle', 'Salaire de base']);
    await user.click(screen.getByRole('menuitem', { name: 'Prime annuelle' }));
    const form = await screen.findByRole('form', { name: 'Changer le montant' });
    expect(await within(form).findByLabelText('Montant net')).toHaveValue('1800');
    await user.click(within(form).getByRole('button', { name: 'Annuler' }));

    await waitFor(() => expect(screen.getByRole('button', { name: 'Changer le montant' })).toHaveFocus());
  });

  it('changing the amount of the only running component opens its form at once', async () => {
    serve(detail({ phases: [phase(), phase({ id: 'p0', startsOn: '2024-01-01', endsOn: '2025-01-01', status: 'past' })] }));
    serveDates();
    const { user } = await renderApp(`/income/${SALARY}`);

    await user.click(await screen.findByRole('button', { name: 'Changer le montant' }));
    expect(await within(await screen.findByRole('form', { name: 'Changer le montant' })).findByLabelText('Montant net')).toHaveValue('2300');
  });

  it('the timeline row menu corrects and deletes a period, and the timeline follows', async () => {
    let current = detail();
    let overview = sourceOverview();
    const sent: string[] = [];
    server.use(
      http.get('*/api/v1/income/sources/:id', () => HttpResponse.json(current)),
      // The overview is the server's: it reads the saved phases again only when the page asks it again.
      http.get('*/api/v1/income/sources/:id/overview', () => HttpResponse.json(overview)),
      http.put('*/api/v1/income/sources/:id/phases/:phaseId', ({ params }) => {
        sent.push(`PUT ${String(params.phaseId)}`);
        overview = { ...overview, changes: overview.changes.map((c) => (c.phaseId === params.phaseId ? { ...c, after: eur('1900.00') } : c)) };
        return HttpResponse.json({ id: params.phaseId });
      }),
      http.delete('*/api/v1/income/sources/:id/phases/:phaseId', ({ params }) => {
        sent.push(`DELETE ${String(params.phaseId)}`);
        current = { ...current, phases: current.phases.filter((p) => p.id !== params.phaseId) };
        overview = { ...overview, changes: overview.changes.filter((c) => c.phaseId !== params.phaseId) };
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const { user } = await renderApp(`/income/${SALARY}`);

    const menu = () => screen.getByRole('button', { name: 'Actions de la période Prime annuelle du 15 décembre 2024' });
    await waitFor(() => expect(menu()).toBeInTheDocument());
    await user.click(menu());
    await user.click(await screen.findByRole('menuitem', { name: 'Corriger' }));
    const form = screen.getByRole('form', { name: 'Corriger la période' });
    expect(within(form).getByLabelText('Montant net')).toHaveValue('1800');
    await user.click(within(form).getByRole('button', { name: 'Enregistrer' }));
    await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('Période corrigée.'));
    expect(screen.getByRole('heading', { name: 'Changements' })).toHaveFocus();
    await waitFor(() => expect(timeline()[1]).toHaveTextContent('15 décembre 2024·Prime annuelle·nouvelle composante·1 900,00 €'));

    await user.click(menu());
    await user.click(await screen.findByRole('menuitem', { name: 'Supprimer' }));
    await user.click(await screen.findByRole('button', { name: 'Confirmer' }));
    await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('Période supprimée.'));
    await waitFor(() => expect(screen.queryByRole('button', { name: /^Actions de la période Prime annuelle/ })).not.toBeInTheDocument());
    await waitFor(() => expect(timeline().map(text).some((r) => r?.includes('Prime annuelle'))).toBe(false));
    expect(sent).toEqual(['PUT p2', 'DELETE p2']);
  });

  it('adds a one-off from the header, and the last one-offs follow', async () => {
    const sent: unknown[] = [];
    let overview = sourceOverview();
    serve(detail());
    server.use(
      http.get('*/api/v1/income/sources/:id/overview', () => HttpResponse.json(overview)),
      http.post('*/api/v1/income/sources/:id/one-offs', async ({ request }) => {
        sent.push(await request.json());
        overview = { ...overview, lastOneOffs: [{ id: 'o9', occurredOn: todayIso(), amount: eur('250.00'), note: null }, ...overview.lastOneOffs] };
        return HttpResponse.json({ id: 'o9' }, { status: 201 });
      }),
    );
    const { user, container } = await renderApp(`/income/${SALARY}`);

    // The visible « Exceptionnel » is named in full for a screen reader.
    await user.click(await screen.findByRole('button', { name: 'Ajouter un revenu exceptionnel' }));
    const form = screen.getByRole('form', { name: 'Nouveau revenu exceptionnel' });
    await user.type(within(form).getByLabelText('Montant net'), '250');
    await expectNoAxeViolations(container);
    await user.click(within(form).getByRole('button', { name: 'Ajouter' }));

    await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('Revenu exceptionnel ajouté.'));
    expect(sent).toEqual([expect.objectContaining({ occurredOn: todayIso(), amount: { amount: '250', currency: 'EUR' } })]);
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    await waitFor(() => expect(within(screen.getByRole('region', { name: 'Derniers exceptionnels' })).getAllByRole('row')).toHaveLength(2));
    expect(within(screen.getByRole('region', { name: 'Derniers exceptionnels' })).getAllByRole('row')[0]).toHaveTextContent('250,00 €');
  });

  it('a change of cadence shows both cadences instead of a percent', async () => {
    serve(detail({ phases: [phase(), phase({ id: 'p0', startsOn: '2024-01-01', endsOn: '2025-01-01', status: 'past', netAmount: eur('1800.00'), every: { count: 1, unit: 'year' } })] }), sourceOverview({
      changes: [
        { on: '2025-01-01', kind: 'change', phaseId: 'p1', label: 'Salaire de base', before: eur('1800.00'), after: eur('2300.00'), percent: null, upcoming: false },
        { on: '2024-01-01', kind: 'start', phaseId: 'p0', label: 'Salaire de base', before: null, after: eur('1800.00'), percent: null, upcoming: false },
      ],
    }));
    await renderApp(`/income/${SALARY}`);

    await screen.findByRole('list', { name: 'Changements, du plus récent au plus ancien' });
    expect(timeline().map(text)).toEqual([
      expect.stringContaining('1 janvier 2025·Salaire de base·1 800,00 €(chaque année)→puis2 300,00 €(chaque mois)') as string,
      // A start shows its own cadence: a yearly amount must not read as monthly.
      expect.stringContaining('1 janvier 2024·Salaire de base·début·1 800,00 €(chaque année)') as string,
    ]);
  });

  it('history lists every payment and filters by component', async () => {
    serve(detail());
    server.use(http.get('*/api/v1/income/sources/:id/expected', ({ request, params }) => {
      const url = new URL(request.url);
      return HttpResponse.json({
        id: params.id, from: url.searchParams.get('from'), to: url.searchParams.get('to'),
        months: [
          { year: 2025, month: 12, total: eur('4100.00'), oneOffs: [], occurrences: [
            { phaseId: 'p1', label: 'Salaire de base', on: '2025-12-01', amount: eur('2300.00') },
            { phaseId: 'p2', label: 'Prime annuelle', on: '2025-12-15', amount: eur('1800.00') },
          ] },
          { year: 2026, month: 1, total: eur('2700.00'), oneOffs: [{ id: 'o1', occurredOn: '2026-01-20', amount: eur('400.00'), note: null }], occurrences: [
            { phaseId: 'p1', label: 'Salaire de base', on: '2026-01-01', amount: eur('2300.00') },
          ] },
        ],
      });
    }));
    const { user, container } = await renderApp(`/income/${SALARY}?tab=history`);

    const region = await screen.findByRole('region', { name: 'Versements' });
    const table = await within(region).findByRole('table', { name: 'Versements, mois par mois' });
    const groups = () => within(table).getAllByRole('rowgroup').slice(1).map((g) => within(g).getAllByRole('row').map(text));
    await within(table).findByRole('columnheader', { name: 'Bulletin' });
    // Newest first: January before December, the one-off of the 20th before the salary of the 1st.
    expect(groups()).toEqual([
      ['janvier 2026Total2 700,00 €Joindre', 'Revenu exceptionnel20 janvier 2026400,00 €', 'Salaire de base1 janvier 20262 300,00 €'],
      ['décembre 2025Total4 100,00 €Joindre', 'Prime annuelle15 décembre 20251 800,00 €', 'Salaire de base1 décembre 20252 300,00 €'],
    ]);
    expect(within(region).getByRole('radio', { name: '12 derniers mois' })).toBeChecked();
    await expectNoAxeViolations(container);

    await user.click(within(region).getByRole('radio', { name: 'Prime annuelle' }));
    expect(groups()).toEqual([['janvier 2026Aucun versementJoindre'], ['décembre 2025Prime annuelle15 décembre 20251 800,00 €Joindre']]);
  });

  it('has no axe violations with the chart as a table and the menus of a writer', async () => {
    serve(detail({ gapSoon: [{ label: 'Salaire de base', endsOn: '2026-12-31' }] }));
    const { user, container } = await renderApp(`/income/${SALARY}`);

    await user.click(await screen.findByRole('button', { name: 'Afficher en tableau' }));
    await screen.findByRole('table', { name: 'Revenus de la source, mois par mois' });
    await expectNoAxeViolations(container);
  });

  it('warns of a component ending with nothing after it, with its date', async () => {
    serve(detail({ gapSoon: [{ label: 'Salaire de base', endsOn: '2026-12-31' }] }));
    const { container } = await renderApp(`/income/${SALARY}`);

    expect(await screen.findByText('Fin de « Salaire de base » le 31 décembre 2026 sans période suivante')).toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('tells the other adult the page is read-only', async () => {
    serve(detail({ canWrite: false, visibility: 'shared' }));
    const { container } = await renderApp(`/income/${SALARY}`);

    expect(await screen.findByText('Vous pouvez consulter ce revenu, pas le modifier.')).toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('shows an ended source with its banner and end date', async () => {
    serve(detail({ ended: true, endedOn: '2025-06-30' }));
    const { container } = await renderApp(`/income/${SALARY}`);

    expect(await screen.findByText('Source terminée')).toBeInTheDocument();
    expect(screen.getByText('Terminée le 30 juin 2025')).toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('says a source without any phase has none yet', async () => {
    serve(detail({ phases: [], monthlyEquivalent: eur('0.00') }), sourceOverview({
      figures: { ...sourceOverview().figures, monthlyNow: eur('0.00'), monthlyParts: [] }, changes: [],
    }));
    await renderApp(`/income/${SALARY}`);

    expect(await screen.findByText('Aucune période pour l’instant.')).toBeInTheDocument();
    expect(await screen.findByText('Aucune composante en cours.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Changer le montant' })).not.toBeInTheDocument();
  });

  it('says an unknown source is not found', async () => {
    server.use(http.get('*/api/v1/income/sources/:id', () => HttpResponse.json({ status: 404, code: null, title: 'Not found' }, { status: 404 })));
    await renderApp(`/income/${SALARY}`);

    expect(await screen.findByText('Cet élément est introuvable.')).toBeInTheDocument();
  });

  it('changes a component’s amount from its row menu', async () => {
    const sent: unknown[] = [];
    server.use(
      http.get('*/api/v1/income/sources/:id', () => HttpResponse.json(detail())),
      http.get('*/api/v1/income/sources/:id/expected', ({ request, params }) => {
        const url = new URL(request.url);
        const [y, m] = [Number(url.searchParams.get('from')!.slice(0, 4)), Number(url.searchParams.get('from')!.slice(5, 7))];
        return HttpResponse.json({
          id: params.id, from: url.searchParams.get('from'), to: url.searchParams.get('to'),
          months: [{ year: y, month: m, oneOffs: [], total: eur('2300.00'), occurrences: [{ phaseId: 'p1', label: 'Salaire de base', on: `${y}-${String(m).padStart(2, '0')}-28`, amount: eur('2300.00') }] }],
        });
      }),
      http.post('*/api/v1/income/sources/:id/phase-changes', async ({ request }) => { sent.push(await request.json()); return HttpResponse.json({ id: 'p10' }, { status: 201 }); }),
    );
    const { user, container } = await renderApp(`/income/${SALARY}`);

    await user.click(await screen.findByRole('button', { name: 'Actions de la période Salaire de base du 1 janvier 2025' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Changer le montant' }));
    const dialog = await screen.findByRole('dialog', { name: 'Changer le montant' });
    await within(dialog).findByRole('combobox', { name: 'Date d’effet' });
    await user.clear(within(dialog).getByLabelText('Montant net'));
    await user.type(within(dialog).getByLabelText('Montant net'), '2500');
    await expectNoAxeViolations(dialog);
    await user.click(within(dialog).getByRole('button', { name: 'Enregistrer le changement' }));

    await waitFor(() => expect(sent).toEqual([expect.objectContaining({ label: 'Salaire de base', netAmount: { amount: '2500', currency: 'EUR' } })]));
    await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('Montant changé.'));
    await expectNoAxeViolations(container);
  });

  it('ends and reopens the source from the ⋯ menu', async () => {
    let current = detail();
    server.use(
      http.get('*/api/v1/income/sources/:id', () => HttpResponse.json(current)),
      http.post('*/api/v1/income/sources/:id/close', () => { current = { ...current, ended: true, endedOn: '2025-06-30' }; return HttpResponse.json({ id: SALARY }); }),
      http.post('*/api/v1/income/sources/:id/reopen', () => { current = { ...current, ended: false, endedOn: null }; return HttpResponse.json({ id: SALARY }); }),
    );
    const { user } = await renderApp(`/income/${SALARY}`);

    await user.click(await screen.findByRole('button', { name: 'Autres actions' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Terminer cette source' }));
    await user.click(within(await screen.findByRole('dialog', { name: 'Terminer la source' })).getByRole('button', { name: 'Terminer' }));
    expect(await screen.findByText('Source terminée', { exact: true })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Autres actions' })).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Rouvrir' }));
    expect(await screen.findByRole('button', { name: 'Autres actions' })).toBeInTheDocument();
    expect(screen.getByRole('status')).toHaveTextContent('Source rouverte.');
  });

  it('a reader sees no write action', async () => {
    serve(detail({ canWrite: false, visibility: 'shared' }));
    const { container } = await renderApp(`/income/${SALARY}`);

    await screen.findByRole('heading', { name: 'Salaire Alice', level: 1 });
    await screen.findByRole('region', { name: 'Évolution' });
    const article = screen.getByRole('article');
    // The chart's table toggle is the only button: no header action, no « Ajouter », no row menu.
    expect(within(article).queryAllByRole('button').map((b) => b.textContent)).toEqual(['Afficher en tableau']);
    expect(within(article).queryByRole('tab', { name: 'Réglages' })).not.toBeInTheDocument();
    await expectNoAxeViolations(container);
  });
});
