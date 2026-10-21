import type { ReactElement } from 'react';
import { createMemoryHistory, createRootRoute, createRouter, RouterProvider } from '@tanstack/react-router';
import { render as rtlRender, screen, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import type { MonthView } from '../api';
import { eur } from '../testData';
import { MonthsChart } from './MonthsChart';

// The month links need a router; the route tree is just this chart.
const render = async (ui: ReactElement) => {
  const router = createRouter({ routeTree: createRootRoute({ component: () => ui }), history: createMemoryHistory() });
  await router.load();
  return rtlRender(<RouterProvider router={router} />);
};

const year = (over: Partial<Record<string, Partial<MonthView>>> = {}): MonthView[] =>
  Array.from({ length: 12 }, (_, i) => {
    const month = `2026-${String(i + 11 > 12 ? i - 1 : i + 11).padStart(2, '0')}`;
    return { month, recurring: eur('17.20'), longCycle: eur('0.00'), ...over[month] };
  });

describe('MonthsChart', () => {
  it('Each_month_has_a_row_in_the_accessible_table', async () => {
    await render(<MonthsChart months={year()} />);

    const table = screen.getByRole('table', { name: '12 prochains mois' });
    const rows = within(table).getAllByRole('row').slice(1);
    expect(rows).toHaveLength(12);
    expect(rows[0]).toHaveTextContent('nov.');
    expect(rows[0]).toHaveTextContent('17,20 €');
  });

  it('A_null_half_shows_a_dash', async () => {
    await render(<MonthsChart months={year({ '2026-11': { recurring: null } })} />);

    const row = within(screen.getByRole('table', { name: '12 prochains mois' })).getAllByRole('row')[1]!;
    expect(row).toHaveTextContent('nov.—0,00 €');
  });

  it('The_label_shows_both_server_amounts_not_a_sum', async () => {
    await render(<MonthsChart months={year({ '2026-12': { longCycle: eur('216.00') } })} />);

    expect(screen.getByText('17,20 € + 216,00 €')).toBeInTheDocument();
    expect(screen.queryByText('233,20 €')).not.toBeInTheDocument();
  });

  it('The_month_table_shows_itself_while_a_link_has_focus', async () => {
    await render(<MonthsChart months={year()} />);

    const wrapper = screen.getByTestId('months-table');
    expect(wrapper).toHaveClass('sr-only', 'focus-within:not-sr-only');
    expect(within(wrapper).getAllByRole('link')).toHaveLength(12);
  });
});
