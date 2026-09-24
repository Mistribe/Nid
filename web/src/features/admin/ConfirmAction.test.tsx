import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { ConfirmAction } from './ConfirmAction';

describe('ConfirmAction', () => {
  it('moves focus to Confirm when asked, and back to the trigger after Cancel', async () => {
    const user = userEvent.setup();
    render(<ConfirmAction label="Rendre admin" confirmation="Bob pourra gérer le foyer." onConfirm={vi.fn()} />);

    await user.click(screen.getByRole('button', { name: 'Rendre admin' }));
    expect(document.activeElement).toBe(screen.getByRole('button', { name: 'Confirmer' }));

    await user.click(screen.getByRole('button', { name: 'Annuler' }));
    expect(document.activeElement).toBe(screen.getByRole('button', { name: 'Rendre admin' }));
  });

  it('moves focus back to the trigger after Confirm', async () => {
    const user = userEvent.setup();
    render(<ConfirmAction label="Rendre admin" confirmation="Bob pourra gérer le foyer." onConfirm={vi.fn()} />);

    await user.click(screen.getByRole('button', { name: 'Rendre admin' }));
    await user.click(screen.getByRole('button', { name: 'Confirmer' }));

    expect(document.activeElement).toBe(screen.getByRole('button', { name: 'Rendre admin' }));
  });

  it('disables the trigger while pending', () => {
    render(<ConfirmAction label="Rendre admin" confirmation="Bob pourra gérer le foyer." onConfirm={vi.fn()} pending />);

    expect(screen.getByRole('button', { name: 'Rendre admin' })).toBeDisabled();
  });

  it('disables the Confirm button while pending', async () => {
    const user = userEvent.setup();
    const { rerender } = render(
      <ConfirmAction label="Rendre admin" confirmation="Bob pourra gérer le foyer." onConfirm={vi.fn()} />,
    );
    await user.click(screen.getByRole('button', { name: 'Rendre admin' }));

    rerender(<ConfirmAction label="Rendre admin" confirmation="Bob pourra gérer le foyer." onConfirm={vi.fn()} pending />);

    expect(screen.getByRole('button', { name: 'Confirmer' })).toBeDisabled();
  });
});
