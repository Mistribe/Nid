import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import type { AssetCosts } from '@/api/assets';
import { renderApp } from '@/test/renderApp';
import { pick } from '@/test/controls';
import { server } from '@/test/server';
import { ASSET, assetView } from '../testData';

const DOC = '0192f0bb-0000-7000-8000-0000000000d1';
const costs = (over: Partial<AssetCosts> = {}): AssetCosts => ({
  year: 2026, total: { amount: '320.50', currency: 'EUR' }, years: [2026, 2025],
  rows: [
    { id: 'c2', occurredOn: '2026-09-01', category: 'tyres', amount: { amount: '200.50', currency: 'EUR' }, note: 'Pneus hiver', documentId: DOC, fromBank: false },
    { id: 'c1', occurredOn: '2026-03-01', category: 'service', amount: { amount: '120.00', currency: 'EUR' }, note: null, documentId: null, fromBank: true },
  ],
  ...over,
});

function serve(posted: unknown[] = [], asked: string[] = []) {
  server.use(
    http.get('*/api/v1/assets/:id', () => HttpResponse.json(assetView({ registeredOn: '2022-03-01' }))),
    http.get('*/api/v1/assets/:id/costs', ({ request }) => {
      const year = new URL(request.url).searchParams.get('year');
      asked.push(year ?? '');
      return HttpResponse.json(year === '2025' ? costs({ year: 2025, total: { amount: '50.00', currency: 'EUR' }, rows: [] }) : costs());
    }),
    http.get('*/api/v1/documents', () => HttpResponse.json({ items: [{ id: DOC, title: 'facture-pneus.pdf', links: [] }], total: 1 })),
    http.post('*/api/v1/assets/:id/costs', async ({ request }) => { posted.push(await request.json()); return HttpResponse.json({ id: 'c3' }, { status: 201 }); }),
  );
}

describe('A vehicle’s costs', () => {
  beforeAll(async () => { await import('../AssetDetailPage'); }, 30_000);

  it('lists the year’s costs with the server’s total, the invoice and the bank origin', async () => {
    serve();
    await renderApp(`/other-assets/${ASSET}?tab=costs`);

    const table = within(await screen.findByRole('table', { name: 'Frais du véhicule' }));
    expect(await table.findByRole('row', { name: /Pneus.*200,50\s€/ })).toBeInTheDocument();
    expect(table.getByRole('link', { name: 'Source du chiffre' })).toHaveAttribute('href', `/documents/${DOC}`);
    expect(table.getByText('Depuis la banque')).toBeInTheDocument();
    expect(screen.getByText(/Total 2026/)).toHaveTextContent('320,50 €');
  });

  it('asks the server for another year', async () => {
    const asked: string[] = [];
    serve([], asked);
    const { user } = await renderApp(`/other-assets/${ASSET}?tab=costs`);

    await screen.findByText(/Total 2026/);
    await pick(user, 'Année', '2025');

    expect(await screen.findByText('Aucun frais cette année.')).toBeInTheDocument();
    expect(asked).toContain('2025');
  });

  it('adds a cost with a POST, its invoice picked among the vehicle’s documents', async () => {
    const posted: unknown[] = [];
    serve(posted);
    const { user } = await renderApp(`/other-assets/${ASSET}?tab=costs`);

    await user.click(await screen.findByRole('button', { name: 'Ajouter un frais' }));
    const dialog = within(await screen.findByRole('dialog', { name: 'Nouveau frais' }));
    await user.type(dialog.getByLabelText('Montant'), '85,40');
    await user.clear(dialog.getByLabelText('Date'));
    await user.type(dialog.getByLabelText('Date'), '01/06/2026');
    await pick(user, 'Catégorie', 'Réparation');
    await pick(user, 'Facture', 'facture-pneus.pdf');
    await user.click(dialog.getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(posted).toEqual([{ occurredOn: '2026-06-01', category: 'repair', amount: '85.40', documentId: DOC }]));
  });

  it('has no costs tab on another kind', async () => {
    server.use(http.get('*/api/v1/assets/:id', () => HttpResponse.json(assetView({ kind: 'art' }))));
    await renderApp(`/other-assets/${ASSET}`);

    await screen.findByRole('heading', { name: 'Citadine', level: 1 });
    expect(screen.queryByRole('tab', { name: 'Frais' })).not.toBeInTheDocument();
  });
});
