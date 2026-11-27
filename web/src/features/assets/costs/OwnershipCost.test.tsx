import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { pick } from '@/test/controls';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { ASSET, assetView, ownershipCost } from '../testData';

function serve(asked: string[] = [], partial = false) {
  server.use(
    http.get('*/api/v1/assets/:id', () => HttpResponse.json(assetView({ kind: 'vehicle' }))),
    http.get('*/api/v1/assets/:id/costs', () => HttpResponse.json({ year: 2026, total: { amount: '0.00', currency: 'EUR' }, years: [2026], rows: [] })),
    http.get('*/api/v1/assets/:id/ownership-cost', ({ request }) => {
      asked.push(new URL(request.url).searchParams.get('year') ?? '');
      return HttpResponse.json(partial ? ownershipCost({
        incomplete: true, perKm: null,
        parts: [
          { code: 'running:fuel', amount: null, sourceId: null, name: null },
          { code: 'subscription', amount: null, sourceId: null, name: null },
          { code: 'depreciation', amount: null, sourceId: null, name: null },
        ],
      }) : ownershipCost());
    }),
  );
}

describe('A vehicle’s cost of ownership', () => {
  beforeAll(async () => { await import('../AssetDetailPage'); }, 30_000);

  it('shows the monthly cost, the cost per km and each part with its source', async () => {
    serve();
    await renderApp(`/other-assets/${ASSET}?tab=costs`);

    const table = within(await screen.findByRole('table', { name: 'Coût de possession par poste' }));
    expect(table.getByRole('row', { name: /Carburant \/ recharge.*1\s200,00/ })).toBeInTheDocument();
    expect(table.getByRole('link', { name: 'Assurance auto' })).toHaveAttribute('href', '/subscriptions/sub-1');
    expect(table.getByRole('link', { name: 'Crédit auto' })).toHaveAttribute('href', '/loans/loan-1');
    expect(table.getByRole('row', { name: /Perte de valeur.*2\s000,00/ })).toBeInTheDocument();
    const figures = within(screen.getByRole('region', { name: 'Coût de possession' }));
    expect(figures.getByText(/386,99/)).toBeInTheDocument();
    expect(figures.getByText(/0,39/)).toBeInTheDocument();
    expect(screen.queryByText(/Total partiel/)).not.toBeInTheDocument();
  });

  it('says a part is not filled in, where to fill it, and that the total is partial', async () => {
    serve([], true);
    const { user } = await renderApp(`/other-assets/${ASSET}?tab=costs`);

    const table = within(await screen.findByRole('table', { name: 'Coût de possession par poste' }));
    expect(table.getAllByText('Non renseigné', { exact: false })).toHaveLength(3);
    expect(screen.getByText('Total partiel : des postes ne sont pas renseignés.')).toBeInTheDocument();
    await user.click(table.getByRole('button', { name: 'lier l’assurance' }));

    expect(await screen.findByRole('tab', { name: /Abonnements/, selected: true })).toBeInTheDocument();
  });

  it('asks the server for the year picked', async () => {
    const asked: string[] = [];
    serve(asked);
    const { user } = await renderApp(`/other-assets/${ASSET}?tab=costs`);
    await screen.findByRole('table', { name: 'Coût de possession par poste' });

    await pick(user, 'Période', '2025');

    await waitFor(() => expect(asked).toContain('2025'));
  });

  it('marks the cost per km partial when the monthly cost is', async () => {
    server.use(
      http.get('*/api/v1/assets/:id', () => HttpResponse.json(assetView({ kind: 'vehicle' }))),
      http.get('*/api/v1/assets/:id/ownership-cost', () => HttpResponse.json(ownershipCost({ incomplete: true }))),
    );
    await renderApp(`/other-assets/${ASSET}`);

    await screen.findByText('Coût au km');
    expect(screen.getAllByText('partiel')).toHaveLength(2);
  });

  it('puts the monthly cost among the vehicle’s figures', async () => {
    serve();
    await renderApp(`/other-assets/${ASSET}`);

    expect(await screen.findByText('Coût mensuel')).toBeInTheDocument();
    expect(screen.getByText('Coût au km')).toBeInTheDocument();
  });
});
