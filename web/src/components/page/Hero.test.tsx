import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import '@/locales/investments';
import { Hero, HeroBody, HeroChange, HeroCurve, HeroUnknown } from './Hero';

const eur = (amount: string) => ({ amount, currency: 'EUR' });

describe('Hero', () => {
  it('shows the label, the figure in the hero style and the change', () => {
    render(<Hero label="Valeur des placements" figure="35 000,00 €" note="au 31 août 2025"
      change={<HeroChange amount={eur('1840.00')} pct="+5,6 %">depuis le 1er janvier</HeroChange>} />);

    const hero = screen.getByRole('region', { name: 'Valeur des placements' });
    expect(screen.getByText('Valeur des placements')).toHaveClass('text-label', 'text-muted-foreground');
    expect(screen.getByText('35 000,00 €')).toHaveClass('text-hero', 'tabular-nums');
    expect(hero).toHaveClass('material', 'rounded-[22px]', 'p-5');
    expect(hero).toHaveTextContent('35 000,00 €+1 840,00 €+5,6 %en haussedepuis le 1er janvierau 31 août 2025');
    expect(screen.getByText('au 31 août 2025')).toHaveClass('text-sm', 'text-muted-foreground');
  });

  it('draws the curve only with two values or more', () => {
    const { container, rerender } = render(<Hero label="Valeur" figure="1 €" values={[1]} />);
    expect(container.querySelector('svg')).toBeNull();

    rerender(<Hero label="Valeur" figure="1 €" values={[1, 2]} />);
    const svg = container.querySelector('svg')!;
    expect(svg).not.toBeNull();
    expect(svg.querySelector('path[stroke]')).toHaveAttribute('stroke', 'var(--primary)');
    expect(svg.parentElement).toHaveClass('hidden', 'w-56', 'sm:block');
  });

  it('leaves no empty curve box when there is no curve to draw', () => {
    const { container, rerender } = render(<Hero label="Valeur" figure="1 €" values={[1]} />);
    expect(container.querySelector('.w-56')).toBeNull();

    // The dashboard's sparkline hands its own values to the same box.
    rerender(<HeroBody label="Valeur" figure="1 €" curve={<HeroCurve values={[]} />} />);
    expect(container.querySelector('.w-56')).toBeNull();
  });

  it('sets an unknown figure back, small and muted', () => {
    render(<Hero label="Valeur" figure={<HeroUnknown>Inconnu</HeroUnknown>} />);
    expect(screen.getByText('Inconnu')).toHaveClass('text-sm', 'font-normal', 'tracking-normal', 'text-muted-foreground');
  });

  it('hides the curve from assistive technology', () => {
    const { container } = render(<Hero label="Valeur" figure="1 €" values={[1, 2, 3]} />);
    expect(container.querySelector('svg')).toHaveAttribute('aria-hidden', 'true');
  });
});
