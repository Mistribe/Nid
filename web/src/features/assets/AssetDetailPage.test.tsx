import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import type { AssetView } from '@/api/assets';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { ASSET, assetView } from './testData';

function serve(initial: AssetView, posted: unknown[] = []) {
  let current = initial;
  server.use(
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
});
