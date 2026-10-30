import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { pick } from '@/test/controls';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { summary } from '../../investments/testData';
import { CARD, CHECKING, connection, JOINT, serveBanks } from './testData';

const envelopes = [
  summary({ id: JOINT, displayName: 'Compte joint', kind: 'checking' }),
  summary({ id: 'livret', displayName: 'Livret A', kind: 'savings' }),
  summary({ id: 'pea', displayName: 'PEA', kind: 'pea' }),
  summary({ id: 'read', displayName: 'Compte de Bob', kind: 'checking', canWrite: false }),
];

function patches() {
  const sent: { id: string; body: unknown }[] = [];
  server.use(http.patch('*/api/v1/banking/accounts/:id', async ({ params, request }) => {
    sent.push({ id: String(params.id), body: await request.json() });
    return HttpResponse.json({ id: params.id });
  }));
  return sent;
}

describe('the account dialog', () => {
  beforeAll(async () => { await import('./BanksPage'); }, 30_000);

  it('associates the account with one of the person’s writable checking or savings envelopes, its balances applied', async () => {
    serveBanks([connection()], envelopes);
    const sent = patches();
    const { user, container } = await renderApp('/settings/banks');

    await user.click((await screen.findAllByRole('button', { name: 'Régler Compte courant' }))[0]!);
    const dialog = await screen.findByRole('dialog', { name: 'Régler Compte courant' });
    await user.click(within(dialog).getByRole('combobox', { name: 'Enveloppe' }));
    const options = within(await screen.findByRole('listbox')).getAllByRole('option').map((o) => o.textContent);
    expect(options).toEqual(['Aucune', 'Compte joint', 'Livret A']);
    await user.keyboard('{Escape}');
    await pick(user, 'Enveloppe', 'Compte joint');
    expect(within(dialog).getByText('Sinon, chaque solde de fin de mois arrive dans À valider.')).toBeInTheDocument();
    await user.click(within(dialog).getByRole('switch', { name: 'Appliquer les soldes automatiquement' }));
    await expectNoAxeViolations(container);
    await user.click(within(dialog).getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(sent).toEqual([{ id: CHECKING, body: { envelopeId: JOINT, autoApplyBalance: true } }]));
    expect(await screen.findByText('Compte courant enregistré.')).toBeInTheDocument();
  });

  it('attaches a card to a checking account', async () => {
    serveBanks([connection()], envelopes);
    const sent = patches();
    const { user } = await renderApp('/settings/banks');

    await user.click((await screen.findAllByRole('button', { name: 'Régler Visa Premier' }))[0]!);
    const dialog = await screen.findByRole('dialog', { name: 'Régler Visa Premier' });
    expect(within(dialog).queryByRole('combobox', { name: 'Enveloppe' })).toBeNull();
    await pick(user, 'Carte rattachée à', 'Compte courant');
    await user.click(within(dialog).getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(sent).toEqual([{ id: CARD, body: { cardOfAccountId: CHECKING } }]));
  });

  it('stops following an account', async () => {
    serveBanks([connection()], envelopes);
    const sent = patches();
    const { user } = await renderApp('/settings/banks');

    await user.click((await screen.findAllByRole('button', { name: 'Régler Compte courant' }))[0]!);
    const dialog = await screen.findByRole('dialog', { name: 'Régler Compte courant' });
    await user.click(within(dialog).getByRole('switch', { name: 'Ne pas suivre ce compte' }));
    await user.click(within(dialog).getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(sent).toEqual([{ id: CHECKING, body: { ignored: true } }]));
  });

  it('clears the envelope of an account associated with one', async () => {
    serveBanks([connection({ accounts: [connection().accounts[0]!].map((a) => ({ ...a, envelopeId: JOINT })) })], envelopes);
    const sent = patches();
    const { user } = await renderApp('/settings/banks');

    await user.click((await screen.findAllByRole('button', { name: 'Régler Compte courant' }))[0]!);
    await pick(user, 'Enveloppe', 'Aucune');
    await user.click(screen.getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(sent).toEqual([{ id: CHECKING, body: { clearEnvelope: true } }]));
  });

  it('shows an envelope already taken on its field', async () => {
    serveBanks([connection()], envelopes);
    server.use(http.patch('*/api/v1/banking/accounts/:id', () =>
      HttpResponse.json({ status: 409, code: 'banking.envelope_taken', title: 'Taken.' }, { status: 409 })));
    const { user } = await renderApp('/settings/banks');

    await user.click((await screen.findAllByRole('button', { name: 'Régler Compte courant' }))[0]!);
    const dialog = await screen.findByRole('dialog', { name: 'Régler Compte courant' });
    await pick(user, 'Enveloppe', 'Compte joint');
    await user.click(within(dialog).getByRole('button', { name: 'Enregistrer' }));

    const field = await within(dialog).findByRole('combobox', { name: 'Enveloppe' });
    await waitFor(() => expect(field).toHaveAttribute('aria-invalid', 'true'));
    expect(field).toHaveAccessibleDescription('Un autre compte bancaire est déjà relié à cette enveloppe.');
  });

  it('shows a currency mismatch on the envelope field', async () => {
    serveBanks([connection()], envelopes);
    server.use(http.patch('*/api/v1/banking/accounts/:id', () => HttpResponse.json({
      status: 422, title: 'Invalid.', errors: [{ path: 'envelopeId', code: 'banking.currency_mismatch', message: 'Mismatch.' }],
    }, { status: 422 })));
    const { user } = await renderApp('/settings/banks');

    await user.click((await screen.findAllByRole('button', { name: 'Régler Compte courant' }))[0]!);
    const dialog = await screen.findByRole('dialog', { name: 'Régler Compte courant' });
    await pick(user, 'Enveloppe', 'Compte joint');
    await user.click(within(dialog).getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(within(dialog).getByRole('combobox', { name: 'Enveloppe' }))
      .toHaveAccessibleDescription('La devise du compte et celle de l\'enveloppe diffèrent.'));
  });
});
