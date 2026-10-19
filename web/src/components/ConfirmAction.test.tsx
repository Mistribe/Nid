import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { ConfirmAction } from './ConfirmAction';

describe('ConfirmAction', () => {
  it('moves focus to Confirm when asked, and back to the trigger after Cancel', async () => {
    const user = userEvent.setup();
    render(<ConfirmAction label="Rendre admin" confirmation="Bob pourra gérer le foyer." onConfirm={vi.fn()} />);

    await user.click(screen.getByRole('button', { name: 'Rendre admin' }));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Confirmer' })).toHaveFocus());

    await user.click(screen.getByRole('button', { name: 'Annuler' }));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Rendre admin' })).toHaveFocus());
  });

  it('moves focus back to the trigger after Confirm', async () => {
    const user = userEvent.setup();
    render(<ConfirmAction label="Rendre admin" confirmation="Bob pourra gérer le foyer." onConfirm={vi.fn()} />);

    await user.click(screen.getByRole('button', { name: 'Rendre admin' }));
    await user.click(await screen.findByRole('button', { name: 'Confirmer' }));

    await waitFor(() => expect(screen.getByRole('button', { name: 'Rendre admin' })).toHaveFocus());
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

    expect(await screen.findByRole('button', { name: 'Confirmer' })).toBeDisabled();
  });
});
