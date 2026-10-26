import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeAll, describe, expect, it, vi } from 'vitest';
import { ProblemError } from '@/api/errors';
import { expectNoAxeViolations } from '@/test/axe';

beforeAll(async () => { await import('./DiscordWebhookControl'); }, 30_000);

async function setup(props: Partial<Parameters<typeof import('./DiscordWebhookControl').DiscordWebhookControl>[0]> = {}) {
  const { DiscordWebhookControl } = await import('./DiscordWebhookControl');
  const onSave = vi.fn().mockResolvedValue(undefined);
  const onRemove = vi.fn().mockResolvedValue(undefined);
  const view = render(<DiscordWebhookControl webhook={null} onSave={onSave} onRemove={onRemove} onTest={() => {}} testText={null} pending={false} removeConfirmation="Vous ne recevrez plus rien sur Discord." {...props} />);
  return { ...view, onSave, onRemove, user: userEvent.setup() };
}

describe('DiscordWebhookControl', () => {
  it('saves the typed URL, also with Enter', async () => {
    const { user, onSave } = await setup();
    await user.type(screen.getByLabelText('Webhook Discord'), 'https://discord.com/api/webhooks/1/abc{Enter}');
    expect(onSave).toHaveBeenCalledWith('https://discord.com/api/webhooks/1/abc');
  });

  it('shows the field error of a refused URL', async () => {
    const { user, onSave } = await setup();
    onSave.mockRejectedValue(new ProblemError({ status: 422, errors: [{ path: 'url', code: 'notifications.invalid_webhook', message: 'raw' }] }));
    await user.type(screen.getByLabelText('Webhook Discord'), 'nope');
    await user.click(screen.getByRole('button', { name: 'Enregistrer le webhook' }));
    expect(await screen.findByText('Ce n’est pas une adresse de webhook Discord.')).toBeInTheDocument();
    expect(screen.getByLabelText('Webhook Discord')).toHaveValue('nope');
  });

  it('shows the saved hint, replaces and removes', async () => {
    const { user, onRemove, container } = await setup({ webhook: { hint: 'wxyz', status: 'ok' } });
    expect(screen.getByText(/Webhook enregistré : …wxyz/)).toBeInTheDocument();
    await expectNoAxeViolations(container);
    await user.click(screen.getByRole('button', { name: 'Remplacer' }));
    expect(screen.getByLabelText('Webhook Discord')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Annuler' }));
    await user.click(screen.getByRole('button', { name: 'Retirer' }));
    expect(onRemove).not.toHaveBeenCalled();
    expect(await screen.findByText('Retirer le webhook Discord ?')).toBeInTheDocument();
    expect(screen.getByText('Vous ne recevrez plus rien sur Discord.')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Confirmer' }));
    expect(onRemove).toHaveBeenCalled();
  });

  it('says a broken webhook must be replaced', async () => {
    await setup({ webhook: { hint: 'wxyz', status: 'broken' } });
    expect(screen.getByText('Discord ne répond plus à ce webhook. Remplacez-le pour recevoir à nouveau les notifications.')).toBeInTheDocument();
  });
  it('does not offer a test while the webhook is broken', async () => {
    await setup({ webhook: { hint: 'wxyz', status: 'broken' } });
    expect(screen.getByRole('button', { name: 'Envoyer un test' })).toBeDisabled();
  });

  it('moves focus with each swap: field after Remplacer, Remplacer after Annuler or a save, field after Retirer', async () => {
    const { user, onSave, rerender, onRemove } = await setup({ webhook: { hint: 'wxyz', status: 'ok' } });
    const { DiscordWebhookControl } = await import('./DiscordWebhookControl');
    await user.click(screen.getByRole('button', { name: 'Remplacer' }));
    expect(screen.getByLabelText('Webhook Discord')).toHaveFocus();
    await user.type(screen.getByLabelText('Webhook Discord'), 'typed');
    await user.click(screen.getByRole('button', { name: 'Annuler' }));
    expect(screen.getByRole('button', { name: 'Remplacer' })).toHaveFocus();

    await user.click(screen.getByRole('button', { name: 'Remplacer' }));
    expect(screen.getByLabelText('Webhook Discord')).toHaveValue('');
    await user.type(screen.getByLabelText('Webhook Discord'), 'https://discord.com/api/webhooks/1/abc');
    await user.click(screen.getByRole('button', { name: 'Enregistrer le webhook' }));
    await vi.waitFor(() => expect(screen.getByRole('button', { name: 'Remplacer' })).toHaveFocus());
    expect(onSave).toHaveBeenCalledTimes(1);

    onRemove.mockImplementation(() => {
      rerender(<DiscordWebhookControl webhook={null} onSave={onSave} onRemove={onRemove} onTest={() => {}} testText={null} pending={false} removeConfirmation="Vous ne recevrez plus rien sur Discord." />);
      return Promise.resolve();
    });
    await user.click(screen.getByRole('button', { name: 'Retirer' }));
    await user.click(await screen.findByRole('button', { name: 'Confirmer' }));
    await vi.waitFor(() => expect(screen.getByLabelText('Webhook Discord')).toHaveFocus());
  });

  it('ignores a second submit while the save runs', async () => {
    let done: () => void = () => {};
    const { user, onSave } = await setup();
    onSave.mockImplementation(() => new Promise<void>((resolve) => { done = resolve; }));
    await user.type(screen.getByLabelText('Webhook Discord'), 'https://discord.com/api/webhooks/1/abc{Enter}{Enter}');
    expect(onSave).toHaveBeenCalledTimes(1);
    expect(screen.getByRole('button', { name: 'Enregistrer le webhook' })).toBeDisabled();
    done();
  });
});
