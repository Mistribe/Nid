import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw/http';
import { describe, expect, it, vi } from 'vitest';
import { createApi } from '@/api/client';
import { createQueryClient } from '@/app/bootstrap';
import { Providers } from '@/app/Providers';
import { aliceSession, adminCapabilities } from '@/test/fixtures';
import { server } from '@/test/server';
import { AdminSeries } from './AdminSeries';

const irl = (points: { date: string; value: string }[]) => ({
  baseDate: null,
  series: [{ key: 'irl', kind: 'irl', name: 'IRL', unit: 'index', frequency: 'quarterly', item: null, points }],
});
const serve = (body: unknown, status = 200, asked: URLSearchParams[] = []) => server.use(
  http.get('*/api/v1/series/points', ({ request }) => { asked.push(new URL(request.url).searchParams); return HttpResponse.json(body as object, { status }); }),
);
const renderSeries = (props: { seriesKey: string; from?: string }) => {
  const queryClient = createQueryClient();
  queryClient.setDefaultOptions({ queries: { ...queryClient.getDefaultOptions().queries, retryDelay: 0 } });
  const api = createApi({ getCsrf: () => aliceSession.csrf, onUnauthenticated: vi.fn() });
  const location = { pathname: '/', search: '', assign: vi.fn(), reload: vi.fn() };
  render(
    <Providers api={api} queryClient={queryClient} session={aliceSession} capabilities={adminCapabilities} location={location}>
      <AdminSeries {...props} />
    </Providers>,
  );
  return userEvent.setup();
};

describe('AdminSeries', () => {
  it('draws the series and offers its table', async () => {
    serve(irl([{ date: '2026-01-01', value: '146.80' }, { date: '2026-04-01', value: '148.37' }]));
    const user = renderSeries({ seriesKey: 'irl' });

    await user.click(await screen.findByRole('button', { name: 'Afficher en tableau' }));
    const table = screen.getByRole('table', { name: 'Valeurs des séries' });
    expect(within(table).getByText('146,8')).toBeInTheDocument();
    expect(within(table).getByText('148,37')).toBeInTheDocument();
  });

  it('says when the series has no point', async () => {
    serve(irl([]));
    renderSeries({ seriesKey: 'irl' });
    expect(await screen.findByText('Aucune donnée sur cette période')).toBeInTheDocument();
  });

  it('shows the problem when the points fail', async () => {
    serve({ status: 500, code: 'server.error', title: 'Boom' }, 500);
    renderSeries({ seriesKey: 'irl' });
    expect(await screen.findByRole('alert')).toHaveTextContent('Une erreur inattendue est survenue. Réessayez dans un instant.');
  });

  it('says there is no data for a quote with no stored rate', async () => {
    serve({ status: 404, code: 'core.series_not_found', key: 'fx:EUR', title: 'Not found' }, 404);
    renderSeries({ seriesKey: 'fx:EUR' });
    expect(await screen.findByText('Aucune donnée sur cette période')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('asks the window it is given', async () => {
    const asked: URLSearchParams[] = [];
    serve(irl([]), 200, asked);
    renderSeries({ seriesKey: 'fx:EUR', from: '2025-11-10' });
    await screen.findByText('Aucune donnée sur cette période');
    expect(asked[0]!.get('s')).toBe('fx:EUR');
    expect(asked[0]!.get('from')).toBe('2025-11-10');
  });
});
