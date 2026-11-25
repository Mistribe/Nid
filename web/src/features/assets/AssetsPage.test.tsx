import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { pick } from '@/test/controls';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { persons } from '../subscriptions/testData';
import { row } from './testData';

function serve(seen: URLSearchParams[]) {
  server.use(
    http.get('*/api/v1/core/persons', () => HttpResponse.json(persons)),
    http.get('*/api/v1/assets', ({ request }) => {
      const q = new URL(request.url).searchParams;
      seen.push(q);
      // The server filters: the page only shows what it answers.
      const rows = q.get('kind') === 'art' ? [row({ id: 'b', name: 'Tableau', kind: 'art' })] : [row(), row({ id: 'b', name: 'Tableau', kind: 'art' })];
      return HttpResponse.json({ rows, total: rows.length, kinds: ['art', 'vehicle'] });
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
});
