import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { pick } from '@/test/controls';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { CostReport, Holding, HoldingsView, Movement, Snapshot } from '../api';
import { detail, eur, PEA } from '../testData';
import { signedUnits, trimUnits } from './holdingFormat';
import { startLines } from './SnapshotDialog';

const FUND = '0192f0aa-0000-7000-8000-0000000000f1';
const EURO = '0192f0aa-0000-7000-8000-0000000000f2';

const holding = (over: Partial<Holding> = {}): Holding => ({
  id: FUND, name: 'Monde ESG', isin: 'LU0000000001', kind: 'fund', version: 1,
  line: { units: '12.500000', unitPrice: '200.000000', value: eur('2500.0000'), weightPct: 55.6 },
  figures: {
    asOf: '2025-09-30', value: eur('2500.0000'), moneyIn: eur('2000.0000'), moneyOut: eur('0'), gain: eur('500.0000'), gainPct: 25,
    annualReturnPct: null, explicitFees: eur('12.0000'), gainBeforeFees: eur('512.0000'),
  },
  ...over,
});
const euroFund = holding({
  id: EURO, name: 'Fonds euros', isin: null, kind: 'euro_fund',
  line: { units: null, unitPrice: null, value: eur('2000.0000'), weightPct: 44.4 }, figures: null,
});

const view = (over: Partial<HoldingsView> = {}): HoldingsView => ({
  envelopeId: PEA, currency: 'EUR', holdingsAsOf: '2025-09-30', snapshotId: 's1', complete: true, total: eur('4500.0000'),
  unallocated: eur('20500.0000'), explicitFees: eur('12.0000'), ucSharePct: 55.6, ucShareFloorPct: 60,
  holdings: [holding(), euroFund],
  warnings: [
    { code: 'uc_share_below_floor', holdingId: null, on: '2025-09-30' },
    { code: 'unexplained_units', holdingId: FUND, on: '2025-09-30', units: '-0.800000', proposedFee: eur('60.00') },
  ],
  ...over,
});

const snapshot: Snapshot = {
  id: 's1', asOf: '2025-09-30', complete: true, total: eur('4500.0000'), sourceLabel: null,
  lines: [{ holdingId: FUND, units: '12.500000', unitPrice: '200.000000', value: eur('2500.0000') }, { holdingId: EURO, value: eur('2000.0000') }],
};

type Call = { method: string; path: string; body?: unknown };

function serve(holdings: HoldingsView, calls: Call[] = []) {
  server.use(
    http.get('*/api/v1/investments/envelopes/:id', () => HttpResponse.json(detail())),
    http.get('*/api/v1/investments/envelopes/:id/holdings', () => HttpResponse.json(holdings)),
    http.get('*/api/v1/investments/envelopes/:id/holding-snapshots', () => HttpResponse.json({ envelopeId: PEA, items: [snapshot] })),
    ...(['holdings', 'holding-snapshots', 'switches', 'holdings/:holdingId/movements'] as const).map((path) =>
      http.post(`*/api/v1/investments/envelopes/:id/${path}`, async ({ request }) => {
        calls.push({ method: 'POST', path, body: await request.json() });
        return HttpResponse.json({ id: 'new', warnings: [] }, { status: 201 });
      })),
    http.put('*/api/v1/investments/envelopes/:id/holdings/:holdingId', async ({ request }) => {
      calls.push({ method: 'PUT', path: 'holding', body: await request.json() });
      return HttpResponse.json({ id: FUND });
    }),
  );
  return calls;
}

describe('holdingFormat', () => {
  it('signs units by kind, trims printed units and prefills from the previous snapshot', () => {
    expect([signedUnits('allocation_out', '1.5'), signedUnits('fee', '0.8'), signedUnits('interest', '2')]).toEqual(['-1.5', '-0.8', '2']);
    expect([trimUnits('12.500000'), trimUnits('10.000000'), trimUnits('100'), trimUnits('-0.800000')]).toEqual(['12.5', '10', '100', '0.8']);
    expect(startLines([holding(), euroFund, holding({ id: 'new' })], snapshot).map((l) => l.units)).toEqual(['12.5', '', '']);
  });
});

