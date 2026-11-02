import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { PEA, detail as envelopeDetail } from '../investments/testData';
import { PROPERTY, propertyDetail } from '../properties/testData';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { SaleItemView, SaleTax } from './api';

const ALICE = '0192f0aa-0000-7000-8000-0000000000a1';
const BOB = '0192f0aa-0000-7000-8000-0000000000b2';

const item = (over: Partial<SaleItemView> = {}): SaleItemView => ({
  item: PROPERTY, module: 'properties', name: 'Maison', kind: 'secondary_residence', status: 'taxed',
  value: '290000.00', valueOn: '2026-06-01', estimated: false, stale: false, approximate: false, yearsHeld: 6,
  gain: '9000.00', incomeTax: '1710.00', socialTax: '1548.00', surtax: '0.00', net: '286742.00',
  notes: ['sale_costs_not_deducted'], ...over,
});
const view = (i: SaleItemView, over: Partial<SaleTax> = {}): SaleTax =>
  ({ currency: 'EUR', rulesYear: 2026, carriedForward: false, total: false, items: [i], totals: null, notes: [], ...over });
const serve = (v: SaleTax | null) => server.use(
  http.get('*/api/v1/tax/sale', () => (v ? HttpResponse.json(v) : HttpResponse.json({ status: 404 }, { status: 404 }))),
  http.get('*/api/v1/core/persons', () => HttpResponse.json([
    { id: ALICE, kind: 'adult', displayName: 'Alice', email: null }, { id: BOB, kind: 'adult', displayName: 'Bob', email: null }])),
);
const section = () => screen.findByRole('region', { name: 'Impôt si vente aujourd’hui' });

