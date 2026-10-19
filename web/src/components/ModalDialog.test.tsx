import { createRef } from 'react';
import { act, render, screen, waitFor } from '@testing-library/react';
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
    expect(dialog).toBeVisible();
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

  it('goes away when it unmounts', () => {
    const { unmount } = render(<ModalDialog title="Titre" onClose={() => {}}>{() => null}</ModalDialog>);
    expect(screen.getByRole('dialog')).toBeInTheDocument();
    unmount();
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('closes itself through dialogRef without calling onClose', async () => {
    const ref = createRef<{ close: () => void }>();
    const onClose = vi.fn();
    render(<ModalDialog title="Titre" onClose={onClose} dialogRef={ref}>{() => null}</ModalDialog>);
    act(() => ref.current?.close());
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(onClose).not.toHaveBeenCalled();
  });
});
