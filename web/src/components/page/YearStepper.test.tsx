import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { YearStepper } from './YearStepper';

const YEARS = [
  { value: '2019', hint: 'Avis' }, { value: '2024', hint: 'Avis' }, { value: '2025', hint: 'Avis + estimation' }, { value: '2026', hint: 'Estimation' },
];

function renderStepper(value: string, years = YEARS) {
  const onChange = vi.fn();
  render(<YearStepper label="Année des revenus" value={value} years={years} onChange={onChange} />);
  return { onChange, user: userEvent.setup() };
}

function Controlled({ initial }: { initial: string }) {
  const [value, setValue] = useState(initial);
  return <YearStepper label="Année des revenus" value={value} years={YEARS} onChange={setValue} />;
}

describe('YearStepper', () => {
  it('keeps the keyboard focus on an arrow that reaches the end', async () => {
    render(<Controlled initial="2024" />);
    const user = userEvent.setup();
    const previous = screen.getByRole('button', { name: 'Année précédente' });
    previous.focus();
    await user.keyboard('{Enter}');
    expect(screen.getByRole('button', { name: '2019, changer d’année' })).toBeInTheDocument();
    expect(previous).toHaveFocus();
    expect(previous).toHaveAttribute('aria-disabled', 'true');
    await user.keyboard('{Enter}');
    expect(screen.getByRole('button', { name: '2019, changer d’année' })).toBeInTheDocument();
  });

  it('reads the year and its hint apart', async () => {
    const { user } = renderStepper('2025');
    await user.click(screen.getByRole('button', { name: '2025, changer d’année' }));
    expect(within(await screen.findByRole('dialog')).getByRole('button', { name: '2019 Avis' })).toBeInTheDocument();
  });

  it('names a group with the label', () => {
    renderStepper('2025');
    expect(screen.getByRole('group', { name: 'Année des revenus' })).toBeInTheDocument();
  });

  it('steps to the neighbouring years', async () => {
    const { onChange, user } = renderStepper('2025');
    await user.click(screen.getByRole('button', { name: 'Année précédente' }));
    await user.click(screen.getByRole('button', { name: 'Année suivante' }));
    expect(onChange.mock.calls).toEqual([['2024'], ['2026']]);
  });

  it('disables the arrow at either end', () => {
    renderStepper('2019');
    expect(screen.getByRole('button', { name: 'Année précédente' })).toHaveAttribute('aria-disabled', 'true');
    expect(screen.getByRole('button', { name: 'Année suivante' })).not.toHaveAttribute('aria-disabled');
  });

  it('disables the next arrow on the last year', () => {
    renderStepper('2026');
    expect(screen.getByRole('button', { name: 'Année suivante' })).toHaveAttribute('aria-disabled', 'true');
  });

  it('steps from a year not in the list', async () => {
    const { onChange, user } = renderStepper('2022');
    await user.click(screen.getByRole('button', { name: 'Année précédente' }));
    await user.click(screen.getByRole('button', { name: 'Année suivante' }));
    expect(onChange.mock.calls).toEqual([['2019'], ['2024']]);
  });

  it('disables both arrows with one year', () => {
    renderStepper('2026', [{ value: '2026', hint: 'Estimation' }]);
    expect(screen.getByRole('button', { name: 'Année précédente' })).toHaveAttribute('aria-disabled', 'true');
    expect(screen.getByRole('button', { name: 'Année suivante' })).toHaveAttribute('aria-disabled', 'true');
  });

  it('opens a grid of every year with its hint and closes on a choice', async () => {
    const { onChange, user } = renderStepper('2025');
    await user.click(screen.getByRole('button', { name: '2025, changer d’année' }));
    const grid = await screen.findByRole('dialog');
    expect(within(grid).getAllByRole('button').map((b) => b.textContent)).toEqual(['2019 Avis', '2024 Avis', '2025 Avis + estimation', '2026 Estimation']);
    expect(within(grid).getByRole('button', { name: /^2025/ })).toHaveAttribute('aria-pressed', 'true');
    expect(within(grid).getByRole('button', { name: /^2024/ })).toHaveAttribute('aria-pressed', 'false');

    await user.click(within(grid).getByRole('button', { name: /^2024/ }));
    expect(onChange).toHaveBeenCalledWith('2024');
    expect(screen.queryByRole('dialog')).toBeNull();
  });
});
