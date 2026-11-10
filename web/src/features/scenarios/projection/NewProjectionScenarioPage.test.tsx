import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { ALICE, comparison, loans, persons, properties, scenarioList } from './testData';

const CREATED = '0192f0aa-0000-7000-8000-0000000000d9';
type Body = { scope: string; document: { horizon: string; assumptions?: Record<string, unknown> | null; events?: Record<string, unknown>[] } };

function serve(observedInflation: string | null = null) {
  const simulated: Body[] = [];
  const created: unknown[] = [];
  const base = { ...comparison().base, observedInflation };
  server.use(
    http.get('*/api/v1/core/persons', () => HttpResponse.json(persons)),
    http.get('*/api/v1/projection', () => HttpResponse.json(base)),
    http.get('*/api/v1/realestate/loans', () => HttpResponse.json(loans)),
    http.get('*/api/v1/realestate/properties', () => HttpResponse.json(properties)),
    http.get('*/api/v1/scenarios', () => HttpResponse.json(scenarioList)),
    http.post('*/api/v1/scenarios/projection/simulate', async ({ request }) => {
      const body = await request.json() as Body;
      simulated.push(body);
      // A document without events answers its own gap, so the panel shows which document it last read.
      return HttpResponse.json(body.document.events?.length ? comparison() : comparison({ gapNominal: '1100.00', gapReal: '1000.00' }));
    }),
    http.post('*/api/v1/scenarios/projection', async ({ request }) => { created.push(await request.json()); return HttpResponse.json({ id: CREATED }, { status: 201 }); }),
  );
  return { simulated, created };
}

type User = Awaited<ReturnType<typeof renderApp>>['user'];
const next = (user: User) => user.click(screen.getByRole('button', { name: 'Continuer' }));
const step = () => screen.getByRole('listitem', { current: 'step' });
const panel = () => screen.getByRole('complementary', { name: 'Résultat' });

async function general(user: User) {
  await user.type(await screen.findByLabelText('Nom du scénario'), 'Retraite anticipée');
  await user.click(await screen.findByRole('radio', { name: 'Retraite de Alice' }));
  await next(user);
}

async function repayment(user: User) {
  await user.click(screen.getByRole('button', { name: 'Ajouter un événement' }));
  await user.click(await screen.findByRole('menuitem', { name: 'Remboursement anticipé' }));
  const dialog = await screen.findByRole('dialog');
  await user.type(within(dialog).getByLabelText('Date du remboursement'), '15/03/2027');
  await user.type(within(dialog).getByLabelText('Montant remboursé'), '20000');
  await user.click(within(dialog).getByRole('button', { name: 'Ajouter l’événement' }));
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
}

const REPAYMENT = { kind: 'early_repayment', start: { on: '2027-03-15' }, amount: '20000', split: 'highest_rate', effect: 'reduce_term', penalty: '0' };

