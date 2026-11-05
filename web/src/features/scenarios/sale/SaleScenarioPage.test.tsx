import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { pick } from '@/test/controls';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { SaleScenario } from '../api';
import { FLAT, properties } from '../projection/testData';

const ID = '0192f0aa-0000-7000-8000-0000000000a1';
const eur = (amount: string) => ({ amount, currency: 'EUR' });

const view: SaleScenario = {
  id: ID, name: 'Vendre Lyon', visibility: 'shared', scope: 'household', canWrite: true,
  document: { version: 1, property: FLAT, saleOn: '2027-06-01', price: '320000.00', agencyFees: '10000.00', penalties: '2000.00', horizonYears: 10 },
  result: {
    sale: {
      soldOn: '2027-06-01', price: eur('320000.00'), agencyFees: eur('10000.00'), tax: eur('8123.00'), loansRepaid: eur('150000.00'),
      penalties: eur('2000.00'), netSeller: eur('149877.00'), loanPayments: eur('1159.92'), propertyNet: eur('-650.00'), monthlyChange: eur('509.92'),
    },
    tax: {
      item: FLAT, module: 'properties', name: 'Appartement Lyon', kind: 'rental', status: 'taxed', value: '310000.00', valueOn: '2027-06-01',
      estimated: false, stale: false, approximate: false, yearsHeld: 7, gain: '22000', incomeTax: '3000', socialTax: '5123', surtax: '0', net: '301877',
      notes: ['agency_fees_deducted'],
    },
    points: [
      { on: '2026-11-05', without: eur('100000.00'), with: eur('100000.00') },
      { on: '2026-12-31', without: eur('101000.00'), with: eur('101000.00') },
      { on: '2036-10-31', without: eur('180000.00'), with: eur('195000.00') },
    ],
    withoutAtHorizon: eur('180000.00'), withAtHorizon: eur('195000.00'), gap: eur('15000.00'),
    warnings: [],
  },
};

function serve(first: SaleScenario = view) {
  let current = first;
  const put: { document: SaleScenario['document'] }[] = [];
  const created: { document: SaleScenario['document']; name: string }[] = [];
  server.use(
    http.get('*/api/v1/realestate/properties', () => HttpResponse.json(properties)),
    http.get(`*/api/v1/scenarios/sale/${ID}`, () => HttpResponse.json(current)),
    http.put(`*/api/v1/scenarios/sale/${ID}`, async ({ request }) => {
      const body = await request.json() as { document: SaleScenario['document'] };
      put.push(body);
      current = { ...current, document: body.document, result: { ...current.result, gap: eur('16000.00') } };
      return HttpResponse.json(current);
    }),
    http.post('*/api/v1/scenarios/sale', async ({ request }) => {
      created.push(await request.json() as { document: SaleScenario['document']; name: string });
      return HttpResponse.json({ id: ID }, { status: 201 });
    }),
  );
  return { put, created };
}

const figures = () => within(screen.getByRole('region', { name: 'Chiffres clés' }));

