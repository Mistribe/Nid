import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { result } from '../taxTestData';
import { BracketScale } from './BracketScale';

const bracket = result().bracket!;

describe('BracketScale', () => {
  it('says the band and the room before the next', () => {
    render(<BracketScale bracket={bracket} marginalRate="11" />);
    const text = screen.getByText(/Vous êtes dans la tranche/).closest('p')?.textContent?.replace(/[\u00a0\u202f]/g, ' ');
    expect(text).toBe('Vous êtes dans la tranche à 11 %. Encore 7 846 € par part avant la tranche à 30 %.');
    expect(screen.getByText('Vous · 21 469 € par part')).toBeInTheDocument();
  });

  it("emphasises the household's band only", () => {
    const { container } = render(<BracketScale bracket={bracket} marginalRate="11" />);
    const current = container.querySelectorAll('[data-current="true"]');
    expect(current).toHaveLength(1);
    expect(current[0]).toHaveTextContent('11 %');
  });

  it('the top band has no next', () => {
    render(<BracketScale bracket={{ ...bracket, quotient: '200000.00', nextFrom: null, toNext: null }} marginalRate="45" />);
    expect(screen.getByText(/C’est la tranche la plus haute/)).toBeInTheDocument();
    expect(screen.queryByText(/Encore/)).toBeNull();
  });

  it('a zero income sits in the first band', () => {
    render(<BracketScale bracket={{ ...bracket, quotient: '0.00' }} marginalRate="0" />);
    expect(screen.getByText(/Vous êtes dans la tranche/)).toHaveTextContent('tranche à 0 %');
    expect(screen.getByText(/^Vous · /).closest('[style]')).toHaveStyle({ left: '0%' });
    expect(screen.getByText(/^Vous · /)).toHaveClass('translate-x-0');
  });

  it('keeps the marker label inside the scale at the far end', () => {
    render(<BracketScale bracket={{ ...bracket, quotient: bracket.displayEnd, nextFrom: null, toNext: null }} marginalRate="45" />);
    expect(screen.getByText(/^Vous · /)).toHaveClass('-translate-x-full');
  });
});
