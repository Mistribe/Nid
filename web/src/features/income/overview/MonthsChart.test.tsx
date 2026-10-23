import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { reduceMotion } from '@/test/motion';
import { eur } from '../testData';
import { MonthsChart } from './MonthsChart';

const month = (n: number, over = {}) => ({
  year: 2026, month: n, upcoming: false, recurring: eur('1000.00'), oneOff: eur('0.00'), total: eur('1000.00'), previousYear: eur('900.00'), bySource: [], ...over,
});
const months = [month(1), month(2, { oneOff: eur('50.00'), total: eur('1050.00') }), month(3, { upcoming: true }), month(4, { recurring: null, oneOff: null, total: null })];

const show = () => render(<MonthsChart months={months} missingRates={['USD']} period="year" year={2026} />);

describe('MonthsChart', () => {
  reduceMotion();
  it('draws one bar group per month, dims the upcoming ones and names each series', async () => {
    const { container } = show();
    const region = screen.getByRole('region', { name: 'Mois par mois · 2026 vs 2025' });
    // The April month without a total draws no stacked bar.
    // Bar layers come in declaration order: last year, recurring, one-off.
    const recurring = container.querySelectorAll('.recharts-bar')[1]!.querySelectorAll('.recharts-bar-rectangle');
    expect(recurring).toHaveLength(3);
    expect([...recurring].map((b) => b.querySelector('path')?.getAttribute('fill-opacity'))).toEqual(['1', '1', '0.4']);
    // A legend names each series: the colours are never alone.
    for (const name of ['Année précédente', 'Récurrent', 'Exceptionnel']) expect(await within(region).findByText(name)).toBeInTheDocument();
  });

  it('scrolls sideways over three years instead of squeezing 36 months', () => {
    const { container } = render(<MonthsChart months={months} missingRates={[]} period="3y" year={2026} />);
    expect(container.querySelector('[data-chart]')).toHaveClass('min-w-[48rem]');
    expect(container.querySelector('[data-chart]')!.parentElement).toHaveClass('overflow-x-auto');
  });

  it('the table gives the server figures month by month', async () => {
    show();
    await userEvent.click(screen.getByRole('button', { name: 'Afficher en tableau' }));
    const rows = within(screen.getByRole('table', { name: 'Revenus mois par mois' })).getAllByRole('row');
    expect(within(rows[2]!).getByText('1 050,00 €')).toBeInTheDocument();
    expect(within(rows[2]!).getByText('50,00 €')).toBeInTheDocument();
    expect(within(rows[2]!).getByText('900,00 €')).toBeInTheDocument();
    expect(within(rows[3]!).getByRole('rowheader')).toHaveTextContent('mars 2026 · à venir');
  });

  it('a month without a rate says which currency is missing', async () => {
    show();
    await userEvent.click(screen.getByRole('button', { name: 'Afficher en tableau' }));
    expect(screen.getByText('taux manquant (USD)')).toBeInTheDocument();
  });
});
