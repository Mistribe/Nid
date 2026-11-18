import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
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

describe('YearStepper', () => {
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
    expect(screen.getByRole('button', { name: 'Année précédente' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Année suivante' })).toBeEnabled();
  });

  it('disables the next arrow on the last year', () => {
    renderStepper('2026');
    expect(screen.getByRole('button', { name: 'Année suivante' })).toBeDisabled();
  });

  it('steps from a year not in the list', async () => {
    const { onChange, user } = renderStepper('2022');
    await user.click(screen.getByRole('button', { name: 'Année précédente' }));
    await user.click(screen.getByRole('button', { name: 'Année suivante' }));
    expect(onChange.mock.calls).toEqual([['2019'], ['2024']]);
  });

  it('disables both arrows with one year', () => {
    renderStepper('2026', [{ value: '2026', hint: 'Estimation' }]);
    expect(screen.getByRole('button', { name: 'Année précédente' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Année suivante' })).toBeDisabled();
  });

  it('opens a grid of every year with its hint and closes on a choice', async () => {
    const { onChange, user } = renderStepper('2025');
    await user.click(screen.getByRole('button', { name: '2025, changer d’année' }));
    const grid = await screen.findByRole('dialog');
    expect(within(grid).getAllByRole('button').map((b) => b.textContent)).toEqual(['2019Avis', '2024Avis', '2025Avis + estimation', '2026Estimation']);
    expect(within(grid).getByRole('button', { name: /^2025/ })).toHaveAttribute('aria-pressed', 'true');
    expect(within(grid).getByRole('button', { name: /^2024/ })).toHaveAttribute('aria-pressed', 'false');

    await user.click(within(grid).getByRole('button', { name: /^2024/ }));
    expect(onChange).toHaveBeenCalledWith('2024');
    expect(screen.queryByRole('dialog')).toBeNull();
  });
});
