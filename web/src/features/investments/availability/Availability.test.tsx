import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { EnvelopeDetail } from '@/api/investments';
import { detail, eur, PEA } from '../testData';

const serve = (body: EnvelopeDetail) => server.use(http.get('*/api/v1/investments/envelopes/:id', () => HttpResponse.json(body)));

describe('a PEE\'s availability (#184)', () => {
  beforeAll(async () => { await import('../EnvelopeDetailPage'); }, 30_000);

  it('shows what is available today, the next release, and the releases to come', async () => {
    serve(detail({
      kind: 'employee_savings',
      availability: {
        availableToday: eur('2000.00'),
        releases: [{ on: '2098-01-01', amount: eur('2000.00') }, { on: '2099-01-01', amount: eur('6000.00') }],
        tranches: [],
      },
    }));
    await renderApp(`/investments/${PEA}`);
    expect(await screen.findByText('Disponible aujourd’hui')).toBeInTheDocument();
    expect(screen.getByText(/^puis 2\s000,00\s€ le/)).toBeInTheDocument();
    const rows = within(screen.getByRole('table', { name: 'Prochaines dates de déblocage' })).getAllByRole('row');
    expect(rows).toHaveLength(3);
    expect(rows[2]).toHaveTextContent(/6\s000,00\s€/);
  });

  it('without dates, says the plan is locked and sends the dates typed from the statement', async () => {
    serve(detail({ kind: 'employee_savings' }));
    let body: unknown;
    server.use(http.patch('*/api/v1/investments/envelopes/:id', async ({ request }) => { body = await request.json(); return HttpResponse.json({}); }));
    const { user } = await renderApp(`/investments/${PEA}`);
    expect(await screen.findByText(/tout le plan compte comme bloqué/)).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Renseigner les dates' }));
    const dialog = await screen.findByRole('dialog', { name: 'Dates de disponibilité' });
    await user.type(within(dialog).getByLabelText('Disponible le'), '01/03/2030');
    await user.type(within(dialog).getByLabelText('Montant (EUR)'), '1500,50');
    await user.click(within(dialog).getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(body).toMatchObject({ availability: [{ availableOn: '2030-03-01', amount: '1500.50' }] }));
  });

  it('shows an early-release case with what it frees, and sends one typed in (#466)', async () => {
    serve(detail({
      kind: 'employee_savings',
      availability: {
        availableToday: eur('2000.00'), releases: [], tranches: [], locked: eur('8000.00'),
        earlyRelease: { case: 'marriage', on: '2026-03-01', amount: eur('2000.00'), released: eur('2000.00') },
      },
    }));
    let body: unknown;
    server.use(http.patch('*/api/v1/investments/envelopes/:id', async ({ request }) => { body = await request.json(); return HttpResponse.json({}); }));
    const { user } = await renderApp(`/investments/${PEA}`);
    expect(await screen.findByText(/^Mariage ou PACS, à partir du .* : 2\s000,00\s€ débloqués avant leurs dates\./)).toBeInTheDocument();
    expect(screen.getByText(/^8\s000,00\s€ restent bloqués sans date connue\./)).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Déblocage anticipé' }));
    const dialog = await screen.findByRole('dialog', { name: 'Déblocage anticipé' });
    await user.clear(within(dialog).getByLabelText(/^Montant/));
    await user.click(within(dialog).getByRole('button', { name: 'Enregistrer' }));
    await waitFor(() => expect(body).toMatchObject({ earlyRelease: { case: 'marriage', on: '2026-03-01', amount: null } }));
  });

  it('is not shown on other kinds', async () => {
    serve(detail());
    await renderApp(`/investments/${PEA}`);
    await screen.findByRole('tab', { name: 'Vue d’ensemble' });
    expect(screen.queryByRole('heading', { name: 'Disponibilité' })).toBeNull();
  });
});
