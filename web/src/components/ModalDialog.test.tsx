import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { ModalDialog } from './ModalDialog';

describe('ModalDialog', () => {
  it('is a modal dialog named by its title, its content labelled by the same title', async () => {
    render(
      <ModalDialog title="Clôturer l’enveloppe" onClose={() => {}}>
        {(titleId) => <form aria-labelledby={titleId}><button type="submit">OK</button></form>}
      </ModalDialog>,
    );
    const dialog = screen.getByRole('dialog', { name: 'Clôturer l’enveloppe' });
    expect(dialog).toHaveAttribute('open');
    expect(screen.getByRole('form', { name: 'Clôturer l’enveloppe' })).toBeInTheDocument();
    await expectNoAxeViolations(document.body);
  });

  it('Escape calls onClose', async () => {
    const onClose = vi.fn();
    const user = userEvent.setup();
    render(
      <ModalDialog title="Titre" onClose={onClose}>
        {() => <button type="button">Dedans</button>}
      </ModalDialog>,
    );
    screen.getByRole('button', { name: 'Dedans' }).focus();
    await user.keyboard('{Escape}');
    expect(onClose).toHaveBeenCalled();
  });

  it('closes the native dialog when it unmounts', () => {
    const { unmount } = render(<ModalDialog title="Titre" onClose={() => {}}>{() => null}</ModalDialog>);
    const dialog = screen.getByRole<HTMLDialogElement>('dialog');
    unmount();
    expect(dialog.open).toBe(false);
  });
});
