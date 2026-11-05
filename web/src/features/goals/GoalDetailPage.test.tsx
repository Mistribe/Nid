import { cleanup, screen, waitFor, within } from '@testing-library/react';
import { delay } from 'msw';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { expectTrailBack } from '@/test/trail';
import { server } from '@/test/server';
import type { GoalDetail } from './api';
import { detail, ENVELOPE, eur, funding, GOAL } from './testData';

const serve = (body: GoalDetail) => server.use(http.get('*/api/v1/goals/:id', () => HttpResponse.json(body)));
const free = (over: Partial<GoalDetail> = {}, goal = {}) => detail({ funding: [], ...over }, goal);
const stat = (label: string) => screen.getByText(label, { selector: 'dt' }).parentElement as HTMLElement;

describe('GoalDetailPage', () => {
  beforeAll(async () => { await import('./GoalDetailPage'); }, 30_000);

  it('loading and error states show no inline back link: the bar leads back to the list', async () => {
    server.use(http.get('*/api/v1/goals/:id', async () => { await delay('infinite'); return HttpResponse.json({}); }));
    await renderApp(`/goals/${GOAL}`);
    expect(await screen.findByLabelText('Chargement de l’objectif')).toBeInTheDocument();
    await waitFor(() => expectTrailBack('Objectifs', '/goals'));
    cleanup();

    server.use(http.get('*/api/v1/goals/:id', () => HttpResponse.json({ status: 404, code: null, title: 'Not found' }, { status: 404 })));
    await renderApp(`/goals/${GOAL}`);
    expect(await screen.findByRole('heading', { level: 1, name: 'Page introuvable' })).toBeInTheDocument();
    await waitFor(() => expectTrailBack('Objectifs', '/goals'));
  });

  it('renders the stat line from the server figures', async () => {
    serve(detail());
    const { container } = await renderApp(`/goals/${GOAL}`);

    expect(await screen.findByRole('heading', { name: 'Voyage', level: 1 })).toBeInTheDocument();
    expect(screen.getAllByRole('link', { name: 'Objectifs' }).map((a) => a.getAttribute('href'))).toContain('/goals');
    expect(stat('Atteint')).toHaveTextContent('2 000,00 €');
    expect(stat('Cible')).toHaveTextContent('5 000,00 €');
    expect(stat('Progression')).toHaveTextContent('40 %');
    expect(stat('Mensualité nécessaire')).toHaveTextContent('200,00 €');
    expect(stat('Rythme mensuel')).toHaveTextContent('150,00 €');
    expect(stat('Date prévue')).toHaveTextContent('1 septembre 2027');
    expect(stat('Statut')).toHaveTextContent('En retard');
    expect(screen.getByText('Projet')).toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('says never when the pace does not reach the target', async () => {
    serve(detail({}, { projectedOn: null, monthlyPace: eur('0.0000') }));
    await renderApp(`/goals/${GOAL}`);
    await screen.findByRole('heading', { name: 'Voyage' });
    expect(stat('Date prévue')).toHaveTextContent('à ce rythme, jamais');
  });

  it('Partial_goal_says_so_without_naming', async () => {
    serve(detail({}, { partial: true, flags: ['partial'] }));
    await renderApp(`/goals/${GOAL}`);
    expect(await screen.findByText('Partiel : une enveloppe liée ne vous est pas visible')).toBeInTheDocument();
  });

  it('Funding_flags_are_labelled', async () => {
    serve(detail({ funding: [
      funding({ flags: ['over_allocated', 'closed_envelope', 'no_value', 'stale', 'missing_currency'] }),
    ] }, { flags: ['closed_envelope'] }));
    await renderApp(`/goals/${GOAL}`);

    const table = await screen.findByRole('table', { name: 'Financement' });
    expect(within(table).getByRole('link', { name: 'PEA Alice' })).toHaveAttribute('href', `/investments/${ENVELOPE}`);
    const row = within(table).getAllByRole('row')[1]!;
    expect(row).toHaveTextContent('50 %');
    expect(row).toHaveTextContent('4 000,00 €');
    for (const label of ['Plus de 100 % de cette enveloppe est affecté', 'Enveloppe clôturée : compte 0', 'Sans valeur', 'Valeur ancienne', 'Pas de taux de change'])
      expect(row).toHaveTextContent(label);
  });

  it('adds an entry with a PUT on its date, and deletes one', async () => {
    const puts: { url: string; body: unknown }[] = [];
    const deletes: string[] = [];
    serve(free({ entries: [{ on: '2026-09-01', amount: eur('1500.0000') }] }));
    server.use(
      http.put('*/api/v1/goals/:id/progress/:on', async ({ request }) => { puts.push({ url: new URL(request.url).pathname, body: await request.json() }); return HttpResponse.json({ id: GOAL }); }),
      http.delete('*/api/v1/goals/:id/progress/:on', ({ request }) => { deletes.push(new URL(request.url).pathname); return new HttpResponse(null, { status: 204 }); }),
    );
    const { user } = await renderApp(`/goals/${GOAL}`);

    const table = await screen.findByRole('table', { name: 'Relevés' });
    expect(within(table).getAllByRole('row')[1]).toHaveTextContent('1 500,00 €');

    await user.click(screen.getByRole('button', { name: 'Ajouter un relevé' }));
    const dialog = await screen.findByRole('dialog');
    const date = within(dialog).getByLabelText('Date');
    await user.clear(date);
    await user.type(date, '02/10/2026');
    await user.type(within(dialog).getByLabelText('Montant'), '1800,50');
    await user.click(within(dialog).getByRole('button', { name: 'Enregistrer' }));
    await waitFor(() => expect(puts).toEqual([{ url: `/api/v1/goals/${GOAL}/progress/2026-10-02`, body: { amount: '1800.50' } }]));

    await user.click(screen.getAllByRole('button', { name: /Actions pour le relevé du 1 septembre 2026/ })[0]!);
    await user.click(await screen.findByRole('menuitem', { name: 'Supprimer' }));
    await user.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Confirmer' }));
    await waitFor(() => expect(deletes).toEqual([`/api/v1/goals/${GOAL}/progress/2026-09-01`]));
  });

  it('Emergency_fund_without_commitments', async () => {
    serve(detail({ months: 3, funding: [], commitments: [] }, {
      kind: 'emergency_fund', target: null, reached: null, progressPct: null, remaining: null, monthlyNeeded: null, monthlyPace: null,
      projectedOn: null, status: null, targetDate: null, flags: ['no_commitments'],
    }));
    await renderApp(`/goals/${GOAL}`);

    await screen.findByRole('heading', { name: 'Voyage' });
    expect(screen.getAllByText('Aucun engagement : la cible n’est pas calculable.').length).toBeGreaterThan(0);
    expect(stat('Atteint')).toHaveTextContent('—');
    expect(stat('Cible')).toHaveTextContent('—');
    expect(stat('Statut')).toHaveTextContent('—');
    expect(document.body).not.toHaveTextContent('0,00');
    expect(document.body).not.toHaveTextContent('NaN');
  });

  it('lists the counted outflows of an emergency fund', async () => {
    serve(detail({ months: 3, funding: [], commitments: [
      { source: 'subscriptions', count: 4, monthly: eur('120.0000'), missingCurrency: null },
      { source: 'loans', count: 1, monthly: null, missingCurrency: 'USD' },
      { source: 'property_costs', count: 2, monthly: eur('80.0000'), missingCurrency: null },
    ] }, { kind: 'emergency_fund', flags: ['missing_currency'] }));
    const { user } = await renderApp(`/goals/${GOAL}`);

    await user.click(await screen.findByRole('tab', { name: 'Dépenses comptées' }));
    const table = await screen.findByRole('table', { name: 'Dépenses comptées' });
    const [subs, loans, costs] = within(table).getAllByRole('row').slice(1);
    expect(costs).toHaveTextContent('Charges des biens');
    expect(subs).toHaveTextContent('Abonnements');
    expect(subs).toHaveTextContent('4');
    expect(subs).toHaveTextContent('120,00 €');
    expect(loans).toHaveTextContent('Prêts');
    expect(loans).toHaveTextContent('Pas de taux de change (USD)');
    expect(screen.getByText('Sur 3 mois de dépenses')).toBeInTheDocument();
  });

  it('shows funding and counted outflows for a funded emergency fund', async () => {
    serve(detail({ months: 3, funding: [funding({ flags: ['stale'] })], commitments: [{ source: 'loans', count: 1, monthly: eur('50.0000') }] }, { kind: 'emergency_fund' }));
    const { user } = await renderApp(`/goals/${GOAL}`);
    expect(await screen.findByRole('tab', { name: 'Financement' })).toBeInTheDocument();
    expect(within(await screen.findByRole('table', { name: 'Financement' })).getAllByRole('row')[1]).toHaveTextContent('Valeur ancienne');
    await user.click(screen.getByRole('tab', { name: 'Dépenses comptées' }));
    expect(await screen.findByRole('table', { name: 'Dépenses comptées' })).toBeInTheDocument();
  });

  it('lets an unfunded emergency fund add readings', async () => {
    serve(detail({ months: 3, funding: [] }, { kind: 'emergency_fund' }));
    await renderApp(`/goals/${GOAL}`);
    expect(await screen.findByRole('button', { name: 'Ajouter un relevé' })).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: 'Dépenses comptées' })).toBeInTheDocument();
  });

  it('closes a goal after confirming; a closed goal offers to reopen and hides the entry actions', async () => {
    const posted: string[] = [];
    serve(free());
    server.use(http.post('*/api/v1/goals/:id/close', ({ request }) => { posted.push(new URL(request.url).pathname); return HttpResponse.json({ id: GOAL }); }));
    const { user } = await renderApp(`/goals/${GOAL}`);
    await user.click(await screen.findByRole('button', { name: 'Clôturer' }));
    await user.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Confirmer' }));
    await waitFor(() => expect(posted).toEqual([`/api/v1/goals/${GOAL}/close`]));
  });

  it('shows Rouvrir and no edit action on a closed goal', async () => {
    serve(free({ entries: [{ on: '2026-09-01', amount: eur('1500.0000') }] }, { closed: true }));
    await renderApp(`/goals/${GOAL}`);
    expect(await screen.findByRole('button', { name: 'Rouvrir' })).toBeEnabled();
    expect(screen.queryByRole('button', { name: 'Clôturer' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Ajouter un relevé' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Actions pour le relevé/ })).not.toBeInTheDocument();
  });

  it('shows a partial funded goal without a visible envelope as funded: the funding tab, no entry form', async () => {
    serve(free({}, { partial: true, flags: ['partial'] }));
    await renderApp(`/goals/${GOAL}`);
    expect(await screen.findByRole('tab', { name: 'Financement' })).toBeInTheDocument();
    expect(screen.getByRole('tabpanel')).toHaveTextContent('Aucune enveloppe liée ne vous est visible.');
    expect(screen.getByText('Partiel : une enveloppe liée ne vous est pas visible')).toBeInTheDocument();
    expect(screen.queryByRole('tab', { name: 'Relevés' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Ajouter un relevé' })).not.toBeInTheDocument();
  });

  it('hides every write action from a reader who cannot write, and says so', async () => {
    serve(free({ entries: [{ on: '2026-09-01', amount: eur('1500.0000') }] }, { canWrite: false }));
    await renderApp(`/goals/${GOAL}`);
    expect(await screen.findByText('Vous pouvez consulter cet objectif, pas le modifier.')).toBeInTheDocument();
    for (const name of ['Modifier', 'Clôturer', 'Ajouter un relevé'])
      expect(screen.queryByRole('button', { name })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Actions pour le relevé/ })).not.toBeInTheDocument();
  });

  it('offers no Rouvrir on a closed goal the reader cannot write', async () => {
    serve(free({}, { closed: true, canWrite: false }));
    await renderApp(`/goals/${GOAL}`);
    await screen.findByRole('heading', { name: 'Voyage' });
    expect(screen.queryByRole('button', { name: 'Rouvrir' })).not.toBeInTheDocument();
  });

  it('says why a reopen is refused', async () => {
    serve(detail({}, { closed: true }));
    server.use(http.post('*/api/v1/goals/:id/reopen', () => HttpResponse.json({ status: 409, code: 'goals.share_exceeded', title: 'Over.' }, { status: 409 })));
    const { user } = await renderApp(`/goals/${GOAL}`);
    await user.click(await screen.findByRole('button', { name: 'Rouvrir' }));
    expect(await screen.findByText('Ses enveloppes sont déjà affectées à plus de 100 % : baissez une part avant de rouvrir.')).toBeInTheDocument();
  });

  it('shows a dash, not never, for a reached goal', async () => {
    serve(detail({}, { status: 'reached', projectedOn: null, monthlyPace: eur('0.0000') }));
    await renderApp(`/goals/${GOAL}`);
    await screen.findByRole('heading', { name: 'Voyage' });
    expect(stat('Date prévue')).not.toHaveTextContent('jamais');
    expect(stat('Date prévue')).toHaveTextContent('—');
  });

  it('shows a failed delete on its own row only', async () => {
    serve(free({ entries: [{ on: '2026-09-01', amount: eur('1500.0000') }, { on: '2026-08-01', amount: eur('1000.0000') }] }));
    server.use(http.delete('*/api/v1/goals/:id/progress/:on', () => HttpResponse.json({ status: 409, code: 'goals.goal_closed', title: 'Closed.' }, { status: 409 })));
    const { user } = await renderApp(`/goals/${GOAL}`);
    const table = await screen.findByRole('table', { name: 'Relevés' });
    await user.click(within(table).getByRole('button', { name: /Actions pour le relevé du 1 septembre 2026/ }));
    await user.click(await screen.findByRole('menuitem', { name: 'Supprimer' }));
    await user.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Confirmer' }));

    const [september, august] = within(table).getAllByRole('row').slice(1);
    expect(await within(september!).findByRole('alert')).toBeInTheDocument();
    expect(within(august!).queryByRole('alert')).not.toBeInTheDocument();
  });

  it('formats shares to two decimals and progress to one', async () => {
    serve(detail({ funding: [funding({ shareBp: 3333 })] }, { progressPct: 40.55 }));
    await renderApp(`/goals/${GOAL}`);
    const table = await screen.findByRole('table', { name: 'Financement' });
    expect(within(table).getAllByRole('row')[1]).toHaveTextContent('33,33 %');
    expect(stat('Progression')).toHaveTextContent('40,6 %');
  });

  it('says what a dash stands for, shows the access and a closed badge', async () => {
    serve(detail({}, { closed: true, visibility: 'private', progressPct: null, monthlyPace: null, projectedOn: null }));
    await renderApp(`/goals/${GOAL}`);
    await screen.findByRole('heading', { name: 'Voyage' });
    expect(stat('Progression')).toHaveTextContent('Non disponible');
    expect(stat('Rythme mensuel')).toHaveTextContent('Non disponible');
    expect(stat('Date prévue')).toHaveTextContent('Non disponible');
    expect(screen.getByText('Privé')).toBeInTheDocument();
    expect(screen.getByText('Clôturé')).toBeInTheDocument();
  });
});
