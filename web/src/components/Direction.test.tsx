import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import '@/locales/investments';
import { Direction } from './Direction';

const eur = (amount: string) => ({ amount, currency: 'EUR' });

describe('Direction', () => {
  it('a rise is good by default', () => {
    const { container } = render(<Direction amount={eur('360.00')} />);
    expect(container.querySelector('svg')).toHaveClass('text-status-good');
    expect(screen.getByText(/en hausse|en baisse/)).toBeInTheDocument();
  });

  it('with rising="bad" a rise is critical and a fall good', () => {
    const up = render(<Direction amount={eur('360.00')} rising="bad" />);
    expect(up.container.querySelector('svg')).toHaveClass('text-status-critical');
    expect(screen.getByText(/en hausse|en baisse/)).toBeInTheDocument();
    up.unmount();
    const down = render(<Direction amount={eur('-200.00')} rising="bad" />);
    expect(down.container.querySelector('svg')).toHaveClass('text-status-good');
    expect(screen.getByText(/en hausse|en baisse/)).toBeInTheDocument();
  });

  it('no change stays muted whatever the tone', () => {
    const { container } = render(<Direction amount={eur('0.00')} rising="bad" />);
    expect(container.querySelector('svg')).toHaveClass('text-muted-foreground');
  });
});
