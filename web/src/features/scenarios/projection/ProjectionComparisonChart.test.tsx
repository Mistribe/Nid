import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { ProjectionComparisonChart } from './ProjectionComparisonChart';
import { comparison } from './testData';
import '@/locales/scenarios';

describe('ProjectionComparisonChart', () => {
  it('draws the base and the scenario as two lines, in the fixed chart colours, with the scenario’s milestones', async () => {
    const { container } = render(<ProjectionComparisonChart comparison={comparison()} real />);

    expect(screen.getByRole('list', { name: 'Légende' })).toHaveTextContent('Base');
    expect(screen.getByRole('list', { name: 'Légende' })).toHaveTextContent('Scénario');
    const chart = container.querySelector<HTMLElement>('[data-chart]')!;
    expect(chart.style.getPropertyValue('--color-base')).toBe('var(--chart-1)');
    expect(chart.style.getPropertyValue('--color-scenario')).toBe('var(--chart-2)');
    const lines = [...container.querySelectorAll('.recharts-line-curve')].map((l) => l.getAttribute('stroke'));
    expect(lines).toEqual(['var(--color-base)', 'var(--color-scenario)']);
    expect(container.querySelectorAll('.recharts-reference-dot')).toHaveLength(1);
    await expectNoAxeViolations(container);
  });
});
