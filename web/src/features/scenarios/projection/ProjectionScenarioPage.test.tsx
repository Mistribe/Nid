import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { ProjectionScenario } from '../api';
import type { ProjectionDocument } from './api';
import { comparison, LOAN_A, LOAN_B, scenarioList } from './testData';

const ID = '0192f0aa-0000-7000-8000-0000000000d9';
const REPAYMENT = { id: '0192f0aa-0000-7000-8000-0000000000a1', kind: 'early_repayment', start: { on: '2027-03-15' }, amount: '20000.00', split: 'highest_rate', effect: 'reduce_term', penalty: '0.00' };
const DOCUMENT: ProjectionDocument = { version: 1, horizon: '10y', assumptions: { inflation: '2.5' }, events: [REPAYMENT] };

function view(over: Partial<ProjectionScenario> = {}): ProjectionScenario {
  const result = comparison({ document: DOCUMENT });
  result.scenario.milestones = [
    { on: '2027-03-15', kind: 'event', label: 'Remboursement anticipé' },
    { on: '2035-09-30', kind: 'loan_end', label: 'Prêt immo', item: LOAN_A },
    { on: '2031-06-30', kind: 'loan_end', label: 'Prêt travaux', item: LOAN_B },
  ];
  result.scenario.warnings = [{ code: 'hidden_targets', detail: '1' }];
  return { id: ID, name: 'Remboursement 2027', visibility: 'shared', scope: 'household', canWrite: true, result, ...over };
}

type Put = { name: string; visibility: string; scope: string; document: ProjectionDocument };

function serve(initial: ProjectionScenario = view()) {
  const puts: Put[] = [];
  const deleted: string[] = [];
  let current = initial;
  server.use(
    http.get('*/api/v1/scenarios/projection/:id', () => HttpResponse.json(current)),
    http.put('*/api/v1/scenarios/projection/:id', async ({ request }) => {
      const body = await request.json() as Put;
      puts.push(body);
      current = { ...current, name: body.name, result: { ...current.result, document: body.document } };
      return HttpResponse.json(current);
    }),
    http.delete('*/api/v1/scenarios/:id', ({ params }) => { deleted.push(String(params.id)); return new HttpResponse(null, { status: 204 }); }),
    http.get('*/api/v1/scenarios', () => HttpResponse.json(scenarioList)),
  );
  return { puts, deleted };
}

const figures = () => within(screen.getByRole('region', { name: 'Chiffres clés' }));
const table = () => screen.getByRole('table', { name: 'Patrimoine en fin d’année, base et scénario' });

