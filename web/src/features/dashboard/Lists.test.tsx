import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { DashboardView } from './api';
import { dashboard, event } from './testData';

function serve(body: DashboardView) {
  server.use(http.get('*/api/v1/dashboard', () => HttpResponse.json(body)));
}

/** Serves `count` pending proposals; resolves once the dashboard has fetched and settled them. */
function servePending(count: number) {
  let served = false;
  server.use(http.get('*/api/v1/core/proposals', () => { served = true; return HttpResponse.json({ items: Array.from({ length: count }, (_, i) => ({ id: String(i) })) }); }));
  return async (client: { isFetching: () => number }) => {
    await waitFor(() => expect(served).toBe(true));
    await waitFor(() => expect(client.isFetching()).toBe(0));
  };
}

const section = (name: string) => screen.findByRole('region', { name });

// Lazy chunks load cold on the first render; warm them so no findBy races its 1 s default.
beforeAll(async () => { await import('./NetWorthCurve'); }, 30_000);

describe('dashboard lists', () => {
  it('lists what is coming in the next 60 days, each linked to its item', async () => {
    serve(dashboard({
      upcoming: [
        event(),
        event({ on: '2025-11-30', kind: 'subscription_expiry', name: 'NordVPN', amount: null }),
        event({ on: '2025-12-01', kind: 'income_phase_end', module: 'income', id: '00000000-0000-7000-8000-0000000000b1', name: 'Employeur', label: 'CDD', amount: null }),
      ],
    }));
    const { container } = await renderApp('/dashboard');

    const items = within(await section('À venir')).getAllByRole('listitem');
    expect(items[0]).toHaveTextContent('10 novembre 2025');
    expect(items[0]).toHaveTextContent('Amazon Prime');
    expect(items[0]).toHaveTextContent('Renouvellement');
    expect(items[0]).toHaveTextContent('69,90 €');
    expect(items[1]).toHaveTextContent('Expire');
    expect(items[2]).toHaveTextContent('Fin de « CDD »');
    expect(within(items[2] as HTMLElement).getByRole('link', { name: 'Employeur' })).toHaveAttribute('href', '/income/00000000-0000-7000-8000-0000000000b1');
    await expectNoAxeViolations(container);
  });

  it('says what each real estate event is, a planned cost by its category with its amount', async () => {
    const property = { module: 'properties', id: '00000000-0000-7000-8000-0000000000f1', name: 'Maison' };
    const loan = { module: 'loans', id: '00000000-0000-7000-8000-0000000000f2', name: 'Prêt travaux', amount: null };
    serve(dashboard({
      upcoming: [
        event({ ...property, on: '2025-10-20', kind: 'property_cost_due', label: 'property_tax', amount: { amount: '1200.00', currency: 'EUR' } }),
        event({ ...property, on: '2025-10-26', kind: 'valuation_due', amount: null }),
        event({ ...loan, on: '2025-11-15', kind: 'loan_end' }),
        event({ ...loan, on: '2025-11-15', kind: 'loan_phase_end', label: '1' }),
        event({ ...property, on: '2025-12-01', kind: 'tenancy_end', label: 'T2 meublé', amount: null }),
      ],
    }));
    const { container } = await renderApp('/dashboard');

    const items = within(await section('À venir')).getAllByRole('listitem');
    expect(items[0]).toHaveTextContent('Taxe foncière');
    expect(items[0]).toHaveTextContent('1 200,00 €');
    expect(within(items[0] as HTMLElement).getByRole('link', { name: 'Maison' })).toHaveAttribute('href', '/properties/00000000-0000-7000-8000-0000000000f1');
    expect(items[1]).toHaveTextContent('Estimation à refaire');
    expect(items[2]).toHaveTextContent('Dernière échéance');
    expect(within(items[2] as HTMLElement).getByRole('link', { name: 'Prêt travaux' })).toHaveAttribute('href', '/loans/00000000-0000-7000-8000-0000000000f2');
    expect(items[3]).toHaveTextContent('Fin de la phase 1 : taux ou différé qui change');
    expect(items[4]).toHaveTextContent('Fin du bail « T2 meublé »');
    await expectNoAxeViolations(container);
  });

  it('says nothing is coming in the next 60 days', async () => {
    serve(dashboard());
    await renderApp('/dashboard');
    expect(await section('À venir')).toHaveTextContent('Rien dans les 60 prochains jours.');
  });

  it('shows the proposals waiting as a line of their own, nothing at zero', async () => {
    serve(dashboard());
    const settled = servePending(1);
    const { queryClient } = await renderApp('/dashboard');

    expect(await screen.findByRole('link', { name: '1 proposition à valider' })).toHaveAttribute('href', '/proposals');
    await settled(queryClient);
  });
});
