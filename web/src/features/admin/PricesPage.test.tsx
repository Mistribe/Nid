import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { adminCapabilities } from '@/test/fixtures';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { toasts } from '@/test/toasts';
import { formatInstant } from '@/lib/format';

const settings = { maxAdults: 2, referenceCurrency: 'EUR', adultCount: 2 };
const run = {
  id: '01926b9e-0000-7000-8000-0000000000f2', startedAt: '2026-10-22T19:00:00+00:00', finishedAt: '2026-10-22T19:01:00+00:00',
  status: 'succeeded', queried: 12, accepted: 9, rejected: 1, notFound: 2, sourceErrors: 0, failureCode: null,
};

const noRuns = () => http.get('*/api/v1/investments/prices/runs', () => HttpResponse.json([]));

describe('Administration › Cours', () => {
  it('lists the runs with their counts', async () => {
    server.use(
      http.get('*/api/v1/admin/household', () => HttpResponse.json({ ...settings, pricesEnabled: true })),
      http.get('*/api/v1/investments/prices/runs', () => HttpResponse.json([run])),
    );
    const { container } = await renderApp('/admin/prices', { capabilities: adminCapabilities });

    const row = await screen.findByRole('row', { name: /réussi/ });
    expect(row).toHaveTextContent(formatInstant(run.startedAt));
    expect(within(row).getAllByRole('cell').map((c) => c.textContent).slice(2)).toEqual(['12', '9', '1', '2', '0', '']);
    await expectNoAxeViolations(container);
  });

  it('turns the collection on and off from the switch', async () => {
    const sent: unknown[] = [];
    server.use(
      http.get('*/api/v1/admin/household', () => HttpResponse.json({ ...settings, pricesEnabled: false })),
      noRuns(),
      http.patch('*/api/v1/admin/household', async ({ request }) => {
        sent.push(await request.json());
        return HttpResponse.json({ ...settings, pricesEnabled: true });
      }),
    );
    const { user } = await renderApp('/admin/prices', { capabilities: adminCapabilities });

    const toggle = await screen.findByRole('switch', { name: 'Récupérer les cours' });
    expect(toggle).not.toBeChecked();
    expect(screen.getByRole('button', { name: 'Mettre à jour maintenant' })).toBeDisabled();
    await user.click(toggle);

    await waitFor(() => expect(sent).toEqual([{ pricesEnabled: true }]));
    await waitFor(() => expect(toasts()).toHaveTextContent('Enregistré'));
  });

  it('asks for an update, and says when one is already running', async () => {
    let calls = 0;
    server.use(
      http.get('*/api/v1/admin/household', () => HttpResponse.json({ ...settings, pricesEnabled: true })),
      noRuns(),
      http.post('*/api/v1/investments/prices/sync', () => {
        calls += 1;
        return calls === 1
          ? HttpResponse.json({ requested: true }, { status: 202 })
          : HttpResponse.json({ status: 409, code: 'investments.price_sync_running', title: 'Running.' }, { status: 409 });
      }),
    );
    const { user } = await renderApp('/admin/prices', { capabilities: adminCapabilities });

    await user.click(await screen.findByRole('button', { name: 'Mettre à jour maintenant' }));
    expect(calls).toBe(0);
    await user.click(await screen.findByRole('button', { name: 'Confirmer' }));
    await waitFor(() => expect(toasts()).toHaveTextContent('Mise à jour demandée'));

    await user.click(screen.getByRole('button', { name: 'Mettre à jour maintenant' }));
    await user.click(await screen.findByRole('button', { name: 'Confirmer' }));
    await waitFor(() => expect(toasts()).toHaveTextContent('Une mise à jour est déjà en cours'));
  });
});
