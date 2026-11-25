import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import type { AssetView } from '@/api/assets';
import { pick } from '@/test/controls';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { ASSET, assetView } from './testData';

function serve(initial: AssetView, posted: unknown[] = [], puts: unknown[] = []) {
  let current = initial;
  server.use(
    http.put('*/api/v1/assets/:id', async ({ request }) => { puts.push(await request.json()); return HttpResponse.json({ id: ASSET }); }),
    http.get('*/api/v1/assets/:id', () => HttpResponse.json(current)),
    http.post('*/api/v1/assets/:id/valuations', async ({ request }) => {
      const body = (await request.json()) as { asOf: string; amount: string };
      posted.push(body);
      current = { ...current, valueFromPurchase: false, value: { amount: `${body.amount}.00`, currency: 'EUR' },
        valuations: [...current.valuations, { id: 'v2', asOf: body.asOf, amount: { amount: `${body.amount}.00`, currency: 'EUR' }, note: null, fromBank: false }] };
      return HttpResponse.json({ id: 'v2' }, { status: 201 });
    }),
  );
}

describe('AssetDetailPage', () => {
  beforeAll(async () => { await import('./AssetDetailPage'); }, 30_000);

  it('marks a value that is the purchase price', async () => {
    serve(assetView({ valueFromPurchase: true, valuations: [] }));
    await renderApp(`/other-assets/${ASSET}`);

    expect(await screen.findByText('prix d’achat', { selector: '[data-slot="badge"]' })).toBeInTheDocument();
  });

  it('shows no badge once a valuation gives the value', async () => {
    serve(assetView());
    await renderApp(`/other-assets/${ASSET}`);

    await screen.findByRole('heading', { name: 'Citadine', level: 1 });
    expect(screen.queryByText('prix d’achat', { selector: '[data-slot="badge"]' })).not.toBeInTheDocument();
  });

  it('adds a valuation with a POST and shows it', async () => {
    const posted: unknown[] = [];
    serve(assetView({ valueFromPurchase: true, valuations: [] }), posted);
    const { user } = await renderApp(`/other-assets/${ASSET}?tab=valuations`);

    await user.click(await screen.findByRole('button', { name: 'Ajouter une valeur' }));
    const dialog = within(await screen.findByRole('dialog', { name: 'Nouvelle valeur' }));
    await user.type(dialog.getByLabelText('Montant'), '9000');
    await user.clear(dialog.getByLabelText('Date'));
    await user.type(dialog.getByLabelText('Date'), '01/06/2026');
    await user.click(dialog.getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(posted).toEqual([expect.objectContaining({ asOf: '2026-06-01', amount: '9000' })]));
    const table = await screen.findByRole('table', { name: 'Valeurs du bien' });
    expect(await within(table).findByText('9 000,00 €')).toBeInTheDocument();
  });

  it('says in French why the last value of an asset without a purchase price cannot be deleted', async () => {
    serve(assetView());
    server.use(http.delete('*/api/v1/assets/:id/valuations/:valuationId', () => HttpResponse.json(
      { status: 422, code: 'assets.value_required', detail: 'An asset has a purchase price or at least one valuation.' }, { status: 422 })));
    const { user } = await renderApp(`/other-assets/${ASSET}?tab=valuations`);

    await user.click(await screen.findByRole('button', { name: /^Actions pour la valeur du/ }));
    await user.click(await screen.findByRole('menuitem', { name: 'Supprimer' }));
    await user.click(await screen.findByRole('button', { name: 'Confirmer' }));

    expect(await screen.findByText('Indiquez ce que vaut ce bien : son prix d’achat ou sa valeur actuelle.')).toBeInTheDocument();
    expect(screen.queryByText(/An asset has/)).not.toBeInTheDocument();
  });

  it('shows the latest mileage reading of a vehicle with its date', async () => {
    serve(assetView({ registeredOn: '2022-03-01', mileageKm: 45000, mileageOn: '2025-06-01', fiscalHp: 5, energy: 'diesel' }));
    await renderApp(`/other-assets/${ASSET}`);

    const figures = within(await screen.findByRole('region', { name: 'Valeur du bien' }));
    expect(figures.getByText(/45\s000 km/)).toBeInTheDocument();
    expect(figures.getByText('Diesel')).toBeInTheDocument();
    expect(figures.getByText(/5 CV/)).toBeInTheDocument();
  });

  it('updates the mileage with a PUT of the whole asset', async () => {
    const puts: unknown[] = [];
    serve(assetView({ registeredOn: '2022-03-01', mileageKm: 45000, mileageOn: '2025-06-01', fiscalHp: 5, energy: 'diesel', note: null }), [], puts);
    const { user } = await renderApp(`/other-assets/${ASSET}`);

    await user.click(await screen.findByRole('button', { name: 'Autres actions' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Mettre à jour le kilométrage' }));
    const dialog = within(await screen.findByRole('dialog', { name: 'Mettre à jour le kilométrage' }));
    await user.clear(dialog.getByLabelText('Kilométrage (km)'));
    await user.type(dialog.getByLabelText('Kilométrage (km)'), '51000');
    await user.clear(dialog.getByLabelText('Date du relevé'));
    await user.type(dialog.getByLabelText('Date du relevé'), '01/06/2026');
    await user.click(dialog.getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(puts).toEqual([expect.objectContaining({
      name: 'Citadine', kind: 'vehicle', version: 1, purchasePrice: '12000', purchasedOn: '2022-03-01',
      registeredOn: '2022-03-01', mileageKm: 51000, mileageOn: '2026-06-01', fiscalHp: 5, energy: 'diesel',
    })]));
  });

  it('shows an estimate with its base and the rate', async () => {
    serve(assetView({ valueEstimated: true, valueBase: { amount: '8000.00', currency: 'EUR' }, valueOn: '2026-06-01', depreciationRate: 12.5 }));
    await renderApp(`/other-assets/${ASSET}`);

    expect(await screen.findByText('estimée', { selector: '[data-slot="badge"]' })).toBeInTheDocument();
    expect(screen.getByText(/estimée à partir de la valeur du/)).toBeInTheDocument();
    expect(screen.getByText('12,5 % par an')).toBeInTheDocument();
  });

  it('sends the rate with the edit, folded under Personnaliser', async () => {
    const puts: unknown[] = [];
    serve(assetView({ note: null, depreciationRate: 12.5 }), [], puts);
    const { user } = await renderApp(`/other-assets/${ASSET}`);

    await user.click(await screen.findByRole('button', { name: 'Modifier' }));
    const dialog = within(await screen.findByRole('dialog', { name: 'Modifier le bien' }));
    expect(dialog.queryByLabelText('Perte de valeur par an (%)')).not.toBeInTheDocument();
    await user.click(dialog.getByRole('button', { name: 'Personnaliser' }));
    expect(dialog.getByLabelText('Perte de valeur par an (%)')).toHaveValue('12,5');
    await user.clear(dialog.getByLabelText('Perte de valeur par an (%)'));
    await user.type(dialog.getByLabelText('Perte de valeur par an (%)'), '15');
    await user.click(dialog.getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(puts).toEqual([expect.objectContaining({ kind: 'vehicle', depreciationRate: 15 })]));
  });

  it('follows the kind picked in the form for the rate fold', async () => {
    serve(assetView({ kind: 'art', note: null }));
    const { user } = await renderApp(`/other-assets/${ASSET}`);

    await user.click(await screen.findByRole('button', { name: 'Modifier' }));
    const dialog = within(await screen.findByRole('dialog', { name: 'Modifier le bien' }));
    expect(dialog.queryByRole('button', { name: 'Personnaliser' })).not.toBeInTheDocument();
    await pick(user, 'Type', 'Véhicule');
    expect(dialog.getByRole('button', { name: 'Personnaliser' })).toBeInTheDocument();
    await pick(user, 'Type', 'Œuvre d’art');
    expect(dialog.queryByRole('button', { name: 'Personnaliser' })).not.toBeInTheDocument();
  });

  it('refuses a rate of 100 % before sending', async () => {
    const puts: unknown[] = [];
    serve(assetView({ note: null }), [], puts);
    const { user } = await renderApp(`/other-assets/${ASSET}`);

    await user.click(await screen.findByRole('button', { name: 'Modifier' }));
    const dialog = within(await screen.findByRole('dialog', { name: 'Modifier le bien' }));
    await user.click(dialog.getByRole('button', { name: 'Personnaliser' }));
    await user.type(dialog.getByLabelText('Perte de valeur par an (%)'), '100');
    await user.click(dialog.getByRole('button', { name: 'Enregistrer' }));

    expect(await dialog.findByText(/pourcentage entre 0 et 100 exclus/)).toBeInTheDocument();
    expect(puts).toEqual([]);
  });

  it('offers no mileage update for another kind', async () => {
    serve(assetView({ kind: 'art' }));
    const { user } = await renderApp(`/other-assets/${ASSET}`);

    await user.click(await screen.findByRole('button', { name: 'Autres actions' }));
    await screen.findByRole('menuitem', { name: 'Archiver' });
    expect(screen.queryByRole('menuitem', { name: 'Mettre à jour le kilométrage' })).not.toBeInTheDocument();
  });
  const LINKED = { id: '0192f0bb-0000-7000-8000-0000000000a1', name: 'Assurance auto', price: { amount: '40.00', currency: 'EUR' }, every: { count: 3, unit: 'month' }, nextDeadline: '2026-12-01' };

  it('lists the linked subscriptions and unlinks one with a PUT', async () => {
    const unlinked: unknown[] = [];
    serve(assetView({ subscriptions: [LINKED] }));
    server.use(http.put('*/api/v1/subscriptions/:id/asset', async ({ request }) => { unlinked.push(await request.json()); return HttpResponse.json({ id: LINKED.id }); }));
    const { user } = await renderApp(`/other-assets/${ASSET}?tab=subscriptions`);

    const table = within(await screen.findByRole('table', { name: 'Abonnements liés à ce bien' }));
    expect(table.getByRole('link', { name: 'Assurance auto' })).toHaveAttribute('href', `/subscriptions/${LINKED.id}`);
    expect(table.getByRole('row', { name: /Assurance auto/ })).toHaveTextContent('tous les 3 mois');
    await user.click(table.getByRole('button', { name: 'Actions de l’abonnement' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Délier' }));

    await waitFor(() => expect(unlinked).toEqual([{ assetId: null }]));
  });

  it('links a subscription picked in the list with a PUT', async () => {
    const linked: unknown[] = [];
    const asked: string[] = [];
    serve(assetView());
    server.use(
      http.get('*/api/v1/subscriptions', ({ request }) => {
        asked.push(new URL(request.url).search);
        return HttpResponse.json({ items: [{ id: LINKED.id, displayName: 'Assurance auto' }], groups: [], findings: [] });
      }),
      http.put('*/api/v1/subscriptions/:id/asset', async ({ request }) => { linked.push(await request.json()); return HttpResponse.json({ id: LINKED.id }); }),
    );
    const { user } = await renderApp(`/other-assets/${ASSET}?tab=subscriptions`);

    await user.click(await screen.findByRole('button', { name: 'Lier un abonnement' }));
    const dialog = within(await screen.findByRole('dialog', { name: 'Lier un abonnement' }));
    await user.click(await dialog.findByRole('combobox'));
    await user.click(await screen.findByRole('option', { name: 'Assurance auto' }));
    await user.click(dialog.getByRole('button', { name: 'Lier' }));

    await waitFor(() => expect(linked).toEqual([{ assetId: ASSET }]));
    expect(asked[0]).toContain('asset=none'); // only the subscriptions without an asset, filtered by the server
  });
});