describe('SaleScenarioPage', () => {
  beforeAll(async () => { await import('./SaleScenarioPage'); }, 30_000);

  it('shows the server figures, the way to the net seller and what stops each month', async () => {
    serve();
    await renderApp(`/scenarios/sale/${ID}`);

    expect(await screen.findByRole('heading', { level: 1, name: 'Vendre Lyon' })).toBeInTheDocument();
    expect(figures().getByText('Net vendeur').nextSibling).toHaveTextContent('149 877,00 €');
    expect(figures().getByText('Impôt sur la plus-value').nextSibling).toHaveTextContent('8 123,00 €');
    expect(figures().getByText('Prêts remboursés').nextSibling).toHaveTextContent('150 000,00 €');
    expect(figures().getByText('Dépenses mensuelles en moins').nextSibling).toHaveTextContent('509,92 €');
    expect(figures().getByText('Écart de patrimoine à l’horizon').nextSibling).toHaveTextContent('15 000,00 €');
    expect(screen.getByText('Indemnités de remboursement anticipé').nextSibling).toHaveTextContent('2 000,00 €');
    expect(screen.getByText('Mensualités des prêts, assurance comprise').nextSibling).toHaveTextContent('1 159,92 €');
    expect(screen.getByText('Aucun avertissement.')).toBeInTheDocument();
  });

  it('shows the capital gain and the projection in their tabs', async () => {
    serve();
    const { user } = await renderApp(`/scenarios/sale/${ID}`);

    await user.click(await screen.findByRole('tab', { name: 'Impôt' }));
    expect(await screen.findByText('Imposable')).toBeInTheDocument();
    expect(screen.getByText('Les frais d’agence à la charge du vendeur sont déduits du prix de vente.')).toBeInTheDocument();

    await user.click(screen.getByRole('tab', { name: 'Projection' }));
    const points = await screen.findByRole('table', { name: 'Patrimoine net en fin d’année' });
    expect(within(points).getByRole('columnheader', { name: 'Sans la vente' })).toBeInTheDocument();
    expect(within(points).getByRole('row', { name: /31 octobre 2036/ })).toHaveTextContent(/180 000,00 €.*195 000,00 €/);
  });

  it('says so when the reader cannot see the property', async () => {
    serve({
      ...view, canWrite: false, document: { ...view.document, property: null },
      result: { ...view.result, sale: null, tax: null, warnings: [{ code: 'property_hidden', value: null }] },
    });
    await renderApp(`/scenarios/sale/${ID}`);

    expect(await screen.findByText('Vous ne voyez pas le bien vendu : ses chiffres ne sont pas affichés.')).toBeInTheDocument();
    expect(screen.getByText('Le bien vendu n’est plus visible : la vente est écartée du calcul.')).toBeInTheDocument();
    expect(figures().getByText('Net vendeur').nextSibling).toHaveTextContent('—');
    expect(screen.queryByRole('button', { name: 'Modifier le scénario' })).not.toBeInTheDocument();
  });

  it('edits the price and puts the whole document', async () => {
    const { put } = serve();
    const { user } = await renderApp(`/scenarios/sale/${ID}`);

    await user.click(await screen.findByRole('button', { name: 'Modifier le scénario' }));
    const dialog = await screen.findByRole('dialog', { name: 'Modifier le scénario' });
    await user.clear(within(dialog).getByLabelText('Prix de vente'));
    await user.type(within(dialog).getByLabelText('Prix de vente'), '330000');
    await user.click(within(dialog).getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(put).toHaveLength(1));
    expect(put[0]).toMatchObject({ name: 'Vendre Lyon', visibility: 'shared', scope: 'household' });
    expect(put[0]!.document).toEqual({
      version: 1, property: FLAT, saleOn: '2027-06-01', price: '330000', agencyFees: '10000.00', penalties: '2000.00', horizonYears: 10,
    });
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(figures().getByText('Écart de patrimoine à l’horizon').nextSibling).toHaveTextContent('16 000,00 €');
  });
});

describe('NewSaleScenarioPage', () => {
  beforeAll(async () => { await import('./NewSaleScenarioPage'); }, 30_000);

  it('creates a sale and opens it', async () => {
    const { created } = serve();
    const { user } = await renderApp('/scenarios/new?kind=sale');

    await user.type(await screen.findByLabelText('Nom du scénario'), 'Vendre Lyon');
    await pick(user, 'Bien vendu', 'Appartement Lyon');
    await user.type(screen.getByLabelText('Date de vente'), '01/06/2027');
    await user.type(screen.getByLabelText('Prix de vente'), '320000');
    await user.click(screen.getByRole('button', { name: 'Créer le scénario' }));

    await waitFor(() => expect(created).toHaveLength(1));
    expect(created[0]).toMatchObject({ name: 'Vendre Lyon' });
    expect(created[0]!.document).toEqual({
      version: 1, property: FLAT, saleOn: '2027-06-01', price: '320000', agencyFees: '0', penalties: '0', horizonYears: 10,
    });
    expect(await screen.findByRole('heading', { level: 1, name: 'Vendre Lyon' })).toBeInTheDocument();
  });
});
