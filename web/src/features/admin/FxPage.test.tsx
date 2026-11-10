import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { adminCapabilities } from '@/test/fixtures';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { toasts } from '@/test/toasts';
import { formatInstant } from '@/lib/format';
import { fromOf } from '@/features/series/selection';

const run = {
  id: '01926b9e-0000-7000-8000-0000000000f1', kind: 'backfill', startedAt: '2026-09-24T07:00:00+00:00',
  finishedAt: '2026-09-24T07:02:00+00:00', status: 'succeeded', filesRead: 2, rowsWritten: 1200, failureCode: null, rejected: [],
};

const empty = { items: [], total: 0, page: 1 };

describe('Administration › Taux de change', () => {
  it('lists the collection runs', async () => {
    server.use(http.get('*/api/v1/core/fx-rates/runs', () => HttpResponse.json({ items: [run], total: 1, page: 1 })));
    const { container, user } = await renderApp('/admin/fx', { capabilities: adminCapabilities });

    await user.click(await screen.findByRole('tab', { name: 'Journal' }));
    const row = await screen.findByRole('row', { name: /rattrapage/ });
    expect(row).toHaveTextContent('réussi');
    expect(row).toHaveTextContent(formatInstant(run.startedAt));
    expect(row).toHaveTextContent('1200');
    await expectNoAxeViolations(container);
  });

  it('pages the log', async () => {
    const seen: string[] = [];
    server.use(http.get('*/api/v1/core/fx-rates/runs', ({ request }) => {
      seen.push(request.url);
      return HttpResponse.json({ items: [run], total: 51, page: 1 });
    }));
    const { user, router } = await renderApp('/admin/fx?tab=journal', { capabilities: adminCapabilities });

    const pages = await screen.findByRole('navigation', { name: 'Pages' });
    expect(pages).toHaveTextContent('Page 1 sur 2');
    await user.click(within(pages).getByRole('button', { name: 'Suivant' }));
    await waitFor(() => expect(router.state.location.search).toEqual({ tab: 'journal', page: 2 }));
    await waitFor(() => expect(seen.at(-1)).toContain('page=2'));
  });

  it('steps back from a page past the end without flashing the empty state', async () => {
    server.use(http.get('*/api/v1/core/fx-rates/runs', ({ request }) => HttpResponse.json(
      new URL(request.url).searchParams.get('page') === '3' ? { items: [], total: 51, page: 3 } : { items: [run], total: 51, page: 2 })));
    const { router } = await renderApp('/admin/fx?tab=journal&page=3', { capabilities: adminCapabilities });

    await waitFor(() => expect(router.state.location.search).toEqual({ tab: 'journal', page: 2 }));
    expect(screen.queryByText("Aucune collecte pour l'instant.")).not.toBeInTheDocument();
    expect(await screen.findByRole('row', { name: /rattrapage/ })).toBeInTheDocument();
  });

  it('asks for a collection, and says when one is already running', async () => {
    let calls = 0;
    server.use(
      http.get('*/api/v1/core/fx-rates/runs', () => HttpResponse.json(empty)),
      http.post('*/api/v1/core/fx-rates/sync', () => {
        calls += 1;
        return calls === 1
          ? HttpResponse.json({ requested: true }, { status: 202 })
          : HttpResponse.json({ status: 409, code: 'core.fx_sync_running', title: 'A rate collection is already running.' }, { status: 409 });
      }),
    );
    const { user } = await renderApp('/admin/fx', { capabilities: adminCapabilities });

    await user.click(await screen.findByRole('button', { name: 'Synchroniser' }));
    expect(calls).toBe(0);
    await user.click(await screen.findByRole('button', { name: 'Confirmer' }));
    await waitFor(() => expect(toasts()).toHaveTextContent('Collecte demandée'));

    await user.click(screen.getByRole('button', { name: 'Synchroniser' }));
    await user.click(await screen.findByRole('button', { name: 'Confirmer' }));
    await waitFor(() => expect(toasts()).toHaveTextContent('Une collecte est déjà en cours'));
  });

  it('enters a manual rate', async () => {
    const sent: Array<{ path: string; body: unknown }> = [];
    server.use(
      http.get('*/api/v1/core/fx-rates/runs', () => HttpResponse.json(empty)),
      http.put('*/api/v1/core/fx-rates/:date/:quote', async ({ request, params }) => {
        sent.push({ path: `${String(params.date)}/${String(params.quote)}`, body: await request.json() });
        return HttpResponse.json({ onDate: params.date, quote: params.quote, rate: '0.8612', source: 'manual' });
      }),
    );
    const { user } = await renderApp('/admin/fx', { capabilities: adminCapabilities });

    await user.click(await screen.findByRole('button', { name: 'Saisir un taux' }));
    await user.type(await screen.findByLabelText('Date'), '01/10/2025');
    await user.type(within(screen.getByRole('dialog')).getByLabelText('Devise'), 'eur');
    await user.type(screen.getByLabelText('Taux pour 1 USD'), '0,8612');
    await user.click(screen.getByRole('button', { name: 'Enregistrer le taux' }));

    await waitFor(() => expect(sent).toEqual([{ path: '2025-10-01/EUR', body: { rate: '0.8612' } }]));
    await waitFor(() => expect(toasts()).toHaveTextContent('Taux enregistré'));
  });

  it('sends nothing, and says why, when the date, the currency or the rate is missing', async () => {
    let calls = 0;
    server.use(
      http.get('*/api/v1/core/fx-rates/runs', () => HttpResponse.json(empty)),
      http.put('*/api/v1/core/fx-rates/:date/:quote', () => { calls += 1; return HttpResponse.json({}); }),
    );
    const { user } = await renderApp('/admin/fx', { capabilities: adminCapabilities });

    await user.click(await screen.findByRole('button', { name: 'Saisir un taux' }));
    await user.type(await screen.findByLabelText('Date'), '31/02/2025');
    await user.type(screen.getByLabelText('Taux pour 1 USD'), '0,86');
    await user.click(screen.getByRole('button', { name: 'Enregistrer le taux' }));
    expect(await screen.findByText(/au format jj\/mm\/aaaa/)).toBeInTheDocument();
    expect(screen.getByText('Ce champ est obligatoire.')).toBeInTheDocument(); // the currency

    await user.clear(screen.getByLabelText('Date'));
    await user.type(screen.getByLabelText('Date'), '01/10/2025');
    await user.type(within(screen.getByRole('dialog')).getByLabelText('Devise'), 'eur');
    await user.clear(screen.getByLabelText('Taux pour 1 USD'));
    await user.click(screen.getByRole('button', { name: 'Enregistrer le taux' }));
    expect(await screen.findByText('Ce champ est obligatoire.')).toBeInTheDocument(); // the rate
    expect(calls).toBe(0);
  });

  const points = (asked: URLSearchParams[]) => http.get('*/api/v1/series/points', ({ request }) => {
    asked.push(new URL(request.url).searchParams);
    return HttpResponse.json({ baseDate: null, series: [] });
  });

  it('draws EUR by default over the last year', async () => {
    const asked: URLSearchParams[] = [];
    server.use(http.get('*/api/v1/core/fx-rates/runs', () => HttpResponse.json(empty)), points(asked));
    await renderApp('/admin/fx', { capabilities: adminCapabilities });

    await waitFor(() => expect(asked.length).toBeGreaterThan(0));
    expect(asked[0]!.get('s')).toBe('fx:EUR');
    expect(asked[0]!.get('from')).toBe(fromOf('1y'));
  });

  it('draws the currency asked', async () => {
    const asked: URLSearchParams[] = [];
    server.use(http.get('*/api/v1/core/fx-rates/runs', () => HttpResponse.json(empty)), points(asked));
    const { user, router } = await renderApp('/admin/fx', { capabilities: adminCapabilities });

    const quote = await screen.findByLabelText('Devise');
    await user.clear(quote);
    await user.type(quote, 'gbp');
    await user.click(screen.getByRole('button', { name: 'Afficher' }));
    await waitFor(() => expect(router.state.location.search).toMatchObject({ quote: 'GBP' }));
    await waitFor(() => expect(asked.some((p) => p.get('s') === 'fx:GBP')).toBe(true));
  });

  it('drops the default currency from the URL', async () => {
    const asked: URLSearchParams[] = [];
    server.use(http.get('*/api/v1/core/fx-rates/runs', () => HttpResponse.json(empty)), points(asked));
    const { user, router } = await renderApp('/admin/fx?quote=CHF', { capabilities: adminCapabilities });

    const field = await screen.findByLabelText('Devise');
    await user.clear(field);
    await user.type(field, 'eur');
    await user.click(screen.getByRole('button', { name: 'Afficher' }));
    await waitFor(() => expect(router.state.location.search).not.toHaveProperty('quote'));
    await waitFor(() => expect(asked.some((p) => p.get('s') === 'fx:EUR')).toBe(true));
  });

  it('refuses an invalid code', async () => {
    server.use(http.get('*/api/v1/core/fx-rates/runs', () => HttpResponse.json(empty)));
    const { user, router } = await renderApp('/admin/fx', { capabilities: adminCapabilities });

    const field = await screen.findByLabelText('Devise');
    await user.clear(field);
    await user.type(field, 'USD');
    await user.click(screen.getByRole('button', { name: 'Afficher' }));
    expect(await screen.findByText(/Trois lettres, autre que USD/)).toBeInTheDocument();
    await user.clear(field);
    expect(screen.queryByText(/Trois lettres, autre que USD/)).not.toBeInTheDocument();
    await user.type(field, 'EU');
    await user.click(screen.getByRole('button', { name: 'Afficher' }));
    expect(screen.getByText(/Trois lettres, autre que USD/)).toBeInTheDocument();
    expect(router.state.location.search).not.toHaveProperty('quote');
  });

  it('keeps the currency across tabs', async () => {
    const asked: URLSearchParams[] = [];
    server.use(http.get('*/api/v1/core/fx-rates/runs', () => HttpResponse.json(empty)), points(asked));
    const { user } = await renderApp('/admin/fx?quote=CHF', { capabilities: adminCapabilities });

    await user.click(await screen.findByRole('tab', { name: 'Journal' }));
    await user.click(await screen.findByRole('tab', { name: 'Série' }));
    expect(await screen.findByLabelText('Devise')).toHaveValue('CHF');
    expect(asked.every((p) => p.get('s') === 'fx:CHF')).toBe(true);
    expect(asked.length).toBeGreaterThan(0);
  });
});
