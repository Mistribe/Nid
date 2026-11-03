import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { baseAndScenario } from './chartSeries';
import { ProjectionComparisonChart } from './ProjectionComparisonChart';
import { comparison } from './testData';
import i18n from '@/app/i18n';
import '@/locales/scenarios';

describe('ProjectionComparisonChart', () => {
  it('draws the base and the scenario as two lines, in the fixed chart colours, with the scenario’s milestones', async () => {
    const data = comparison();
    const { container } = render(<ProjectionComparisonChart series={baseAndScenario(data, i18n.t)} currency="EUR" milestones={data.scenario.milestones} real />);

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

  it('draws one line and one legend entry per series, in order, without milestones when none are given', () => {
    const data = comparison();
    const series = ['Base', 'Retraite', 'Achat', 'Vente'].map((label, i) => ({ key: `s${i}`, label, points: data.base.points }));
    const { container } = render(<ProjectionComparisonChart series={series} currency="EUR" real={false} />);

    expect(screen.getAllByRole('listitem').map((li) => li.textContent)).toEqual(['Base', 'Retraite', 'Achat', 'Vente']);
    const chart = container.querySelector<HTMLElement>('[data-chart]')!;
    expect(chart.style.getPropertyValue('--color-s3')).toBe('var(--chart-4)');
    expect(container.querySelectorAll('.recharts-line-curve')).toHaveLength(4);
    expect(container.querySelectorAll('.recharts-reference-dot')).toHaveLength(0);
  });
});
