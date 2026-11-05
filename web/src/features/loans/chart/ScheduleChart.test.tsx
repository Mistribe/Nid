import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { schedule } from '../testData';
import { ScheduleChart } from './ScheduleChart';

describe('ScheduleChart', () => {
  it('is a named figure, described by the summary the page writes', () => {
    render(<ScheduleChart years={schedule().years} currency="EUR" summaryId="summary-id" />);

    expect(screen.getByRole('figure', { name: 'Graphique de l’échéancier par année' })).toHaveAttribute('aria-describedby', 'summary-id');
  });

  it('capital is the accent and interest the neutral tone', () => {
    const { container } = render(<ScheduleChart years={schedule().years} currency="EUR" summaryId="summary-id" />);
    const chart = container.querySelector<HTMLElement>('[data-chart]')!;

    expect(chart.style.getPropertyValue('--color-principal')).toBe('var(--primary)');
    expect(chart.style.getPropertyValue('--color-interest')).toBe('#9E9E9E');
  });

  it('draws the balance in a chart of its own: one vertical axis per chart', () => {
    const { container } = render(<ScheduleChart years={schedule().years} currency="EUR" summaryId="summary-id" />);

    expect(screen.getByRole('figure', { name: 'Capital restant dû par année' })).toBeInTheDocument();
    expect(container.querySelector('[yAxisId]')).toBeNull();
    expect(container.innerHTML).not.toContain('orientation="right"');
  });
});
