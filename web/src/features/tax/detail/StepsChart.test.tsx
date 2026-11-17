import { render, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import type { TaxStep } from '@/api/tax';
import { result } from '../taxTestData';
import { StepsChart, StepsTable } from './StepsChart';

const steps = result().steps ?? [];
const group = (g: string) => steps.filter((s) => s.group === g);
const ticks = (c: HTMLElement) => [...c.querySelectorAll('.recharts-yAxis-tick-labels .recharts-cartesian-axis-tick-value')].map((n) => n.textContent);

describe('StepsChart', () => {
  it('draws one floating bar per step, coloured by what it does', async () => {
    const { container } = render(<StepsChart steps={group('income')} title="Votre revenu imposable" />);
    await waitFor(() => expect(container.querySelectorAll('.recharts-bar-rectangle path')).toHaveLength(3));
    const bars = [...container.querySelectorAll('.recharts-bar-rectangle path')];
    expect(bars.map((b) => b.getAttribute('fill'))).toEqual(['var(--chart-2)', 'var(--status-good)', 'var(--chart-1)']);
    expect(ticks(container)).toEqual(['Salaires', 'Abattement de 10 %', 'Revenu imposable']);
  });

  it('labels the totals only', async () => {
    const { container } = render(<StepsChart steps={group('tax')} title="Votre impôt" />);
    const texts = () => [...container.querySelectorAll('.recharts-label-list text')].map((n) => n.textContent?.replace(/\s/g, ' ')).filter(Boolean);
    await waitFor(() => expect(texts()).toEqual(['247,00 €', '247,00 €']));
  });

  it('skips a bar under one euro, keeps the others', () => {
    const { container } = render(<StepsChart steps={group('tax')} title="Votre impôt" />);
    expect(ticks(container)).not.toContain('Arrondi');
    expect(ticks(container)).toContain('Décote');
  });

  it('names Ajoute, Retire and Total once', () => {
    const { rerender } = render(<StepsChart steps={group('income')} title="t" legend />);
    for (const w of ['Ajoute', 'Retire', 'Total']) expect(screen.getAllByText(w)).toHaveLength(1);
    rerender(<StepsChart steps={group('income')} title="t" />);
    for (const w of ['Ajoute', 'Retire', 'Total']) expect(screen.queryByText(w)).toBeNull();
  });

  it('a step the server added later reads as Autre, never its code', () => {
    const later: TaxStep = { code: 'new_thing', group: 'income', amount: '10.00', from: '0.00', to: '10.00' };
    const { container } = render(<StepsChart steps={[later]} title="t" />);
    expect(ticks(container)).toEqual(['Autre']);
  });
});

describe('StepsTable', () => {
  it('says every step without a sign', () => {
    render(<StepsTable steps={steps} />);
    expect(screen.getByRole('row', { name: /Abattement de 10 %/ }).textContent).toMatch(/Retire.*7\s158,00\s€/);
    expect(screen.getByRole('row', { name: /^Revenu imposable/ }).textContent).toMatch(/Total.*64\s407,00\s€/);
    expect(screen.getByRole('row', { name: /Arrondi/ })).toBeTruthy();
    expect(screen.queryByText(/^-|−\d/)).toBeNull();
  });

  it('reads a negative income tax in your favour', () => {
    const negative = steps.map((s) => (s.code === 'income_tax' ? { ...s, to: '-500.00' } : s));
    render(<StepsTable steps={negative} />);
    expect(within(screen.getByRole('row', { name: /Impôt sur le revenu/ })).getByText(/500,00 € en votre faveur/)).toBeTruthy();
  });
});
