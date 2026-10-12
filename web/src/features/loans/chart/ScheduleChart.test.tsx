import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { schedule } from '../testData';
import { ScheduleChart } from './ScheduleChart';

describe('ScheduleChart', () => {
  it('is a named figure, described by the summary the page writes', () => {
    render(<ScheduleChart years={schedule().years} currency="EUR" summaryId="summary-id" />);

    expect(screen.getByRole('figure', { name: 'Graphique de l’échéancier par année' })).toHaveAttribute('aria-describedby', 'summary-id');
  });
});
