import { screen, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { ASSET, assetView } from '../testData';

const noCosts = http.get('*/api/v1/assets/:id/costs', () => HttpResponse.json({ year: 2025, total: { amount: '0.00', currency: 'EUR' }, years: [2025], rows: [] }));
const readings = [{ on: '2025-03-01', km: 12000 }, { on: '2025-09-01', km: 150000 }];

describe('A vehicle’s mileage readings', () => {
  beforeAll(async () => { await import('../AssetDetailPage'); }, 30_000);

  it('lists the readings newest first and removes a wrong one after asking', async () => {
    const removed: string[] = [];
    server.use(
      noCosts,
      http.get('*/api/v1/assets/:id', () => HttpResponse.json(assetView({ kind: 'vehicle', mileageKm: 150000, mileageOn: '2025-09-01', mileageReadings: readings }))),
      http.delete('*/api/v1/assets/:id/mileage-readings/:on', ({ params }) => { removed.push(String(params.on)); return new HttpResponse(null, { status: 204 }); }),
    );
    const { user } = await renderApp(`/other-assets/${ASSET}?tab=costs`);

    const table = within(await screen.findByRole('table', { name: 'Relevés de kilométrage' }));
    const rows = table.getAllByRole('row').slice(1);
    expect(rows[0]).toHaveTextContent(/1 septembre 2025.*150\s000 km/);
    expect(rows[1]).toHaveTextContent(/1 mars 2025.*12\s000 km/);
    await user.click(table.getByRole('button', { name: /^Actions pour le relevé du 1(er)? septembre 2025/ }));
    await user.click(await screen.findByRole('menuitem', { name: 'Supprimer' }));
    await user.click(await screen.findByRole('button', { name: 'Confirmer' }));

    expect(await screen.findByText('Relevé supprimé.')).toBeInTheDocument();
    expect(removed).toEqual(['2025-09-01']);
  });

  it('offers no removal to a reader who cannot write', async () => {
    server.use(noCosts, http.get('*/api/v1/assets/:id', () => HttpResponse.json(assetView({ kind: 'vehicle', canWrite: false, mileageReadings: readings }))));
    await renderApp(`/other-assets/${ASSET}?tab=costs`);

    const table = within(await screen.findByRole('table', { name: 'Relevés de kilométrage' }));
    expect(table.queryByRole('button', { name: /Actions pour le relevé/ })).not.toBeInTheDocument();
  });
});
