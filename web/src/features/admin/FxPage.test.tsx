import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { adminCapabilities } from '@/test/fixtures';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { toasts } from '@/test/toasts';
import { formatInstant } from '@/lib/format';

const run = {
  id: '01926b9e-0000-7000-8000-0000000000f1', kind: 'backfill', startedAt: '2026-09-24T07:00:00+00:00',
  finishedAt: '2026-09-24T07:02:00+00:00', status: 'succeeded', filesRead: 2, rowsWritten: 1200, failureCode: null, rejected: [],
};

describe('Administration › Taux de change', () => {
  it('lists the collection runs', async () => {
    server.use(http.get('*/api/v1/core/fx-rates/runs', () => HttpResponse.json([run])));
    const { container } = await renderApp('/admin/fx', { capabilities: adminCapabilities });

    const row = await screen.findByRole('row', { name: /rattrapage/ });
    expect(row).toHaveTextContent('réussi');
    expect(row).toHaveTextContent(formatInstant(run.startedAt));
    expect(row).toHaveTextContent('1200');
    await expectNoAxeViolations(container);
  });

  it('asks for a collection, and says when one is already running', async () => {
    let calls = 0;
    server.use(
      http.get('*/api/v1/core/fx-rates/runs', () => HttpResponse.json([])),
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
      http.get('*/api/v1/core/fx-rates/runs', () => HttpResponse.json([])),
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
      http.get('*/api/v1/core/fx-rates/runs', () => HttpResponse.json([])),
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

  it('opens a currency against the dollar', async () => {
    server.use(http.get('*/api/v1/core/fx-rates/runs', () => HttpResponse.json([])));
    const { user, router } = await renderApp('/admin/fx', { capabilities: adminCapabilities });

    await user.type(await screen.findByLabelText('Devise'), 'eur');
    await user.click(screen.getByRole('button', { name: 'Voir la série' }));
    await waitFor(() => expect(router.state.location.pathname).toBe('/series'));
    expect(router.state.location.search).toMatchObject({ s: 'fx:EUR' });
  });

  it('refuses USD and a code that is not three letters', async () => {
    server.use(http.get('*/api/v1/core/fx-rates/runs', () => HttpResponse.json([])));
    const { user, router } = await renderApp('/admin/fx', { capabilities: adminCapabilities });

    const field = await screen.findByLabelText('Devise');
    await user.type(field, 'USD');
    await user.click(screen.getByRole('button', { name: 'Voir la série' }));
    expect(await screen.findByText(/Trois lettres, autre que USD/)).toBeInTheDocument();
    await user.clear(field);
    await user.type(field, 'EU');
    await user.click(screen.getByRole('button', { name: 'Voir la série' }));
    expect(screen.getByText(/Trois lettres, autre que USD/)).toBeInTheDocument();
    expect(router.state.location.pathname).toBe('/admin/fx');
  });
});