describe('the « Supports » tab', () => {
  beforeAll(async () => { await import('../EnvelopeDetailPage'); await import('./HoldingsTab'); }, 30_000);

  it('invites to detail the holdings, then creates the first one', async () => {
    const calls = serve(view({ holdings: [], warnings: [], ucSharePct: null, explicitFees: null, unallocated: null }));
    const { user, container } = await renderApp(`/investments/${PEA}?tab=holdings`);

    await user.click(await screen.findByRole('button', { name: 'Détailler les supports' }));
    const dialog = within(await screen.findByRole('dialog', { name: 'Nouveau support' }));
    await user.type(dialog.getByLabelText('Support'), 'Fonds euros');
    await pick(user, 'Type', 'Fonds en euros');
    await user.click(dialog.getByRole('button', { name: 'Ajouter le support' }));

    expect(await screen.findByText('Support ajouté.')).toBeInTheDocument();
    expect(calls).toHaveLength(1);
    expect(calls[0]).toMatchObject({ method: 'POST', path: 'holdings', body: { name: 'Fonds euros', kind: 'euro_fund', isin: null } });
    expect(screen.queryByRole('tab', { name: 'Supports' })).toHaveAttribute('aria-selected', 'true');
    await expectNoAxeViolations(container);
  });

  it('lists the holdings with the server’s figures, the unallocated rest, the UC share and the warnings', async () => {
    serve(view());
    const { user, container } = await renderApp(`/investments/${PEA}?tab=holdings`);

    const table = await screen.findByRole('table', { name: 'Supports de l’enveloppe' });
    const rows = within(table).getAllByRole('row').slice(1);
    expect(rows[0]).toHaveTextContent('Monde ESG');
    expect(rows[0]).toHaveTextContent('12,5');
    expect(rows[0]).toHaveTextContent('2 500,00 €');
    expect(rows[0]).toHaveTextContent('55,6 %');
    expect(rows[0]).toHaveTextContent('+25,0 %');
    expect(rows[1]).toHaveTextContent('Fonds en euros');
    expect(screen.getByText('Non réparti').parentElement).toHaveTextContent('20 500,00 €');
    const figures = screen.getByRole('region', { name: 'Valeur et gain' });
    expect(figures).toHaveTextContent('Part UC55,6 %sous le plancher de 60 %');
    expect(figures).toHaveTextContent('Frais prélevés12,00 €');
    expect(screen.queryByRole('list', { name: 'Avertissements des supports' })).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: '2 avertissements' }));
    const warnings = screen.getByRole('list', { name: 'Avertissements des supports' });
    expect(warnings).toHaveTextContent('Monde ESG : 0,8 parts disparues sans mouvement au 30 septembre 2025, soit environ 60,00 € de frais à enregistrer ?');
    await expectNoAxeViolations(container);
  });

  it('filters the holdings by name or ISIN and by type, the closed ones on demand', async () => {
    const closed = holding({ id: 'closed', name: 'Ancien fonds', closedOn: '2024-06-30', line: null, figures: null });
    serve(view({ holdings: [holding(), euroFund, closed] }));
    const { user } = await renderApp(`/investments/${PEA}?tab=holdings`);

    const table = await screen.findByRole('table', { name: 'Supports de l’enveloppe' });
    const names = () => within(table).getAllByRole('row').slice(1).map((r) => r.textContent);
    expect(names()).toHaveLength(2);
    await user.click(screen.getByRole('switch', { name: 'Afficher les supports clôturés' }));
    expect(names()).toHaveLength(3);
    await pick(user, 'Type', 'Fonds en euros');
    expect(names()).toEqual([expect.stringContaining('Fonds euros')]);
    await pick(user, 'Type', 'Tous les types');
    await user.type(screen.getByRole('searchbox', { name: 'Rechercher un support' }), 'esg');
    expect(names()).toEqual([expect.stringContaining('Monde ESG')]);
    await user.type(screen.getByRole('searchbox', { name: 'Rechercher un support' }), 'zzz');
    expect(screen.getByText('Aucun support ne correspond aux filtres.')).toBeInTheDocument();
  });

  it('records a snapshot prefilled with the previous units, partial when a row is left empty, with the envelope’s value on demand', async () => {
    const calls = serve(view());
    const { user } = await renderApp(`/investments/${PEA}?tab=holdings`);

    await user.click(await screen.findByRole('button', { name: 'Saisir un relevé' }));
    const dialog = within(await screen.findByRole('dialog', { name: 'Relevé des supports' }));
    expect(await dialog.findByLabelText('Parts de Monde ESG')).toHaveValue('12.5');
    expect(dialog.queryByLabelText('Parts de Fonds euros')).not.toBeInTheDocument();
    await user.type(dialog.getByLabelText('Prix unitaire de Monde ESG'), '210');
    await user.click(dialog.getByRole('button', { name: 'Enregistrer le relevé' }));
    expect(await dialog.findByText('Indiquez la valeur imprimée.')).toBeInTheDocument();

    await user.type(dialog.getByLabelText('Valeur de Monde ESG'), '2625,00');
    await user.click(dialog.getByLabelText('Enregistrer aussi la valeur de l’enveloppe'));
    await user.click(dialog.getByRole('button', { name: 'Enregistrer le relevé' }));

    expect(await screen.findByText('Relevé enregistré.')).toBeInTheDocument();
    expect(calls[0]!.body).toMatchObject({
      complete: false, recordValuation: true,
      lines: [{ holdingId: FUND, units: '12.5', unitPrice: '210', value: { amount: '2625.00', currency: 'EUR' } }],
    });
  });

  it('records a movement with units signed by its kind', async () => {
    const calls = serve(view());
    const { user } = await renderApp(`/investments/${PEA}?tab=holdings`);

    await user.click(await screen.findByRole('button', { name: 'Actions pour Monde ESG' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Enregistrer un mouvement' }));
    const dialog = within(await screen.findByRole('dialog', { name: 'Mouvement sur Monde ESG' }));
    await pick(user, 'Nature', 'Part d’un rachat');
    await user.type(dialog.getByLabelText('Montant'), '400');
    await user.type(dialog.getByLabelText('Parts'), '2');
    await user.click(dialog.getByRole('button', { name: 'Enregistrer le mouvement' }));

    expect(await screen.findByText('Mouvement enregistré.')).toBeInTheDocument();
    expect(calls[0]).toMatchObject({ path: 'holdings/:holdingId/movements', body: { kind: 'allocation_out', units: '-2', amount: { amount: '400' } } });
  });

  it('records a switch between two holdings, and closes one', async () => {
    const calls = serve(view());
    const { user } = await renderApp(`/investments/${PEA}?tab=holdings`);

    await user.click(await screen.findByRole('button', { name: 'Arbitrer' }));
    let dialog = within(await screen.findByRole('dialog', { name: 'Arbitrage' }));
    await pick(user, 'Vers', 'Monde ESG');
    await user.type(dialog.getByLabelText('Montant'), '500');
    await user.click(dialog.getByRole('button', { name: 'Enregistrer l’arbitrage' }));
    expect(await dialog.findByText('Choisissez deux supports différents.')).toBeInTheDocument();
    await pick(user, 'Vers', 'Fonds euros');
    await user.type(dialog.getByLabelText('Parts cédées'), '2,5');
    await user.click(dialog.getByRole('button', { name: 'Enregistrer l’arbitrage' }));
    expect(await screen.findByText('Arbitrage enregistré.')).toBeInTheDocument();
    expect(calls[0]!.body).toMatchObject({ maturity: false, from: { holdingId: FUND, units: '-2.5', amount: { amount: '500' } }, to: { holdingId: EURO, units: null } });

    await user.click(screen.getByRole('button', { name: 'Actions pour Fonds euros' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Clôturer' }));
    dialog = within(await screen.findByRole('dialog', { name: 'Clôturer Fonds euros' }));
    await user.click(dialog.getByRole('button', { name: 'Clôturer le support' }));
    expect(await screen.findByText('Support clôturé.')).toBeInTheDocument();
    await waitFor(() => expect(calls[1]).toMatchObject({ method: 'PUT', body: { name: 'Fonds euros', kind: 'euro_fund', version: 1 } }));
    expect((calls[1]!.body as { closedOn: string }).closedOn).toMatch(/^\d{4}-\d{2}-\d{2}$/);
  });
});

describe('a holding’s page', () => {
  beforeAll(async () => { await import('./HoldingPage'); }, 30_000);

  const report = (over: Partial<CostReport> = {}): CostReport => ({
    envelopeId: PEA, holdingId: FUND, year: 2024, grossReturnPct: 8, totalFeesPct: 2.5, netReturnPct: 5.5, contractFeesPct: null, supportFeesPct: null,
    retrocessionPct: 0.85, netAnnualized5yPct: null, sourceLabel: null, estimatedImplicitCost: eur('168.98'), warnings: [], ...over,
  });
  const movements: Movement[] = [
    { id: 'm1', occurredOn: '2024-01-10', kind: 'allocation_in', units: '10.000000', unitPrice: '200.000000', amount: eur('2000.0000') },
    { id: 'm2', occurredOn: '2024-12-31', kind: 'fee', units: '-0.800000', unitPrice: '75.000000', amount: eur('60.0000') },
  ];

  it('shows the figures, the movements newest first and the cost reports with their estimate, and saves a report', async () => {
    serve(view());
    const puts: unknown[] = [];
    server.use(
      http.get('*/api/v1/investments/envelopes/:id/holdings/:holdingId/movements', () => HttpResponse.json({ envelopeId: PEA, holdingId: FUND, items: movements })),
      http.get('*/api/v1/investments/envelopes/:id/holdings/:holdingId/series', () => HttpResponse.json({ envelopeId: PEA, holdingId: FUND, points: [] })),
      http.get('*/api/v1/investments/envelopes/:id/holdings/:holdingId/cost-reports', () => HttpResponse.json({
        envelopeId: PEA, holdingId: FUND, items: [report(), report({ year: 2023, estimatedImplicitCost: null, warnings: [{ code: 'cost_report_inconsistent', holdingId: FUND }] })],
      })),
      http.put('*/api/v1/investments/envelopes/:id/holdings/:holdingId/cost-reports/:year', async ({ request, params }) => {
        puts.push({ year: params.year, body: await request.json() });
        return HttpResponse.json(report());
      }),
    );
    const { user, container } = await renderApp(`/investments/${PEA}/holdings/${FUND}`);

    expect(await screen.findByRole('heading', { name: 'Monde ESG', level: 1 })).toBeInTheDocument();
    const figures = screen.getByRole('region', { name: 'Chiffres du support' });
    expect(figures).toHaveTextContent('Entrées2 000,00 €');
    expect(figures).toHaveTextContent('+500,00 €+25,0 %');
    expect(figures).toHaveTextContent('Frais prélevés12,00 €');
    expect(screen.getByRole('link', { name: 'PEA' })).toHaveAttribute('href', `/investments/${PEA}`);
    await expectNoAxeViolations(container);

    await user.click(screen.getByRole('tab', { name: 'Mouvements (2)' }));
    const rows = within(await screen.findByRole('table', { name: 'Mouvements du support' })).getAllByRole('row').slice(1);
    expect(rows[0]).toHaveTextContent('Frais');
    expect(rows[0]).toHaveTextContent('-0,8');
    expect(rows[1]).toHaveTextContent('Part d’un versement');

    await user.click(screen.getByRole('tab', { name: 'Coûts' }));
    const costs = within(await screen.findByRole('table', { name: 'Coûts' })).getAllByRole('row').slice(1);
    expect(costs[0]).toHaveTextContent('2024');
    expect(costs[0]).toHaveTextContent('2,5 %');
    expect(costs[0]).toHaveTextContent('168,98 €');
    expect(costs[1]).toHaveTextContent('incohérent');
    await expectNoAxeViolations(container);

    await user.click(screen.getByRole('button', { name: 'Autres actions' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Saisir un rapport de frais' }));
    const dialog = within(await screen.findByRole('dialog', { name: 'Rapport de frais de Monde ESG' }));
    await user.clear(dialog.getByLabelText('Année'));
    await user.type(dialog.getByLabelText('Année'), '2024');
    await user.type(dialog.getByLabelText('Rendement brut (%)'), '8');
    await user.type(dialog.getByLabelText('Frais totaux (%)'), '2,5');
    await user.type(dialog.getByLabelText('Rendement net (%)'), '5,5');
    await user.click(dialog.getByRole('button', { name: 'Enregistrer' }));

    expect(await screen.findByText('Rapport de frais enregistré.')).toBeInTheDocument();
    expect(puts).toHaveLength(1);
    expect(puts[0]).toMatchObject({ year: '2024', body: { grossReturnPct: 8, totalFeesPct: 2.5, netReturnPct: 5.5, retrocessionPct: null } });
  });
});