describe('SaleTaxSection', () => {
  beforeAll(async () => { await import('../properties/PropertyDetailPage'); await import('../investments/EnvelopeDetailPage'); }, 30_000);

  it('shows a secondary residence owned 50/50: figures, methods, allowances, one row per owner', async () => {
    server.use(http.get('*/api/v1/realestate/properties/:id', () => HttpResponse.json(propertyDetail())));
    serve(view(item({
      property: {
        costPrice: '281000.00', feesMethod: 'flat', worksMethod: 'actual', incomeAllowancePct: '36', socialAllowancePct: '9.75',
        owners: [
          { person: ALICE, gain: '4500.00', incomeTax: '855.00', socialTax: '774.00', surtax: '0.00' },
          { person: BOB, gain: '4500.00', incomeTax: '855.00', socialTax: '774.00', surtax: '10.00' },
        ],
      },
    })));
    const { container } = await renderApp(`/properties/${PROPERTY}`);

    const s = within(await section());
    expect(s.getByText('estimation')).toBeInTheDocument();
    expect(s.getByText('Imposable')).toBeInTheDocument();
    expect(s.getByText('Plus-value', { selector: 'dt' }).nextElementSibling).toHaveTextContent('9 000,00 €');
    expect(s.getByText('Impôt', { selector: 'dt' }).nextElementSibling).toHaveTextContent('1 710,00 €');
    expect(s.getByText('Prélèvements sociaux', { selector: 'dt' }).nextElementSibling).toHaveTextContent('1 548,00 €');
    expect(s.queryByText('Surtaxe', { selector: 'dt' })).not.toBeInTheDocument();
    expect(s.getByText('Net', { selector: 'dt' }).nextElementSibling).toHaveTextContent('286 742,00 €');
    expect(s.getByText('Prix de revient fiscal', { selector: 'dt' }).nextElementSibling).toHaveTextContent('281 000,00 €');
    expect(s.getByText('forfait 7,5 %')).toBeInTheDocument();
    expect(s.getByText('travaux réels')).toBeInTheDocument();
    expect(s.getByText('Années de détention', { selector: 'dt' }).nextElementSibling).toHaveTextContent('6');
    expect(s.getByText('Abattements', { selector: 'dt' }).nextElementSibling).toHaveTextContent('impôt 36 %, prélèvements 9,75 %');
    const rows = within(s.getByRole('table', { name: 'Plus-value par propriétaire' })).getAllByRole('row').slice(1);
    expect(rows).toHaveLength(2);
    expect(await within(rows[1]!).findByText('Bob')).toBeInTheDocument();
    expect(rows[1]).toHaveTextContent('10,00 €');
    expect(s.getByText('Les frais de vente (agence, diagnostics) ne sont pas déduits.')).toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('shows a life insurance with its parts and the allowance used', async () => {
    server.use(http.get('*/api/v1/investments/envelopes/:id', () => HttpResponse.json(envelopeDetail({ kind: 'life_insurance' }))));
    serve(view(item({
      item: PEA, module: 'investments', kind: 'life_insurance', name: 'AV', yearsHeld: 10, notes: ['life_insurance_visible_contracts_only'],
      envelope: { remainingCapital: '20000.00', gainBefore2017: '100.00', gainReduced: '2000.00', gainFull: '300.00', allowanceUsed: '4600.00' },
    })));
    const { container } = await renderApp(`/investments/${PEA}`);

    const s = within(await section());
    expect(s.getByText('Capital restant', { selector: 'dt' }).nextElementSibling).toHaveTextContent('20 000,00 €');
    expect(s.getByText('Part avant le 27/09/2017', { selector: 'dt' }).nextElementSibling).toHaveTextContent('100,00 €');
    expect(s.getByText('Part à taux réduit', { selector: 'dt' }).nextElementSibling).toHaveTextContent('2 000,00 €');
    expect(s.getByText('Part à taux plein', { selector: 'dt' }).nextElementSibling).toHaveTextContent('300,00 €');
    expect(s.getByText('Abattement utilisé', { selector: 'dt' }).nextElementSibling).toHaveTextContent('4 600,00 €');
    expect(s.getByText(/Seuls les contrats que vous voyez/)).toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('A_finding_shows_no_figure: the finding and a link to the settings tab', async () => {
    server.use(http.get('*/api/v1/investments/envelopes/:id', () => HttpResponse.json(envelopeDetail())));
    serve(view(item({
      item: PEA, module: 'investments', kind: 'pea', status: 'not_estimated', finding: 'opening_date_unknown',
      value: null, valueOn: null, gain: null, incomeTax: null, socialTax: null, surtax: null, net: null, yearsHeld: null, notes: [],
    })));
    await renderApp(`/investments/${PEA}`);

    const s = within(await section());
    expect(s.getByText('Date d’ouverture inconnue : renseignez-la dans les réglages')).toBeInTheDocument();
    expect(s.getByRole('link', { name: 'Ouvrir les réglages' })).toHaveAttribute('href', `/investments/${PEA}?tab=settings`);
    expect(s.queryByText('Plus-value')).not.toBeInTheDocument();
    expect(s.queryByText('Net')).not.toBeInTheDocument();
  });

  it('offers no settings link to a reader, the finding stays', async () => {
    server.use(http.get('*/api/v1/investments/envelopes/:id', () => HttpResponse.json(envelopeDetail({ canWrite: false }))));
    serve(view(item({
      item: PEA, module: 'investments', kind: 'pea', status: 'not_estimated', finding: 'opening_date_unknown',
      value: null, valueOn: null, gain: null, incomeTax: null, socialTax: null, surtax: null, net: null, yearsHeld: null, notes: [],
    })));
    await renderApp(`/investments/${PEA}`);

    const s = within(await section());
    expect(s.getByText('Date d’ouverture inconnue : renseignez-la dans les réglages')).toBeInTheDocument();
    expect(s.queryByRole('link', { name: 'Ouvrir les réglages' })).not.toBeInTheDocument();
  });

  it('hides the life insurance parts and the allowance when they are zero', async () => {
    server.use(http.get('*/api/v1/investments/envelopes/:id', () => HttpResponse.json(envelopeDetail({ kind: 'life_insurance' }))));
    serve(view(item({
      item: PEA, module: 'investments', kind: 'life_insurance', name: 'AV',
      envelope: { remainingCapital: '20000.00', gainBefore2017: '0.00', gainReduced: '0.00', gainFull: '300.00', allowanceUsed: '0.00' },
    })));
    await renderApp(`/investments/${PEA}`);

    const s = within(await section());
    expect(s.getByText('Part à taux plein', { selector: 'dt' })).toBeInTheDocument();
    expect(s.queryByText('Part avant le 27/09/2017', { selector: 'dt' })).not.toBeInTheDocument();
    expect(s.queryByText('Part à taux réduit', { selector: 'dt' })).not.toBeInTheDocument();
    expect(s.queryByText('Abattement utilisé', { selector: 'dt' })).not.toBeInTheDocument();
  });

  it('shows a server error inside the titled section', async () => {
    server.use(
      http.get('*/api/v1/realestate/properties/:id', () => HttpResponse.json(propertyDetail())),
      http.get('*/api/v1/tax/sale', () => HttpResponse.json({ status: 500, title: 'Boom' }, { status: 500, headers: { 'Content-Type': 'application/problem+json' } })),
    );
    await renderApp(`/properties/${PROPERTY}`);

    expect(await within(await section()).findByRole('alert')).toBeInTheDocument();
  });

  it('shows « approximatif » and « valeur estimée » next to the figures of an estimated envelope', async () => {
    server.use(http.get('*/api/v1/investments/envelopes/:id', () => HttpResponse.json(envelopeDetail())));
    serve(view(item({
      item: PEA, module: 'investments', kind: 'pea', approximate: true, estimated: true, stale: true,
      envelope: { remainingCapital: '20000.00' }, notes: ['approximate', 'value_estimated'],
    })));
    await renderApp(`/investments/${PEA}`);

    const s = within(await section());
    expect(s.getByText('estimation · approximatif · valeur estimée · valeur à mettre à jour')).toBeInTheDocument();
  });

  it('A_page_that_is_not_found_hides_the_section', async () => {
    server.use(http.get('*/api/v1/realestate/properties/:id', () => HttpResponse.json(propertyDetail())));
    serve(null);
    await renderApp(`/properties/${PROPERTY}`);

    await screen.findByRole('heading', { name: 'Maison de Nantes', level: 1 });
    await screen.findByRole('region', { name: 'Coût réel' });
    // The sale query has settled: its skeleton is gone.
    await waitFor(() => expect(screen.queryByRole('status', { name: 'Calcul de l’impôt' })).not.toBeInTheDocument());
    expect(screen.queryByRole('region', { name: 'Impôt si vente aujourd’hui' })).not.toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('shows the code of a note it does not know', async () => {
    server.use(http.get('*/api/v1/realestate/properties/:id', () => HttpResponse.json(propertyDetail())));
    serve(view(item({ notes: ['brand_new_note'] })));
    await renderApp(`/properties/${PROPERTY}`);

    expect(within(await section()).getByText('brand_new_note')).toBeInTheDocument();
  });
});
