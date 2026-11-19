import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import '@/locales/tax';
import { result } from '../taxTestData';
import { IncomeSplit } from './IncomeSplit';

describe('IncomeSplit', () => {
  it('is a card with the bar and its amounts', () => {
    render(<IncomeSplit result={result()} />);
    const region = screen.getByRole('region', { name: 'Où vont vos revenus' });
    expect(region).toHaveTextContent('sur 69 000,00 € de revenus nets avant impôt');
    expect(screen.getByRole('img', { name: /^Reste .+, Impôt sur le revenu 247,00/ })).toBeInTheDocument();
  });

  it('draws nothing with a single part', () => {
    const { container } = render(<IncomeSplit result={result({ incomeTax: '0.00', totalTax: '0.00' })} />);
    expect(container).toBeEmptyDOMElement();
  });
});
