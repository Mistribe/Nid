import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { pick } from '@/test/controls';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { persons } from '../subscriptions/testData';
import { list, row } from './testData';

function serve(seen: URLSearchParams[]) {
  server.use(
    http.get('*/api/v1/core/persons', () => HttpResponse.json(persons)),
    http.get('*/api/v1/assets', ({ request }) => {
      const q = new URL(request.url).searchParams;
      seen.push(q);
      // The server filters: the page only shows what it answers.
      const rows = q.get('kind') === 'art' ? [row({ id: 'b', name: 'Tableau', kind: 'art' })] : [row(), row({ id: 'b', name: 'Tableau', kind: 'art' })];
      return HttpResponse.json(list(rows, undefined, ['art', 'vehicle']));
    }),
  );
}

describe('AssetsPage', () => {
  beforeAll(async () => { await import('./AssetsPage'); }, 30_000);

  it('lists the server rows with kind and value', async () => {
    serve([]);
    await renderApp('/other-assets');

    const table = await screen.findByRole('table', { name: 'Liste des biens' });
    const [first, second] = within(table).getAllByRole('row').slice(1);
    expect(first).toHaveTextContent('Citadine');
    expect(first).toHaveTextContent('Véhicule');
    expect(first).toHaveTextContent('8 000,00 €');
    expect(second).toHaveTextContent('Œuvre d’art');
    expect(within(first!).getByRole('link', { name: 'Citadine' })).toHaveAttribute('href', '/other-assets/0192f0bb-0000-7000-8000-000000000001');
    expect(screen.getByRole('link', { name: 'Ajouter un bien' })).toHaveAttribute('href', '/other-assets/new');
  });

  it('writes the kind filter to the URL and shows what the server answers, without filtering itself', async () => {
    const seen: URLSearchParams[] = [];
    serve(seen);
    const { user, router } = await renderApp('/other-assets');
    await screen.findByRole('table', { name: 'Liste des biens' });

    await pick(user, 'Type', 'Œuvre d’art');

    await waitFor(() => expect(router.state.location.search).toMatchObject({ kind: 'art' }));
    await waitFor(() => expect(seen.at(-1)!.get('kind')).toBe('art'));
    await waitFor(() => expect(within(screen.getByRole('table', { name: 'Liste des biens' })).getAllByRole('row')).toHaveLength(2));
    expect(screen.queryByText('Citadine')).not.toBeInTheDocument();
  });

  it('folds the assets of the same owners into one row with the server total, unfolded on demand', async () => {
    const alice = [{ personId: '0192f0aa-0000-7000-8000-0000000000a1', shareBp: 10000 }];
    const car = row({ id: 'a', name: 'Citadine', owners: alice });
    const painting = row({ id: 'b', name: 'Tableau', kind: 'art', owners: alice, value: { amount: '2000.00', currency: 'EUR' } });
    const bike = row({ id: 'c', name: 'Vélo', kind: 'other' });
    server.use(
      http.get('*/api/v1/core/persons', () => HttpResponse.json(persons)),
      http.get('*/api/v1/assets', () => HttpResponse.json(list([car, painting, bike], [
        { owners: alice, items: ['a', 'b'], value: { amount: '10000.00', currency: 'EUR' } },
        { owners: bike.owners, items: ['c'], value: bike.value },
      ]))),
    );
    const { user } = await renderApp('/other-assets');

    const table = await screen.findByRole('table', { name: 'Liste des biens' });
    const rows = () => within(table).getAllByRole('row').slice(1);
    expect(rows()).toHaveLength(2);
    expect(rows()[0]).toHaveTextContent('2 biens');
    expect(rows()[0]).toHaveTextContent('10 000,00 €');
    expect(rows()[1]).toHaveTextContent('Vélo');
    await user.click(within(rows()[0]!).getByRole('button', { name: 'Déplier les biens' }));
    expect(rows().map((r) => within(r).getAllByRole('cell')[0]!.textContent)).toEqual(['2 biens', 'CitadinePartagé', 'TableauPartagé', 'VéloPartagé']);
  });

  it('lists every asset on its own row once the grouping is switched off, and keeps that in the URL', async () => {
    const alice = [{ personId: '0192f0aa-0000-7000-8000-0000000000a1', shareBp: 10000 }];
    server.use(
      http.get('*/api/v1/core/persons', () => HttpResponse.json(persons)),
      http.get('*/api/v1/assets', () => HttpResponse.json(list([row({ id: 'a', name: 'Citadine', owners: alice }), row({ id: 'b', name: 'Tableau', owners: alice })],
        [{ owners: alice, items: ['a', 'b'], value: { amount: '10000.00', currency: 'EUR' } }]))),
    );
    const { user, router } = await renderApp('/other-assets');

    const table = await screen.findByRole('table', { name: 'Liste des biens' });
    expect(within(table).getAllByRole('row').slice(1)).toHaveLength(1);
    await user.click(screen.getByRole('switch', { name: 'Regrouper par propriétaire' }));

    await waitFor(() => expect(router.state.location.search).toMatchObject({ flat: 1 }));
    expect(within(await screen.findByRole('table', { name: 'Liste des biens' })).getAllByRole('row').slice(1)).toHaveLength(2);
    await user.click(screen.getByRole('switch', { name: 'Regrouper par propriétaire' }));
    await waitFor(() => expect(router.state.location.search).not.toHaveProperty('flat'));
  });
});