describe('ProjectionScenarioPage', () => {
  beforeAll(async () => { await import('./ProjectionScenarioPage'); }, 30_000);

  it('« Comparer » opens the compare page with this scenario, also for a reader who cannot write', async () => {
    serve(view({ canWrite: false }));
    await renderApp(`/scenarios/projection/${ID}`);

    const link = await screen.findByRole('link', { name: 'Comparer' });
    expect(link).toHaveAttribute('href', `/scenarios/compare?ids=${ID}`);
  });

  it('« Comparer » opens a private scenario saved in scope Foyer in scope « Moi », where it is visible', async () => {
    serve(view({ visibility: 'private', scope: 'household' }));
    await renderApp(`/scenarios/projection/${ID}`);

    const link = await screen.findByRole('link', { name: 'Comparer' });
    expect(new URL(link.getAttribute('href')!, 'http://x').searchParams.get('scope')).toBe('me');
  });

  it('« Comparer » opens the comparison in scope « Moi » for a « Moi » scenario', async () => {
    serve(view({ scope: 'me' }));
    await renderApp(`/scenarios/projection/${ID}`);

    const link = await screen.findByRole('link', { name: 'Comparer' });
    expect(new URL(link.getAttribute('href')!, 'http://x').searchParams.get('scope')).toBe('me');
    expect(new URL(link.getAttribute('href')!, 'http://x').searchParams.get('ids')).toBe(ID);
  });

  it('shows the server’s figures inline: the scenario at the horizon, the gap with the base and the last loan’s end', async () => {
    serve();
    await renderApp(`/scenarios/projection/${ID}`);

    expect(await screen.findByRole('heading', { level: 1, name: 'Remboursement 2027' })).toBeInTheDocument();
    expect(figures().getByText('Patrimoine à l’horizon, valeur d’aujourd’hui').nextSibling).toHaveTextContent('41 000,00 €');
    expect(figures().getByText('Écart avec la base, valeur d’aujourd’hui').nextSibling).toHaveTextContent('24 000,00 €');
    expect(figures().getByText('Fin du dernier prêt').nextSibling).toHaveTextContent('30 septembre 2035');
    expect(screen.getByText('Chiffres indicatifs : rendements supposés, pas d’impôt futur ni de pension.')).toBeInTheDocument();
  });

  it('shows the hidden-target warning', async () => {
    serve();
    await renderApp(`/scenarios/projection/${ID}`);
    expect(await screen.findByText('1 événement vise un élément que vous ne voyez pas : il est écarté.')).toBeInTheDocument();
  });

  it('reads the year-end table and the figures in the chosen reading', async () => {
    serve();
    const { user } = await renderApp(`/scenarios/projection/${ID}`);

    const row = await within(await screen.findByRole('table', { name: 'Patrimoine en fin d’année, base et scénario' })).findByRole('row', { name: /2027/ });
    expect(row).toHaveTextContent('16 100,00 €');
    expect(row).toHaveTextContent('11 500,00 €');
    expect(row).toHaveTextContent('-4 600,00 €');

    await user.click(screen.getByRole('radio', { name: 'Valeur courante' }));
    const nominal = within(table()).getByRole('row', { name: /2027/ });
    expect(nominal).toHaveTextContent('17 000,00 €');
    expect(nominal).toHaveTextContent('12 000,00 €');
    expect(nominal).toHaveTextContent('-5 000,00 €');
    expect(figures().getByText('Écart avec la base, valeur courante').nextSibling).toHaveTextContent('25 000,00 €');
    expect(figures().getByText('Patrimoine à l’horizon, valeur courante').nextSibling).toHaveTextContent('43 000,00 €');
  });

  it('keeps the open tab in the URL', async () => {
    serve();
    const { user, router } = await renderApp(`/scenarios/projection/${ID}`);

    await user.click(await screen.findByRole('tab', { name: 'Événements' }));
    expect(router.state.location.search).toMatchObject({ tab: 'events' });
    expect(await screen.findByRole('table', { name: 'Événements du scénario' })).toBeInTheDocument();
    await user.click(screen.getByRole('tab', { name: 'Projection' }));
    expect(router.state.location.search).not.toHaveProperty('tab');
  });

  it('opens the tab the URL names', async () => {
    serve();
    await renderApp(`/scenarios/projection/${ID}?tab=assumptions`);
    expect(await screen.findByRole('tab', { name: 'Hypothèses', selected: true })).toBeInTheDocument();
  });

  it('saves an added event with the whole document it was shown', async () => {
    const { puts } = serve();
    const { user } = await renderApp(`/scenarios/projection/${ID}?tab=events`);

    await user.click(await screen.findByRole('button', { name: 'Ajouter un événement' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Changement d’épargne' }));
    const dialog = await screen.findByRole('dialog');
    await user.type(within(dialog).getByLabelText('Date de début'), '01/01/2027');
    await user.type(within(dialog).getByLabelText('Montant mensuel'), '300');
    await user.click(within(dialog).getByRole('button', { name: 'Ajouter l’événement' }));

    await waitFor(() => expect(puts).toHaveLength(1));
    expect(puts[0]).toEqual({
      name: 'Remboursement 2027', visibility: 'shared', scope: 'household',
      document: { ...DOCUMENT, events: [REPAYMENT, { kind: 'savings_change', start: { on: '2027-01-01' }, monthlyAmount: '300' }] },
    });
    expect(await within(screen.getByRole('table', { name: 'Événements du scénario' })).findByText('Changement d’épargne')).toBeInTheDocument();
  });

  it('shows the change being saved and waits for it before the next one, which builds on it', async () => {
    const { puts } = serve();
    let release = () => {};
    const gate = new Promise<void>((resolve) => { release = resolve; });
    server.use(http.put('*/api/v1/scenarios/projection/:id', async ({ request }) => {
      const body = await request.json() as Put;
      puts.push(body);
      if (puts.length === 1) await gate;
      return HttpResponse.json({ ...view(), result: { ...view().result, document: body.document } });
    }));
    const { user } = await renderApp(`/scenarios/projection/${ID}?tab=events`);

    const add = async (date: string) => {
      await user.click(await screen.findByRole('button', { name: 'Ajouter un événement' }));
      await user.click(await screen.findByRole('menuitem', { name: 'Changement d’épargne' }));
      const dialog = await screen.findByRole('dialog');
      await user.type(within(dialog).getByLabelText('Date de début'), date);
      await user.type(within(dialog).getByLabelText('Montant mensuel'), '300');
      await user.click(within(dialog).getByRole('button', { name: 'Ajouter l’événement' }));
      await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    };
    await add('01/01/2027');

    // Pending: the list shows the change, its actions wait, nothing is announced yet.
    await waitFor(() => expect(puts).toHaveLength(1));
    const events = within(screen.getByRole('table', { name: 'Événements du scénario' }));
    expect(events.getByText('Changement d’épargne')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Ajouter un événement' })).toBeDisabled();
    expect(events.getByRole('button', { name: 'Actions pour Remboursement anticipé' })).toBeDisabled();
    expect(screen.queryByText('Événement ajouté.')).not.toBeInTheDocument();

    release();
    expect(await screen.findByText('Événement ajouté.')).toBeInTheDocument();
    await waitFor(() => expect(screen.getByRole('button', { name: 'Ajouter un événement' })).toBeEnabled());
    await add('01/01/2028');

    await waitFor(() => expect(puts).toHaveLength(2));
    expect(puts[1]!.document.events).toEqual([
      REPAYMENT,
      { kind: 'savings_change', start: { on: '2027-01-01' }, monthlyAmount: '300' },
      { kind: 'savings_change', start: { on: '2028-01-01' }, monthlyAmount: '300' },
    ]);
  });

  it('asks before removing an event, then saves the document without it', async () => {
    const { puts } = serve();
    const { user } = await renderApp(`/scenarios/projection/${ID}?tab=events`);

    const events = await screen.findByRole('table', { name: 'Événements du scénario' });
    await user.click(within(events).getByRole('button', { name: 'Actions pour Remboursement anticipé' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Retirer' }));
    const confirm = await screen.findByRole('alertdialog', { name: 'Retirer l’événement' });
    expect(confirm).toHaveTextContent('« Remboursement anticipé » sera retiré du scénario.');
    expect(puts).toHaveLength(0);
    await user.click(within(confirm).getByRole('button', { name: 'Confirmer' }));

    await waitFor(() => expect(puts).toHaveLength(1));
    expect(puts[0]!.document).toEqual({ ...DOCUMENT, events: [] });
    expect(await screen.findByText('Aucun événement pour l’instant.')).toBeInTheDocument();
  });

  it('shows a refused event’s field under it', async () => {
    serve();
    server.use(http.put('*/api/v1/scenarios/projection/:id', () => HttpResponse.json({
      type: 'about:blank', title: 'Invalid', status: 422, code: 'projection.invalid_field',
      errors: [{ path: 'events[0].amount', code: 'projection.invalid_field', message: 'The amount is above the balance.' }],
    }, { status: 422, headers: { 'Content-Type': 'application/problem+json' } })));
    const { user } = await renderApp(`/scenarios/projection/${ID}?tab=events`);

    await user.click(await screen.findByRole('button', { name: 'Ajouter un événement' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Changement d’épargne' }));
    const dialog = await screen.findByRole('dialog');
    await user.type(within(dialog).getByLabelText('Date de début'), '01/01/2027');
    await user.type(within(dialog).getByLabelText('Montant mensuel'), '300');
    await user.click(within(dialog).getByRole('button', { name: 'Ajouter l’événement' }));

    const events = within(screen.getByRole('table', { name: 'Événements du scénario' }));
    expect(await events.findByText('Montant remboursé : The amount is above the balance.')).toBeInTheDocument();
    expect(events.getByText('Changement d’épargne')).toBeInTheDocument();
  });

  it('edits the assumptions in a dialog and saves them with the events it was shown', async () => {
    const { puts } = serve();
    const { user } = await renderApp(`/scenarios/projection/${ID}?tab=assumptions`);

    const section = await screen.findByRole('region', { name: 'Hypothèses du scénario' });
    expect(within(section).getByText('2,5 %')).toBeInTheDocument();
    await user.click(within(section).getByRole('button', { name: 'Modifier les hypothèses' }));
    const dialog = await screen.findByRole('dialog', { name: 'Modifier les hypothèses' });
    expect(within(dialog).getByLabelText('Inflation (%)')).toHaveValue('2,5');
    await user.clear(within(dialog).getByLabelText('Inflation (%)'));
    await user.type(within(dialog).getByLabelText('Inflation (%)'), '3');
    await user.click(within(dialog).getByRole('radio', { name: '20 ans' }));
    await user.click(within(dialog).getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(puts).toHaveLength(1));
    expect(puts[0]!.document).toEqual({ version: 1, horizon: '20y', assumptions: { inflation: '3' }, events: [REPAYMENT] });
    expect(await screen.findByText('Hypothèses enregistrées.')).toBeInTheDocument();
  });

  it('renames the scenario', async () => {
    const { puts } = serve();
    const { user } = await renderApp(`/scenarios/projection/${ID}`);

    await user.click(await screen.findByRole('button', { name: 'Renommer' }));
    const dialog = await screen.findByRole('dialog', { name: 'Renommer le scénario' });
    await user.clear(within(dialog).getByLabelText('Nom du scénario'));
    await user.type(within(dialog).getByLabelText('Nom du scénario'), 'Remboursement 2028');
    await user.click(within(dialog).getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(puts).toHaveLength(1));
    expect(puts[0]).toEqual({ name: 'Remboursement 2028', visibility: 'shared', scope: 'household', document: DOCUMENT });
    expect(await screen.findByRole('heading', { level: 1, name: 'Remboursement 2028' })).toBeInTheDocument();
  });

  it('deletes the scenario after a confirmation and goes back to the list', async () => {
    const { deleted } = serve();
    const { user, router } = await renderApp(`/scenarios/projection/${ID}`);

    await user.click(await screen.findByRole('button', { name: 'Supprimer' }));
    await user.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Confirmer' }));

    await waitFor(() => expect(deleted).toEqual([ID]));
    await waitFor(() => expect(router.state.location.pathname).toBe('/scenarios'));
  });

  it('is read-only when the reader cannot write it', async () => {
    serve(view({ canWrite: false }));
    const { user } = await renderApp(`/scenarios/projection/${ID}?tab=events`);

    expect(await screen.findByText('Vous pouvez consulter ce scénario, pas le modifier.')).toBeInTheDocument();
    expect(await screen.findByRole('table', { name: 'Événements du scénario' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Ajouter un événement' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /^Actions pour/ })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Renommer' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Supprimer' })).not.toBeInTheDocument();
    await user.click(screen.getByRole('tab', { name: 'Hypothèses' }));
    expect(await screen.findByRole('region', { name: 'Hypothèses du scénario' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Modifier les hypothèses' })).not.toBeInTheDocument();
  });

  it('has no accessibility violation', async () => {
    serve();
    const { container } = await renderApp(`/scenarios/projection/${ID}`);
    await screen.findByRole('table', { name: 'Patrimoine en fin d’année, base et scénario' });
    await expectNoAxeViolations(container);
  });
});
