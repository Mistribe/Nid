import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { ChartContainer, ChartLegendContent } from './chart';

describe('ChartLegendContent', () => {
  // An area stroked with the background (the 2 px separations) hands that colour to the legend: the swatch would vanish.
  it('draws a series’ swatch in its configured colour, not the stroke Recharts reports', () => {
    render(
      <ChartContainer config={{ net: { label: 'Valeur nette', color: 'rgb(42, 120, 214)' }, other: { label: 'Autre' } }}>
        <ChartLegendContent payload={[{ value: 'net', dataKey: 'net', color: 'rgb(255, 255, 255)' }, { value: 'other', dataKey: 'other', color: 'rgb(1, 2, 3)' }]} />
      </ChartContainer>,
    );

    expect(screen.getByText('Valeur nette').firstElementChild).toHaveStyle({ backgroundColor: 'rgb(42, 120, 214)' });
    expect(screen.getByText('Autre').firstElementChild).toHaveStyle({ backgroundColor: 'rgb(1, 2, 3)' }); // no colour configured
  });
});
