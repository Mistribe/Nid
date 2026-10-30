import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { pick } from '@/test/controls';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { formatInstant } from '../../admin/format';
import { summary } from '../../investments/testData';
import { account, CONNECTION, connection, serveBanks } from './testData';

describe('Réglages › Banques', () => {
  beforeAll(async () => { await import('./BanksPage'); }, 30_000);

  it('lists each connection with its state, its expiry, its last sync and its accounts', async () => {
    serveBanks([connection(), connection({ id: 'c2', aspspName: 'Crédit Agricole', status: 'expired', lastSyncedAt: null, accounts: [] })]);
    const { container } = await renderApp('/settings/banks');

    const bnp = await screen.findByRole('region', { name: 'BNP Paribas' });
    expect(bnp).toHaveTextContent('Active');
    expect(bnp).toHaveTextContent('Expire le 27/04/2027');
    expect(bnp).toHaveTextContent(formatInstant('2026-10-28T04:30:00+00:00'));
    const rows = within(within(bnp).getByRole('table', { name: 'Comptes de BNP Paribas' })).getAllByRole('row').slice(1);
    expect(within(bnp).getByRole('list', { name: 'Comptes de BNP Paribas' })).toHaveTextContent('Visa Premier');
    expect(rows[0]).toHaveTextContent('Compte courant');
    expect(rows[0]).toHaveTextContent('FR76 •••• 1234');
    expect(rows[0]).toHaveTextContent('1 520,30 €');
    expect(rows[1]).toHaveTextContent('Visa Premier');
    expect(rows[1]).toHaveTextContent('Carte');
    expect(screen.getByRole('region', { name: 'Crédit Agricole' })).toHaveTextContent('Expirée');
    expect(screen.getByRole('link', { name: 'Banques' })).toHaveAttribute('aria-current', 'page');
    await expectNoAxeViolations(container);
  });

  it('labels a revoked connection and one in error', async () => {
    serveBanks([connection({ status: 'revoked' }), connection({ id: 'c2', aspspName: 'Crédit Agricole', status: 'error', accounts: [] })]);
    await renderApp('/settings/banks');

    expect(await screen.findByRole('region', { name: 'BNP Paribas' })).toHaveTextContent('Révoquée');
    expect(screen.getByRole('region', { name: 'Crédit Agricole' })).toHaveTextContent('En erreur');
  });

  it('invites to connect a bank when there is none', async () => {
    serveBanks([]);
    await renderApp('/settings/banks');

    expect(await screen.findByRole('heading', { name: 'Aucune banque reliée' })).toBeInTheDocument();
  });

  it('says banking is off, and hides the « Banques » entry, when the household has it off', async () => {
    await renderApp('/settings/banks');

    expect(await screen.findByText(/La synchronisation bancaire n'est pas activée/)).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Banques' })).toBeNull();
    expect(screen.getByRole('link', { name: 'Apparence' })).toBeInTheDocument();
  });

  it('says the administrator has yet to configure it when it is on without its application', async () => {
    server.use(http.get('*/api/v1/banking/status', () => HttpResponse.json({ enabled: true, configured: false })));
    await renderApp('/settings/banks');

    expect(await screen.findByText(/pas encore configurée/)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Banques' })).toBeInTheDocument();
  });

  it('connects a bank: picks it, starts the connection and leaves for the bank', async () => {
    serveBanks([]);
    const posted: unknown[] = [];
    server.use(http.post('*/api/v1/banking/connections', async ({ request }) => {
      posted.push(await request.json());
      return HttpResponse.json({ id: CONNECTION, redirectUrl: 'https://bank.example/authorize?x=1' }, { status: 201 });
    }));
    const { user, location } = await renderApp('/settings/banks');

    await user.click((await screen.findAllByRole('button', { name: 'Relier une banque' }))[0]!);
    const dialog = await screen.findByRole('dialog', { name: 'Relier une banque' });
    await pick(user, 'Banque', 'Crédit Agricole');
    await user.click(within(dialog).getByRole('button', { name: 'Continuer vers la banque' }));

    await waitFor(() => expect(location.assign).toHaveBeenCalledWith('https://bank.example/authorize?x=1'));
    expect(posted).toEqual([{ aspspName: 'Crédit Agricole' }]);
  });

  it('back from the bank, says so and opens its first unassociated account', async () => {
    serveBanks([connection()]);
    const { router } = await renderApp(`/settings/banks?connected=${CONNECTION}`);

    expect(await screen.findByRole('dialog', { name: 'Régler Compte courant' })).toBeInTheDocument();
    expect(screen.getByText('Banque reliée.')).toBeInTheDocument();
    await waitFor(() => expect(router.state.location.search).toEqual({}));
  });

  it('back from a refusal, says the bank refused', async () => {
    serveBanks([]);
    await renderApp('/settings/banks?error=banking.auth_refused');

    expect(await screen.findByRole('alert')).toHaveTextContent('La banque a refusé ou annulé l\'autorisation.');
  });

  it('synchronises a connection, and says when one is already running', async () => {
    serveBanks([connection()]);
    const sent: unknown[] = [];
    server.use(http.post('*/api/v1/banking/sync', async ({ request }) => {
      sent.push(await request.json());
      return sent.length === 1
        ? HttpResponse.json({ requested: true }, { status: 202 })
        : HttpResponse.json({ status: 409, code: 'banking.sync_running', title: 'Running.' }, { status: 409 });
    }));
    const { user } = await renderApp('/settings/banks');

    await user.click(await screen.findByRole('button', { name: 'Actions sur BNP Paribas' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Synchroniser' }));
    await waitFor(() => expect(screen.getByText('Synchronisation terminée.')).toBeInTheDocument());
    expect(sent).toEqual([{ connectionId: CONNECTION }]);

    await user.click(screen.getByRole('button', { name: 'Actions sur BNP Paribas' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Synchroniser' }));
    await waitFor(() => expect(screen.getByText('Une synchronisation est déjà en cours.')).toBeInTheDocument());
  });

  it('synchronises all the connections from the header', async () => {
    serveBanks([connection()]);
    const sent: unknown[] = [];
    server.use(http.post('*/api/v1/banking/sync', async ({ request }) => { sent.push(await request.json()); return HttpResponse.json({ runId: 'r1' }, { status: 202 }); }));
    const { user } = await renderApp('/settings/banks');

    await user.click(await screen.findByRole('button', { name: 'Synchroniser tout' }));

    await waitFor(() => expect(screen.getByText('Synchronisation terminée.')).toBeInTheDocument());
    expect(sent).toEqual([{}]);
  });

  it('lists the accounts no connection reads any more, and sets them', async () => {
    const left = account({ id: 'b9', name: 'Livret ancien', ibanMasked: null, kind: 'savings', envelopeId: 'e1', synced: false });
    serveBanks([], [summary({ id: 'e1', displayName: 'Livret A', kind: 'savings' })], [left]);
    const { user } = await renderApp('/settings/banks');

    const region = await screen.findByRole('region', { name: 'Comptes non synchronisés' });
    expect(region).toHaveTextContent('Livret ancien');
    expect(region).toHaveTextContent('Livret A');
    await user.click(within(region).getAllByRole('button', { name: 'Régler Livret ancien' })[0]!);

    expect(await screen.findByRole('dialog', { name: 'Régler Livret ancien' })).toBeInTheDocument();
  });

  it('lists an account under its connection only, even when that connection no longer reads it', async () => {
    const expired = connection({ status: 'expired', accounts: [account({ synced: false })] });
    serveBanks([expired]);
    await renderApp('/settings/banks');

    expect(await screen.findByRole('region', { name: 'BNP Paribas' })).toHaveTextContent('Compte courant');
    expect(screen.queryByRole('region', { name: 'Comptes non synchronisés' })).toBeNull();
  });

  it('renews a connection at the bank', async () => {
    serveBanks([connection({ status: 'expired' })]);
    server.use(http.post('*/api/v1/banking/connections/:id/renew', () => HttpResponse.json({ id: CONNECTION, redirectUrl: 'https://bank.example/renew' })));
    const { user, location } = await renderApp('/settings/banks');

    await user.click(await screen.findByRole('button', { name: 'Actions sur BNP Paribas' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Renouveler' }));

    await waitFor(() => expect(location.assign).toHaveBeenCalledWith('https://bank.example/renew'));
  });

  it('deletes a connection after a confirmation', async () => {
    serveBanks([connection()]);
    let deleted = '';
    server.use(http.delete('*/api/v1/banking/connections/:id', ({ params }) => { deleted = String(params.id); return new HttpResponse(null, { status: 204 }); }));
    const { user } = await renderApp('/settings/banks');

    await user.click(await screen.findByRole('button', { name: 'Actions sur BNP Paribas' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Supprimer' }));
    // Spec §8: the connection goes, its accounts and their values stay.
    expect(await screen.findByText(/ses comptes et leurs valeurs restent/)).toBeInTheDocument();
    expect(deleted).toBe('');
    await user.click(await screen.findByRole('button', { name: 'Confirmer' }));

    await waitFor(() => expect(deleted).toBe(CONNECTION));
  });
});
