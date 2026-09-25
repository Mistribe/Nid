import { render, screen, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import '@/app/i18n';
import { expectNoAxeViolations } from '@/test/axe';
import { CoverageBand } from './CoverageBand';
import { detail } from './testData';

describe('CoverageBand', () => {
  const coverage = detail().coverage;

  it('draws one segment per period, each source with its own pattern and a title', () => {
    const { container } = render(<CoverageBand coverage={coverage} />);

    const segments = container.querySelectorAll('rect[data-source]');
    expect(segments).toHaveLength(3);
    expect(segments[0]!.getAttribute('fill')).toBe('url(#coverage-paid)');
    expect(segments[1]!.getAttribute('fill')).toBe('url(#coverage-gift)');
    expect(segments[1]!.querySelector('title')?.textContent).toMatch(/Offert.*28 octobre 2025/);
  });

  it('marks a projected period with a dashed outline', () => {
    const { container } = render(<CoverageBand coverage={coverage} />);

    expect(container.querySelectorAll('rect[data-source]')[2]!.getAttribute('stroke-dasharray')).toBe('4 2');
  });

  it('names each source once in the legend and gives the same periods as a table', async () => {
    const { container } = render(<CoverageBand coverage={coverage} />);

    const legend = screen.getByRole('list', { name: 'Légende' });
    expect(within(legend).getAllByRole('listitem').map((i) => i.textContent)).toEqual(['Payé', 'Offert']);
    const table = screen.getByRole('table', { name: 'Périodes couvertes' });
    const rows = within(table).getAllByRole('row').slice(1);
    expect(rows).toHaveLength(3);
    expect(rows[2]).toHaveTextContent('prévu');
    await expectNoAxeViolations(container);
  });

  it('says so when nothing is covered', () => {
    render(<CoverageBand coverage={[]} />);
    expect(screen.getByText('Aucune période couverte.')).toBeInTheDocument();
  });
});
