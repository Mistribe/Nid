import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { todayIso } from '@/lib/today';
import type { DashboardView } from '@/api/dashboard';
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
        event({ on: '2025-11-30', kind: 'subscription_cancel_deadline', name: 'Assurance habitation', amount: null }),
        event({ on: '2025-12-01', kind: 'income_phase_end', module: 'income', id: '00000000-0000-7000-8000-0000000000b1', name: 'Employeur', label: 'CDD', amount: null }),
      ],
    }));
    const { container } = await renderApp('/dashboard');

    // The first three, then « Tout voir » (issue 391: the card stays about the curve's height).
    expect(within(await section('À venir')).getAllByRole('listitem')).toHaveLength(3);
    await userEvent.click(within(await section('À venir')).getByRole('button', { name: 'Tout voir (4)' }));
    const items = within(await section('À venir')).getAllByRole('listitem');
    expect(items).toHaveLength(4);
    expect(within(await section('À venir')).getByRole('button', { name: 'Voir moins' })).toBeInTheDocument();
    // One list per day, named by its date (« Aujourd’hui » and « Demain » only near today).
    expect(within(await section('À venir')).getByRole('list', { name: 'lundi 10 novembre 2025' })).toContainElement(items[0] as HTMLElement);
    expect(items[0]).toHaveTextContent('Amazon Prime');
    expect(items[0]).toHaveTextContent('Renouvellement');
    expect(items[0]).toHaveTextContent('69,90 €');
    expect(items[1]).toHaveTextContent('Expire');
    expect(items[2]).toHaveTextContent('Dernier jour pour résilier');
    expect(items[3]).toHaveTextContent('Fin de « CDD »');
    expect(within(items[3] as HTMLElement).getByRole('link', { name: /Employeur/ })).toHaveAttribute('href', '/income/00000000-0000-7000-8000-0000000000b1');
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

    await userEvent.click(within(await section('À venir')).getByRole('button', { name: 'Tout voir (5)' }));
    const items = within(await section('À venir')).getAllByRole('listitem');
    expect(items[0]).toHaveAttribute('data-tile', '3');
    expect(items[0]).toHaveTextContent('Taxe foncière');
    expect(items[0]).toHaveTextContent('1 200,00 €');
    expect(within(items[0] as HTMLElement).getByRole('link', { name: /Maison/ })).toHaveAttribute('href', '/properties/00000000-0000-7000-8000-0000000000f1');
    expect(items[1]).toHaveTextContent('Estimation à refaire');
    expect(items[2]).toHaveTextContent('Dernière échéance');
    expect(within(items[2] as HTMLElement).getByRole('link', { name: 'Dernière échéance Prêt travaux' })).toHaveAttribute('href', '/loans/00000000-0000-7000-8000-0000000000f2');
    expect(items[3]).toHaveTextContent('Fin de la phase 1 : le taux ou la mensualité change');
    expect(items[4]).toHaveTextContent('Fin du bail « T2 meublé »');
    await expectNoAxeViolations(container);
  });

  it('falls back to the neutral tile for a module with no page', async () => {
    serve(dashboard({ upcoming: [event({ module: 'unknown', id: null })] }));
    await renderApp('/dashboard');
    expect(within(await section('À venir')).getAllByRole('listitem')[0]).toHaveAttribute('data-tile', 'neutral');
  });

  it('shows the PER deadline of 31 December, linked to the tax page', async () => {
    const per = { on: '2025-12-31', module: 'tax', id: null, amount: { amount: '2920.00', currency: 'EUR' } };
    serve(dashboard({
      upcoming: [
        event({ ...per, kind: 'per_deadline', name: '', label: 'household' }),
        event({ ...per, kind: 'per_ceiling_expiring', name: 'Alice', amount: { amount: '1000.00', currency: 'EUR' } }),
      ],
    }));
    const { container } = await renderApp('/dashboard');

    const items = within(await section('À venir')).getAllByRole('listitem');
    expect(items[0]).toHaveTextContent('encore verser avant le 31 décembre');
    expect(items[0]).toHaveTextContent('2 920,00 €');
    expect(within(items[0] as HTMLElement).getByRole('link', { name: /Foyer \(plafonds mutualisés\)/ })).toHaveAttribute('href', '/tax');
    expect(items[1]).toHaveTextContent('minimum à verser avant le 31 décembre pour ne pas perdre de déduction');
    expect(within(items[1] as HTMLElement).getByRole('link', { name: 'Épargne retraite (PER) : minimum à verser avant le 31 décembre pour ne pas perdre de déduction Alice 1 000,00 €' })).toHaveAttribute('href', '/tax');
    await expectNoAxeViolations(container);
  });

  it('announces an envelope\'s tax date, linked to the envelope (issue 400)', async () => {
    serve(dashboard({ upcoming: [event({ on: '2025-12-01', kind: 'tax_milestone', module: 'investments', id: 'e1', name: 'Vita', label: 'life_insurance_8y', amount: null })] }));
    await renderApp('/dashboard');
    const item = within(await section('À venir')).getAllByRole('listitem')[0] as HTMLElement;
    expect(item).toHaveTextContent('8 ans du contrat : la fiscalité des retraits change');
    expect(within(item).getByRole('link')).toHaveAttribute('href', '/investments/e1');
  });

  it('shows the tax calendar: the household’s dates on the tax page, the property tax on its property', async () => {
    const tax = { module: 'tax', id: null, name: '', label: null, amount: null };
    serve(dashboard({
      upcoming: [
        event({ ...tax, on: '2026-05-21', kind: 'tax_filing_deadline', label: 'department_unknown' }),
        event({ ...tax, on: '2026-09-25', kind: 'tax_balance_due', amount: { amount: '512.00', currency: 'EUR' } }),
        event({ on: '2026-10-15', kind: 'property_tax_due', module: 'properties', id: 'p1', name: 'Maison', label: null, amount: null }),
      ],
    }));
    const { container } = await renderApp('/dashboard');

    const items = within(await section('À venir')).getAllByRole('listitem');
    expect(items[0]).toHaveTextContent('Impôt : date limite de la déclaration en ligne');
    expect(items[0]).toHaveTextContent('renseignez le département dans le profil fiscal');
    expect(within(items[0] as HTMLElement).getByRole('link')).toHaveAttribute('href', '/tax');
    expect(items[1]).toHaveTextContent('512,00 €');
    expect(within(items[2] as HTMLElement).getByRole('link', { name: /Taxe foncière à payer Maison/ })).toHaveAttribute('href', '/properties/p1');
    await expectNoAxeViolations(container);
  });

  it('names today and tomorrow rather than their dates', async () => {
    const today = todayIso();
    const [y, m, d] = today.split('-').map(Number) as [number, number, number];
    const tomorrow = new Date(Date.UTC(y, m - 1, d + 1)).toISOString().slice(0, 10);
    serve(dashboard({ upcoming: [event({ on: today }), event({ on: tomorrow, kind: 'subscription_expiry', name: 'NordVPN', amount: null })] }));
    await renderApp('/dashboard');

    const upcoming = await section('À venir');
    expect(within(upcoming).getByRole('list', { name: 'Aujourd’hui' })).toHaveTextContent('Amazon Prime');
    expect(within(upcoming).getByRole('list', { name: 'Demain' })).toHaveTextContent('NordVPN');
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