describe('NewProjectionScenarioPage', () => {
  beforeAll(async () => { await import('./NewProjectionScenarioPage'); }, 30_000);

  it('goes through the three steps, simulates at the side, then creates the scenario and opens it', async () => {
    const { simulated, created } = serve();
    const { user, router } = await renderApp('/scenarios/new?kind=projection');

    expect(await screen.findByRole('heading', { name: 'Nouvelle projection' })).toBeInTheDocument();
    expect(await screen.findByRole('radio', { name: '10 ans' })).toBeChecked();
    await general(user);

    expect(step()).toHaveTextContent('Événements');
    expect(screen.getByText('Aucun événement pour l’instant.')).toBeInTheDocument();
    await repayment(user);
    const events = within(screen.getByRole('table', { name: 'Événements du scénario' }));
    expect(events.getByText('Remboursement anticipé')).toBeInTheDocument();
    expect(events.getByText('20 000,00 €')).toBeInTheDocument();
    expect(events.getByText('15/03/2027')).toBeInTheDocument();

    await waitFor(() => expect(simulated.at(-1)?.document.events).toEqual([REPAYMENT]));
    expect(simulated.at(-1)).toMatchObject({ scope: 'household', document: { version: 1, horizon: `retire:${ALICE}` } });
    expect(await within(panel()).findByText('25 000,00 €')).toBeInTheDocument();
    expect(within(panel()).getByText('24 000,00 €')).toBeInTheDocument();
    expect(within(panel()).getByText('Un événement daté dans le passé est pris aujourd’hui.')).toBeInTheDocument();

    await next(user);
    expect(step()).toHaveTextContent('Hypothèses');
    await user.click(screen.getByRole('button', { name: 'Changer les hypothèses du foyer' }));
    await user.type(screen.getByLabelText('Inflation (%)'), '3,5');
    await waitFor(() => expect(simulated.at(-1)?.document.assumptions).toEqual({ inflation: '3.5' }));

    await user.click(screen.getByRole('button', { name: 'Créer le scénario' }));
    await waitFor(() => expect(router.state.location.pathname).toBe(`/scenarios/projection/${CREATED}`));
    expect(created).toEqual([{
      name: 'Retraite anticipée', visibility: 'shared', scope: 'household', owners: [{ personId: ALICE, shareBp: 10000 }],
      document: { version: 1, horizon: `retire:${ALICE}`, assumptions: { inflation: '3.5' }, events: [REPAYMENT] },
    }]);
  });

  it('hints the observed inflation on the current year', async () => {
    serve('3.1');
    const { user } = await renderApp('/scenarios/new?kind=projection');
    const year = new Date().getFullYear();

    await general(user);
    await next(user);
    await user.click(screen.getByRole('button', { name: 'Changer les hypothèses du foyer' }));

    expect(screen.getByLabelText(`Inflation ${year} (%)`)).toHaveAccessibleDescription(/3,1 %/);
    expect(screen.getByLabelText(`Inflation ${year + 1} (%)`)).not.toHaveAccessibleDescription(/3,1 %/);
  });

  it('sends the yearly overrides it was typed', async () => {
    const { simulated } = serve();
    const { user } = await renderApp('/scenarios/new?kind=projection');
    const year = new Date().getFullYear();

    await general(user);
    await next(user);
    await user.click(screen.getByRole('button', { name: 'Changer les hypothèses du foyer' }));
    await user.type(screen.getByLabelText(`Inflation ${year} (%)`), '2,5');
    await user.type(screen.getByLabelText(`Liquidités ${year + 1} (%)`), '3');
    await user.type(screen.getByLabelText('Évolution des revenus : inflation + x %'), '0,5');
    await user.type(screen.getByLabelText('Volatilité Actions (%)'), '20');
    await waitFor(() => expect(simulated.at(-1)?.document.assumptions).toEqual({
      inflationByYear: { [year]: '2.5' }, classRatesByYear: { cash: { [year + 1]: '3' } }, incomeGrowth: '0.5', classVolatility: { equity: '20' },
    }));
  });

  it('sends the withdrawal rate and living cost typed, and omits them when empty', async () => {
    const { simulated } = serve();
    const { user } = await renderApp('/scenarios/new?kind=projection');

    await general(user);
    await next(user);
    await user.click(screen.getByRole('button', { name: 'Changer les hypothèses du foyer' }));
    await user.type(screen.getByLabelText('Taux de retrait (%)'), '3,5');
    await waitFor(() => expect(simulated.at(-1)?.document.assumptions).toEqual({ withdrawalRate: '3.5' }));
    await user.type(screen.getByLabelText('Coût de la vie mensuel'), '2500');
    await waitFor(() => expect(simulated.at(-1)?.document.assumptions).toEqual({ withdrawalRate: '3.5', monthlyLivingCost: '2500' }));
    await user.clear(screen.getByLabelText('Taux de retrait (%)'));
    await waitFor(() => expect(simulated.at(-1)?.document.assumptions).toEqual({ monthlyLivingCost: '2500' }));
  });

  it('sends no simulation while a typed assumption cannot be read', async () => {
    const { simulated } = serve();
    const { user } = await renderApp('/scenarios/new?kind=projection');

    await general(user);
    await next(user);
    await user.click(screen.getByRole('button', { name: 'Changer les hypothèses du foyer' }));
    await waitFor(() => expect(simulated.length).toBeGreaterThan(0));
    await user.type(screen.getByLabelText('Inflation (%)'), '3,');
    await new Promise((r) => setTimeout(r, 700));
    expect(simulated.every((s) => s.document.assumptions == null)).toBe(true);
    expect(screen.getByLabelText('Inflation (%)')).toHaveAttribute('aria-invalid', 'true');
  });

  it('brings a refused event back to its step, its field marked in the dialog', async () => {
    const { created } = serve();
    server.use(http.post('*/api/v1/scenarios/projection', async ({ request }) => {
      created.push(await request.json());
      return HttpResponse.json({
        type: 'about:blank', title: 'Invalid', status: 422,
        errors: [{ path: 'events[0].amount', code: 'projection.invalid_field', message: 'The amount is required and positive.' }],
      }, { status: 422, headers: { 'Content-Type': 'application/problem+json' } });
    }));
    const { user } = await renderApp('/scenarios/new?kind=projection');

    await general(user);
    await repayment(user);
    await next(user);
    await user.click(screen.getByRole('button', { name: 'Créer le scénario' }));

    await waitFor(() => expect(step()).toHaveTextContent('Événements'));
    const table = screen.getByRole('table', { name: 'Événements du scénario' });
    expect(within(table).getByText('Montant remboursé : The amount is required and positive.')).toBeInTheDocument();
    await user.click(within(table).getByRole('button', { name: 'Actions pour Remboursement anticipé' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Modifier' }));
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByLabelText('Montant remboursé')).toHaveAttribute('aria-invalid', 'true');
    expect(within(dialog).getByText('The amount is required and positive.')).toBeInTheDocument();
  });

  it('removes an event from the list once confirmed', async () => {
    serve();
    const { user } = await renderApp('/scenarios/new?kind=projection');

    await general(user);
    await repayment(user);
    // The panel reads the document with the event…
    expect(await within(panel()).findByText('25 000,00 €')).toBeInTheDocument();
    const table = screen.getByRole('table', { name: 'Événements du scénario' });
    await user.click(within(table).getByRole('button', { name: 'Actions pour Remboursement anticipé' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Retirer' }));
    await user.click(within(await screen.findByRole('alertdialog', { name: 'Retirer l’événement' })).getByRole('button', { name: 'Confirmer' }));
    expect(await screen.findByText('Aucun événement pour l’instant.')).toBeInTheDocument();
    // …then the one without it, asked again or from the cache.
    expect(await within(panel()).findByText('1 100,00 €', {}, { timeout: 3000 })).toBeInTheDocument();
    expect(within(panel()).queryByText('25 000,00 €')).not.toBeInTheDocument();
  });


  it('keeps the purchase creation without a kind', async () => {
    serve();
    await renderApp('/scenarios/new');
    expect(await screen.findByRole('heading', { name: 'Nouveau scénario' })).toBeInTheDocument();
  });

  it('has no accessibility violation', async () => {
    serve();
    const { container, user } = await renderApp('/scenarios/new?kind=projection');
    await general(user);
    await repayment(user);
    await within(panel()).findByText('25 000,00 €');
    await expectNoAxeViolations(container);
  });
});
