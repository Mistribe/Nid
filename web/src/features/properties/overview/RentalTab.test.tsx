import { screen, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { PropertiesRental } from '../api';
import { householdRental, PROPERTY, propertiesRental, RENTAL } from '../testData';

const serve = (body: PropertiesRental, seen: string[] = []) =>
  server.use(http.get('*/api/v1/realestate/properties/overview/rental', ({ request }) => {
    const search = new URL(request.url).search;
    seen.push(search);
    // One property alone: its own answer (spec §4.2).
    return HttpResponse.json(search.includes('property=')
      ? propertiesRental({ ...body, count: 1, months: [{ month: '2026-05', rentReceived: { amount: '450.00', currency: 'EUR' }, charges: null, loanPayments: null, cashFlow: null }] })
      : body);
  }));
const region = (name: string) => screen.findByRole('region', { name });

describe('Biens › location', () => {
  beforeAll(async () => { await import('../PropertiesPage'); await import('./RentalTab'); }, 30_000);

  it('is absent while no property is let', async () => {
    serve(propertiesRental());
    await renderApp('/properties');

    await screen.findByRole('tab', { name: 'Liste' });
    expect(screen.queryByRole('tab', { name: 'Location' })).not.toBeInTheDocument();
  });

  it('shows the server figures of the let properties', async () => {
    serve(householdRental());
    const { container } = await renderApp('/properties?tab=rental');

    expect(await screen.findByRole('tab', { name: 'Location' })).toHaveAttribute('aria-selected', 'true');
    const figures = (await screen.findByText('Loyers reçus', { selector: 'dt' })).closest('dl')!;
    expect(figures).toHaveTextContent('12 060,00 €sur 12 510,00 € attendus');
    // A balance today, not a figure of the 12 months above it.
    expect(figures).toHaveTextContent('Impayés à ce jour450,00 €');
    expect(figures).toHaveTextContent('Vacance2,59 %14 jours sans bail');
    expect(figures).toHaveTextContent('Rendement brut4,98 %net avant impôt 4,38 %1 bien loué depuis moins de 12 mois n’y compte pas');
    expect(figures).toHaveTextContent('Cash-flow-604,00 €en baisseaprès charges et mensualités · ≈ -50,33 € par mois');
    expect(screen.getByText('2 biens loués · du 23 oct. 2025 au 22 oct. 2026')).toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('shows the cash flow with its arrow, the figure uncoloured', async () => {
    serve(householdRental());
    await renderApp('/properties?tab=rental');

    const cash = (await screen.findByText('Cash-flow', { selector: 'dt' })).nextElementSibling!;
    expect(cash.className).not.toMatch(/text-status-good-text|text-destructive/);
    expect(cash.querySelector('svg')).toHaveClass('text-status-critical');
    expect(within(cash as HTMLElement).getByText('en baisse')).toBeInTheDocument();
  });

  it('arrears due show a warning icon beside the amount, the amount uncoloured', async () => {
    serve(householdRental());
    await renderApp('/properties?tab=rental');

    const due = (await screen.findByText('Impayés à ce jour', { selector: 'dt' })).nextElementSibling!;
    const icon = due.querySelector('svg');
    expect(icon).toHaveAttribute('aria-hidden', 'true');
    expect(icon).toHaveClass('text-status-serious');
    expect(due.className).not.toMatch(/text-status-|text-destructive/);
    expect(due).toHaveTextContent('450,00 €');
  });

  it('lists each let property with its lease and cash flow, each row to its rental tab', async () => {
    serve(householdRental());
    await renderApp('/properties?tab=rental');

    const table = await screen.findByRole('table', { name: 'Location par bien' });
    expect(within(table).getAllByRole('columnheader').map((h) => h.textContent))
      .toEqual(['Bien', 'Bail en cours', 'Loyer', 'Reçu', 'Impayés', 'Vacance', 'Rendement brut', 'Rendement net', 'Cash-flow']);
    const [flat, studio] = within(table).getAllByRole('row').slice(1);
    expect(flat).toHaveTextContent('Bail Martin, depuis le 1 mars 2024');
    expect(flat).toHaveTextContent('-3 304,00 €');
    expect(flat).toHaveTextContent('À jour');
    expect(within(flat!).getByRole('link', { name: /T2 Villeurbanne/ })).toHaveAttribute('href', `/properties/${RENTAL}?tab=rental`);
    expect(studio).toHaveTextContent('Bail Dupont, jusqu’au 30 nov. 2026');
    expect(studio).toHaveTextContent('+450,00 €');
    // A lease ending within 60 days is named.
    expect(await region('Baux')).toHaveTextContent('Studio : le bail se termine le 30 nov. 2026.');
  });

  it('reads a property paid ahead as up to date, more received than expected', async () => {
    const d = householdRental();
    serve({ ...d, properties: d.properties.map((p) => (p.name === 'Studio' ? { ...p, arrears: '-120.00' } : p)) });
    await renderApp('/properties?tab=rental');

    const table = await screen.findByRole('table', { name: 'Location par bien' });
    const studio = within(table).getAllByRole('row').slice(1)[1]!;
    expect(studio).toHaveTextContent('À jourplus perçu que prévu');
    expect(studio).not.toHaveTextContent('120,00');
  });

  it('shows one property alone from its own answer, kept in the URL, and the months as a table', async () => {
    const seen: string[] = [];
    serve(householdRental(), seen);
    const { user } = await renderApp('/properties?tab=rental');

    const chart = await region('Loyers, charges et mensualités par mois');
    await user.click(within(chart).getByRole('button', { name: 'Afficher en tableau' }));
    expect(within(within(chart).getByRole('table')).getAllByRole('row')[1]).toHaveTextContent('oct. 20250,00 €1 120,00 €0,00 €-1 120,00 €');

    await user.click(within(chart).getByRole('radio', { name: 'Studio' }));
    expect(seen.at(-1)).toBe(`?scope=household&property=${PROPERTY}`);
    expect(await within(chart).findByText('450,00 €')).toBeInTheDocument();
  });
});
