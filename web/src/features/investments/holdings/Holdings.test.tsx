import { cleanup, screen, waitFor, within } from '@testing-library/react';
import { delay } from 'msw';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { pick } from '@/test/controls';
import { expectLabelBefore } from '@/test/filters';
import { renderApp } from '@/test/renderApp';
import { expectTrailBack } from '@/test/trail';
import { server } from '@/test/server';
import type { CostReport, Holding, HoldingBreakdown, HoldingsView, Movement, Snapshot } from '@/api/investments';
import { detail, eur, PEA } from '../testData';
import { signedUnits, trimUnits } from './holdingFormat';
import { startLines } from './SnapshotDialog';
import { expectOnePrimary } from '@/test/headerActions';

const FUND = '0192f0aa-0000-7000-8000-0000000000f1';
const EURO = '0192f0aa-0000-7000-8000-0000000000f2';

const holding = (over: Partial<Holding> = {}): Holding => ({
  id: FUND, name: 'Monde ESG', isin: 'LU0000000001', kind: 'fund', version: 1, autoPrice: true,
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
    http.get('*/api/v1/investments/envelopes/:id/holdings/:holdingId/breakdown', () => HttpResponse.json({ status: 404 }, { status: 404 })),
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

    await screen.findByRole('button', { name: 'Détailler les supports' });
    expectOnePrimary(container);
    await user.click(screen.getByRole('button', { name: 'Détailler les supports' }));
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

  it('offers « Cours automatique » once an ISIN is typed, and sends it off when unchecked', async () => {
    const calls = serve(view({ holdings: [], warnings: [], ucSharePct: null, explicitFees: null, unallocated: null }));
    const { user } = await renderApp(`/investments/${PEA}?tab=holdings`);

    await user.click(await screen.findByRole('button', { name: 'Détailler les supports' }));
    const dialog = within(await screen.findByRole('dialog', { name: 'Nouveau support' }));
    await user.type(dialog.getByLabelText('Support'), 'Monde ESG');
    expect(dialog.queryByRole('switch', { name: 'Cours automatique' })).not.toBeInTheDocument();
    await user.type(dialog.getByLabelText('ISIN (facultatif)'), 'LU0000000001');
    const auto = dialog.getByRole('switch', { name: 'Cours automatique' });
    expect(auto).toBeChecked();
    expect(dialog.getByText('Nid récupère son cours chaque soir de semaine')).toBeInTheDocument();
    await user.click(auto);
    await user.click(dialog.getByRole('button', { name: 'Ajouter le support' }));

    await screen.findByText('Support ajouté.');
    expect(calls[0]).toMatchObject({ path: 'holdings', body: { isin: 'LU0000000001', autoPrice: false } });
  });

  it('lists the holdings with the server’s figures, the unallocated rest, the UC share and the warnings', async () => {
    serve(view());
    const { user, container } = await renderApp(`/investments/${PEA}?tab=holdings`);

    const table = await screen.findByRole('table', { name: 'Supports de l’enveloppe' });
    expectOnePrimary(container);
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

  it('shows each holding\u2019s latest price, accepts a refused one then refetches, and lists what is not up to date', async () => {
    const calls = serve(view());
    let current = view({
      holdings: [holding({ price: { value: '210.500000', currency: 'EUR', onDate: '2025-10-01', source: 'auto', status: 'rejected', previous: '200.000000' } }), euroFund],
      estimate: {
        value: eur('4700.00'), asOf: '2025-10-02', basedOn: '2025-09-30', converted: true,
        stale: [{ holdingId: FUND, reason: 'rejected_price' }, { holdingId: EURO, reason: 'no_units' }], missingReason: null,
      },
    });
    server.use(
      http.get('*/api/v1/investments/envelopes/:id/holdings', () => HttpResponse.json(current)),
      http.post('*/api/v1/investments/envelopes/:id/holdings/:holdingId/price/accept', async ({ request }) => {
        calls.push({ method: 'POST', path: 'accept', body: await request.json() });
        current = view({ holdings: [holding({ price: { value: '210.500000', currency: 'EUR', onDate: '2025-10-01', source: 'auto', status: 'accepted', previous: '200.000000' } }), euroFund] });
        return HttpResponse.json(true);
      }),
    );
    const { user, container } = await renderApp(`/investments/${PEA}?tab=holdings`);

    const table = await screen.findByRole('table', { name: 'Supports de l’enveloppe' });
    const row = within(table).getAllByRole('row')[1]!;
    expect(row).toHaveTextContent('210,5 EUR');
    expect(row).toHaveTextContent('1 octobre 2025');
    expect(row).toHaveTextContent('Cours refusé');
    // Once in the page's figures, once as the tab's total line, below « Non réparti » it includes.
    const total = screen.getAllByText('Valeur estimée au 2 octobre 2025').at(-1)!.closest('p')!;
    expect(total).toHaveTextContent('4 700,00 €');
    expect(screen.getByText('Non réparti').compareDocumentPosition(total) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    const list = screen.getByRole('list', { name: 'Pas à jour' });
    expect(list).toHaveTextContent('Monde ESG : Cours refusé');
    expect(list).toHaveTextContent('Fonds euros : Parts inconnues');
    await expectNoAxeViolations(container);

    // The card layout (below md) offers the same badge and action.
    const card = within(screen.getByRole('list', { name: 'Supports de l’enveloppe' }));
    expect(card.getByText('Cours refusé')).toBeInTheDocument();
    expect(card.getByRole('button', { name: 'Accepter ce cours' })).toBeInTheDocument();

    await user.click(within(row).getByRole('button', { name: 'Accepter ce cours' }));
    expect(await screen.findByText('Le cours trouvé (210,5) s’écarte de plus de 20 % du précédent (200). L’appliquer quand même ?')).toBeInTheDocument();
    await user.click(within(screen.getByRole('alertdialog')).getByRole('button', { name: 'Confirmer' }));
    await waitFor(() => expect(calls).toContainEqual({ method: 'POST', path: 'accept', body: { onDate: '2025-10-01' } }));
    await waitFor(() => expect(within(table).queryByText('Cours refusé')).not.toBeInTheDocument());
  });

  it('says why there is no estimate, and nothing of the prices when the feature is off', async () => {
    const calls = serve(view({ estimate: { value: null, asOf: null, basedOn: null, converted: false, stale: [], missingReason: 'incomplete_snapshot' } }));
    const first = await renderApp(`/investments/${PEA}?tab=holdings`);
    expect(await screen.findByText('Le dernier relevé est partiel')).toBeInTheDocument();
    first.unmount();
    serve(view({ estimate: { value: null, asOf: null, basedOn: null, converted: false, stale: [], missingReason: 'disabled' } }), calls);
    const { user } = await renderApp(`/investments/${PEA}?tab=holdings`);
    const table = await screen.findByRole('table', { name: 'Supports de l’enveloppe' });
    expect(screen.queryByText(/Saisissez un relevé|relevé est partiel|Valeur estimée/)).not.toBeInTheDocument();
    expect(within(table).queryByRole('columnheader', { name: 'Cours' })).not.toBeInTheDocument();
    await user.click(within(table).getByRole('button', { name: 'Actions pour Monde ESG' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Modifier' }));
    const dialog = within(await screen.findByRole('dialog', { name: 'Modifier le support' }));
    expect(dialog.getByLabelText('ISIN (facultatif)')).toHaveValue('LU0000000001');
    expect(dialog.queryByRole('switch', { name: 'Cours automatique' })).not.toBeInTheDocument();
  });

  it('lines its filters up in one row, the search first, each label before its control', async () => {
    const closed = holding({ id: 'closed', name: 'Ancien fonds', closedOn: '2024-06-30', line: null, figures: null });
    serve(view({ holdings: [holding(), euroFund, closed] }));
    await renderApp(`/investments/${PEA}?tab=holdings`);

    await screen.findByRole('table', { name: 'Supports de l’enveloppe' });
    const row = screen.getByRole('group', { name: 'Filtres' });
    const search = within(row).getByRole('searchbox', { name: 'Rechercher un support' });
    const kind = within(row).getByRole('combobox', { name: 'Type' });
    expectLabelBefore(within(row).getByText('Rechercher un support'), search);
    expectLabelBefore(within(row).getByText('Type'), kind);
    expect(search.compareDocumentPosition(kind) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(within(row).getByRole('switch', { name: 'Afficher les supports clôturés' })).toBeInTheDocument();
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
    expect(await dialog.findByText('Choisissez des supports différents.')).toBeInTheDocument();
    await pick(user, 'Vers', 'Fonds euros');
    await user.type(dialog.getByLabelText('Parts cédées'), '2,5');
    await user.click(dialog.getByRole('button', { name: 'Enregistrer l’arbitrage' }));
    expect(await screen.findByText('Arbitrage enregistré.')).toBeInTheDocument();
    // One holding on each side: the one amount is both legs'.
    expect(calls[0]!.body).toMatchObject({
      maturity: false, from: [{ holdingId: FUND, units: '-2.5', amount: { amount: '500' } }], to: [{ holdingId: EURO, units: null, amount: { amount: '500' } }],
    });

    await user.click(screen.getByRole('button', { name: 'Actions pour Fonds euros' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Clôturer' }));
    dialog = within(await screen.findByRole('dialog', { name: 'Clôturer Fonds euros' }));
    await user.click(dialog.getByRole('button', { name: 'Clôturer le support' }));
    expect(await screen.findByText('Support clôturé.')).toBeInTheDocument();
    await waitFor(() => expect(calls[1]).toMatchObject({ method: 'PUT', body: { name: 'Fonds euros', kind: 'euro_fund', version: 1 } }));
    expect((calls[1]!.body as { closedOn: string }).closedOn).toMatch(/^\d{4}-\d{2}-\d{2}$/);
  });
});

describe('a switch of several legs', () => {
  it('records one holding into two, each leg with its amount as printed, and refuses a holding twice', async () => {
    const calls = serve(view({ holdings: [holding(), euroFund, holding({ id: 'fund-b', name: 'Fonds B', isin: null })] }));
    const { user } = await renderApp(`/investments/${PEA}?tab=holdings`);

    await user.click(await screen.findByRole('button', { name: 'Arbitrer' }));
    const dialog = within(await screen.findByRole('dialog', { name: 'Arbitrage' }));
    await pick(user, 'Vers', 'Fonds euros');
    await user.click(dialog.getByRole('button', { name: 'Ajouter un support d’arrivée' }));
    await pick(user, 'Vers 2', 'Monde ESG');
    await user.type(dialog.getByLabelText('Montant cédé'), '500');
    await user.type(dialog.getByLabelText('Montant reçu 1'), '300');
    await user.type(dialog.getByLabelText('Montant reçu 2'), '200');
    await user.click(dialog.getByRole('button', { name: 'Enregistrer l’arbitrage' }));
    // Monde ESG is already the leaving holding: refused before anything is sent.
    expect(await dialog.findByText('Choisissez des supports différents.')).toBeInTheDocument();
    expect(calls).toHaveLength(0);

    await pick(user, 'Vers 2', 'Fonds B');
    await user.click(dialog.getByRole('button', { name: 'Enregistrer l’arbitrage' }));
    expect(await screen.findByText('Arbitrage enregistré.')).toBeInTheDocument();
    expect(calls[0]!.body).toMatchObject({
      from: [{ holdingId: FUND, amount: { amount: '500' } }],
      to: [{ holdingId: EURO, amount: { amount: '300' } }, { holdingId: 'fund-b', amount: { amount: '200' } }],
    });
  });
});

describe('a holding’s page', () => {
  beforeAll(async () => { await import('./HoldingPage'); }, 30_000);

  it('loading and refused states show no inline back link: the bar leads back to the envelope once read', async () => {
    server.use(http.get('*/api/v1/investments/envelopes/:id', async () => { await delay('infinite'); return HttpResponse.json({}); }));
    await renderApp(`/investments/${PEA}/holdings/${FUND}`);
    expect(await screen.findByLabelText('Chargement des supports')).toBeInTheDocument();
    await waitFor(() => expectTrailBack('Placements', '/investments'));
    cleanup();

    // The envelope is read, the holding is not in it.
    serve(view({ holdings: [] }));
    await renderApp(`/investments/${PEA}/holdings/${FUND}`);
    expect(await screen.findByText('Cet élément est introuvable.')).toBeInTheDocument();
    await waitFor(() => expectTrailBack('PEA', `/investments/${PEA}`));
    expectTrailBack('Placements', '/investments');
  });

  const report = (over: Partial<CostReport> = {}): CostReport => ({
    envelopeId: PEA, holdingId: FUND, year: 2024, grossReturnPct: 8, totalFeesPct: 2.5, netReturnPct: 5.5, contractFeesPct: null, supportFeesPct: null,
    retrocessionPct: 0.85, netAnnualized5yPct: null, sourceLabel: null, estimatedImplicitCost: eur('168.98'), warnings: [], ...over,
  });
  const movements: Movement[] = [
    { id: 'm1', occurredOn: '2024-01-10', kind: 'allocation_in', units: '10.000000', unitPrice: '200.000000', amount: eur('2000.0000') },
    { id: 'm2', occurredOn: '2024-12-31', kind: 'fee', units: '-0.800000', unitPrice: '75.000000', amount: eur('60.0000') },
    {
      id: 'm3', occurredOn: '2025-01-10', kind: 'switch_out', units: '-2.000000', unitPrice: '250.000000', amount: eur('500.0000'), switchRef: 'sw1',
      switchLegs: [
        { holdingId: FUND, holdingName: 'Monde ESG', kind: 'switch_out', units: '-2.000000', unitPrice: '250.000000', amount: eur('500.0000') },
        { holdingId: EURO, holdingName: 'Fonds euros', kind: 'switch_in', amount: eur('300.0000') },
        { holdingId: 'fund-b', holdingName: 'Fonds B', kind: 'switch_in', amount: eur('200.0000') },
      ],
    },
  ];

  const servePage = (holdings: HoldingsView) => {
    serve(holdings);
    server.use(
      http.get('*/api/v1/investments/envelopes/:id/holdings/:holdingId/movements', () => HttpResponse.json({ envelopeId: PEA, holdingId: FUND, items: [] })),
      http.get('*/api/v1/investments/envelopes/:id/holdings/:holdingId/series', () => HttpResponse.json({ envelopeId: PEA, holdingId: FUND, points: [] })),
      http.get('*/api/v1/investments/envelopes/:id/holdings/:holdingId/cost-reports', () => HttpResponse.json({ envelopeId: PEA, holdingId: FUND, items: [] })),
    );
  };

  it('opens a holding\u2019s price in the explorer, and a euro fund has none', async () => {
    servePage(view());
    const first = await renderApp(`/investments/${PEA}/holdings/${FUND}`);
    const { user, router } = first;
    await user.click(await screen.findByRole('link', { name: 'Voir la série' }));
    await waitFor(() => expect(router.state.location.pathname).toBe('/series'));
    expect(router.state.location.search).toMatchObject({ s: `holding:${PEA}:${FUND}:price` });
    first.unmount();

    servePage(view());
    await renderApp(`/investments/${PEA}/holdings/${EURO}`);
    await screen.findByRole('region', { name: 'Chiffres du support' });
    expect(screen.queryByRole('link', { name: 'Voir la série' })).not.toBeInTheDocument();
  });

  it('shows a refused price with its date and badge, and « Cours automatique » only with the prices on', async () => {
    servePage(view({ holdings: [holding({ price: { value: '210.500000', currency: 'EUR', onDate: '2025-10-01', source: 'auto', status: 'rejected', previous: '200.000000' } })] }));
    const first = await renderApp(`/investments/${PEA}/holdings/${FUND}`);
    const figures = await screen.findByRole('region', { name: 'Chiffres du support' });
    expect(figures).toHaveTextContent('Cours210,5 EUR1 octobre 2025Cours refusé');
    expect(figures).toHaveTextContent('Cours automatiqueactivé');
    first.unmount();

    servePage(view({ estimate: { value: null, asOf: null, basedOn: null, converted: false, stale: [], missingReason: 'disabled' } }));
    await renderApp(`/investments/${PEA}/holdings/${FUND}`);
    expect(await screen.findByRole('region', { name: 'Chiffres du support' })).not.toHaveTextContent('Cours');
  });

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

    await user.click(screen.getByRole('tab', { name: 'Mouvements (3)' }));
    const rows = within(await screen.findByRole('table', { name: 'Mouvements du support' })).getAllByRole('row').slice(1);
    // Issue #45: a switch's leg shows the whole operation.
    expect(rows[0]).toHaveTextContent('Arbitrage sortantMonde ESG → Fonds euros, Fonds B');
    expect(rows[1]).toHaveTextContent('Frais');
    expect(rows[1]).toHaveTextContent('-0,8');
    expect(rows[2]).toHaveTextContent('Part d’un versement');

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

describe('a holding’s breakdown', () => {
  beforeAll(async () => { await import('./HoldingPage'); }, 30_000);

  const BREAKDOWN = '*/api/v1/investments/envelopes/:id/holdings/:holdingId/breakdown';
  const known = (over: Partial<HoldingBreakdown> = {}): HoldingBreakdown => ({
    asOf: '2025-03-31', regions: [{ code: 'north_america', pct: 70 }, { code: 'europe_ex_france', pct: 30 }],
    sectors: [{ code: 'information_technology', pct: 100 }], stale: false, documentId: null, ...over,
  });
  const open = (holdings: HoldingsView) => {
    serve(holdings);
    server.use(
      http.get('*/api/v1/investments/envelopes/:id/holdings/:holdingId/movements', () => HttpResponse.json({ envelopeId: PEA, holdingId: FUND, items: [] })),
      http.get('*/api/v1/investments/envelopes/:id/holdings/:holdingId/series', () => HttpResponse.json({ envelopeId: PEA, holdingId: FUND, points: [] })),
    );
    return { puts: [] as unknown[] };
  };
  const putInto = (puts: unknown[], status = 200, body: HoldingBreakdown = known()) =>
    server.use(http.put(BREAKDOWN, async ({ request }) => { puts.push(await request.json()); return HttpResponse.json(body, { status }); }));

  it('shows the holding’s annual return after inflation with its month (#419)', async () => {
    serve(view({ holdings: [holding({ figures: { ...holding().figures!, annualReturnPct: 5.2 }, realReturn: { percent: 3.1, inflationTo: '2025-09', missing: null } }), euroFund] }));
    server.use(
      http.get('*/api/v1/investments/envelopes/:id/holdings/:holdingId/movements', () => HttpResponse.json({ envelopeId: PEA, holdingId: FUND, items: [] })),
      http.get('*/api/v1/investments/envelopes/:id/holdings/:holdingId/series', () => HttpResponse.json({ envelopeId: PEA, holdingId: FUND, points: [] })),
      http.get('*/api/v1/investments/envelopes/:id/holdings/:holdingId/cost-reports', () => HttpResponse.json({ envelopeId: PEA, holdingId: FUND, items: [] })),
    );
    await renderApp(`/investments/${PEA}/holdings/${FUND}`);
    const real = within(await screen.findByRole('region', { name: 'Chiffres du support' })).getByText('Rendement annuel réel');
    expect(real.nextElementSibling).toHaveTextContent(/\+3,1\s%\spar an/);
    expect(real.nextElementSibling!.nextElementSibling).toHaveTextContent('inflation comptée jusqu’à fin septembre 2025');
  });

  it('shows the holding’s breakdown and its date, flagged when old', async () => {
    open(view());
    server.use(http.get(BREAKDOWN, () => HttpResponse.json(known({ stale: true }))));
    const { container } = await renderApp(`/investments/${PEA}/holdings/${FUND}`);

    const block = await screen.findByRole('region', { name: 'Répartition' });
    expect(await within(block).findByText('Amérique du Nord')).toBeInTheDocument();
    expect(block).toHaveTextContent('70,0 %');
    expect(block).toHaveTextContent('Technologies de l’information');
    expect(block).toHaveTextContent('Fiche du 31 mars 2025');
    expect(block).toHaveTextContent('plus d’un an');
    await expectNoAxeViolations(container);
  });

  it('links the document a breakdown comes from', async () => {
    open(view());
    server.use(http.get(BREAKDOWN, () => HttpResponse.json(known({ documentId: '0194ee77-0000-7000-8000-00000000d0c1' }))));
    await renderApp(`/investments/${PEA}/holdings/${FUND}`);

    const block = await screen.findByRole('region', { name: 'Répartition' });
    expect(await within(block).findByRole('link', { name: /source/i })).toHaveAttribute('href', '/documents/0194ee77-0000-7000-8000-00000000d0c1');
  });

  it('records a breakdown, one line per region and per sector, with the numbers typed', async () => {
    const { puts } = open(view());
    putInto(puts);
    const { user } = await renderApp(`/investments/${PEA}/holdings/${FUND}`);

    await user.click(await screen.findByRole('button', { name: 'Renseigner' }));
    const dialog = within(await screen.findByRole('dialog', { name: 'Répartition de Monde ESG' }));
    expect(within(dialog.getByRole('group', { name: 'Régions' })).getAllByRole('textbox')).toHaveLength(7);
    expect(within(dialog.getByRole('group', { name: 'Secteurs' })).getAllByRole('textbox')).toHaveLength(12);
    await user.type(dialog.getByLabelText('Date de la fiche'), '31/03/2025');
    const regions = within(dialog.getByRole('group', { name: 'Régions' }));
    await user.type(regions.getByRole('textbox', { name: 'Amérique du Nord' }), '70,5');
    await user.type(regions.getByRole('textbox', { name: 'Europe hors France' }), '29,5');
    expect(regions.getByText('Total : 100 %')).toBeInTheDocument();
    await user.click(dialog.getByRole('button', { name: 'Enregistrer' }));

    expect(await screen.findByText('Répartition enregistrée.')).toBeInTheDocument();
    // No sector typed: the key is absent, an empty list would be refused.
    expect(puts).toEqual([{ asOf: '2025-03-31', regions: [{ code: 'north_america', pct: 70.5 }, { code: 'europe_ex_france', pct: 29.5 }] }]);
  });

  it('records an equity in one line: a region and a sector at 100 %', async () => {
    const { puts } = open(view({ holdings: [holding({ kind: 'equity' }), euroFund] }));
    putInto(puts);
    const { user } = await renderApp(`/investments/${PEA}/holdings/${FUND}`);

    await user.click(await screen.findByRole('button', { name: 'Renseigner' }));
    const dialog = within(await screen.findByRole('dialog', { name: 'Répartition de Monde ESG' }));
    await user.type(dialog.getByLabelText('Date de la fiche'), '31/03/2025');
    await pick(user, 'Région', 'France');
    await pick(user, 'Secteur', 'Finance');
    await user.click(dialog.getByRole('button', { name: 'Enregistrer' }));

    expect(await screen.findByText('Répartition enregistrée.')).toBeInTheDocument();
    expect(puts).toEqual([{ asOf: '2025-03-31', regions: [{ code: 'france', pct: 100 }], sectors: [{ code: 'financials', pct: 100 }] }]);
  });

  it('shows the server’s 100 % refusal under the regions', async () => {
    open(view());
    server.use(http.put(BREAKDOWN, () => HttpResponse.json(
      { status: 400, code: 'investments.breakdown_not_100', errors: [{ path: 'regions', code: 'investments.breakdown_not_100', message: 'x' }] }, { status: 400 })));
    const { user } = await renderApp(`/investments/${PEA}/holdings/${FUND}`);

    await user.click(await screen.findByRole('button', { name: 'Renseigner' }));
    const dialog = within(await screen.findByRole('dialog', { name: 'Répartition de Monde ESG' }));
    await user.type(dialog.getByLabelText('Date de la fiche'), '31/03/2025');
    await user.type(within(dialog.getByRole('group', { name: 'Régions' })).getByRole('textbox', { name: 'Japon' }), '99,9');
    await user.click(dialog.getByRole('button', { name: 'Enregistrer' }));

    expect(await within(dialog.getByRole('group', { name: 'Régions' })).findByText('Chaque série saisie doit faire exactement 100 %.')).toBeInTheDocument();
    expect(dialog.getByRole('group', { name: 'Secteurs' })).not.toHaveTextContent('exactement 100 %');
  });

  it('offers no breakdown on a euro fund', async () => {
    open(view());
    await renderApp(`/investments/${PEA}/holdings/${EURO}`);

    expect(await screen.findByRole('heading', { name: 'Fonds euros', level: 1 })).toBeInTheDocument();
    expect(screen.queryByRole('region', { name: 'Répartition' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Renseigner' })).not.toBeInTheDocument();
  });

  it('offers no breakdown on a fund without an ISIN', async () => {
    open(view({ holdings: [holding({ isin: null }), euroFund] }));
    await renderApp(`/investments/${PEA}/holdings/${FUND}`);

    expect(await screen.findByRole('heading', { name: 'Monde ESG', level: 1 })).toBeInTheDocument();
    expect(screen.queryByRole('region', { name: 'Répartition' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Renseigner' })).not.toBeInTheDocument();
  });

  it('keeps « Renseigner » disabled while the breakdown is being read', async () => {
    open(view());
    server.use(http.get(BREAKDOWN, () => new Promise(() => {})));
    await renderApp(`/investments/${PEA}/holdings/${FUND}`);

    expect(await screen.findByRole('button', { name: 'Renseigner' })).toBeDisabled();
  });
});
