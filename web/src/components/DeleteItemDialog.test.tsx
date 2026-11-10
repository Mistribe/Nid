import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import type { components } from '@/api/schema';
import { DeleteItemDialog } from './DeleteItemDialog';

const preview = (over: Partial<components['schemas']['ItemDeletionPreview']> = {}): components['schemas']['ItemDeletionPreview'] =>
  ({ name: 'Épargne vacances', version: 7, imported: false, removes: [], blockers: [], ...over });

const show = (p = preview(), onConfirm = vi.fn()) =>
  render(<DeleteItemDialog preview={p} previewError={null} closeHint="Clore l’objectif" pending={false} error={null} onConfirm={onConfirm} onClose={vi.fn()} />);

describe('DeleteItemDialog', () => {
  it('button stays disabled until the exact name is typed', async () => {
    const user = userEvent.setup();
    show();
    const button = screen.getByRole('button', { name: 'Supprimer définitivement' });
    const field = screen.getByLabelText('Tapez Épargne vacances pour confirmer');
    await user.type(field, 'Épargne vacance');
    expect(button).toBeDisabled();
    await user.clear(field);
    await user.type(field, 'épargne vacances');
    expect(button).toBeDisabled();
    await user.clear(field);
    await user.type(field, 'Épargne vacances');
    expect(button).toBeEnabled();
  });

  it('Enter in the field does not confirm', async () => {
    const user = userEvent.setup();
    const onConfirm = vi.fn();
    show(preview(), onConfirm);
    await user.type(screen.getByLabelText('Tapez Épargne vacances pour confirmer'), 'Épargne vacances{Enter}');
    expect(onConfirm).not.toHaveBeenCalled();
  });

  it('confirm sends the preview version', async () => {
    const user = userEvent.setup();
    const onConfirm = vi.fn();
    show(preview(), onConfirm);
    await user.type(screen.getByLabelText('Tapez Épargne vacances pour confirmer'), 'Épargne vacances');
    await user.click(screen.getByRole('button', { name: 'Supprimer définitivement' }));
    expect(onConfirm).toHaveBeenCalledWith(7);
  });

  it('blockers replace the delete button', () => {
    show(preview({ blockers: [{ key: 'funding', count: 1, items: [{ id: 'x', name: 'Appartement de Léa' }] }] }));
    expect(screen.getByText(/Appartement de Léa/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Supprimer définitivement' })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Fermer' })).toBeInTheDocument();
  });

  it('lists what will be removed', () => {
    show(preview({ removes: [{ key: 'flows', count: 3 }] }));
    expect(screen.getByText('3 flux')).toBeInTheDocument();
  });

  it('focus starts in the field', async () => {
    show();
    await waitFor(() => expect(screen.getByLabelText('Tapez Épargne vacances pour confirmer')).toHaveFocus());
  });

  it('asks for the name again when the preview version changes', async () => {
    const user = userEvent.setup();
    const props = { previewError: null, closeHint: 'Clore l’objectif', pending: false, error: null, onConfirm: vi.fn(), onClose: vi.fn() };
    const { rerender } = render(<DeleteItemDialog preview={preview()} {...props} />);
    const field = screen.getByLabelText('Tapez Épargne vacances pour confirmer');
    await user.type(field, 'Épargne vacances');
    rerender(<DeleteItemDialog preview={preview({ version: 8 })} {...props} />);
    expect(screen.getByLabelText('Tapez Épargne vacances pour confirmer')).toHaveValue('');
    expect(screen.getByRole('button', { name: 'Supprimer définitivement' })).toBeDisabled();
  });

  it('can be closed while loading', async () => {
    const user = userEvent.setup();
    const onClose = vi.fn();
    render(<DeleteItemDialog preview={undefined} previewError={null} closeHint="x" pending={false} error={null} onConfirm={vi.fn()} onClose={onClose} />);
    await user.click(screen.getByRole('button', { name: 'Fermer' }));
    expect(onClose).toHaveBeenCalled();
  });
});
