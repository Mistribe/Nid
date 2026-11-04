import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { pick } from '@/test/controls';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { toasts } from '@/test/toasts';
import { formatInstant, formatInstantDay } from '../../admin/format';
import { summary } from '../../investments/testData';
import { account, CONNECTION, connection, rule, serveBanks } from './testData';

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
    expect(within(screen.getByRole('navigation', { name: 'Réglages' })).getByRole('link', { name: 'Banques' })).toHaveAttribute('aria-current', 'page');
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
    expect(await screen.findByRole('region', { name: 'Règles mémorisées' })).toHaveTextContent('Aucune règle mémorisée.');
  });

  it('says banking is off, and hides the « Banques » entry, when the household has it off', async () => {
    await renderApp('/settings/banks');

    expect(await screen.findByText(/La synchronisation bancaire n'est pas activée/)).toBeInTheDocument();
    expect(within(screen.getByRole('navigation', { name: 'Réglages' })).queryByRole('link', { name: 'Banques' })).toBeNull();
    expect(within(screen.getByRole('navigation', { name: 'Réglages' })).getByRole('link', { name: 'Apparence' })).toBeInTheDocument();
  });

  it('says the administrator has yet to configure it when it is on without its application', async () => {
    server.use(http.get('*/api/v1/banking/status', () => HttpResponse.json({ enabled: true, configured: false })));
    await renderApp('/settings/banks');

    expect(await screen.findByText(/pas encore configurée/)).toBeInTheDocument();
    expect(within(screen.getByRole('navigation', { name: 'Réglages' })).getByRole('link', { name: 'Banques' })).toBeInTheDocument();
  });

  it('without Enable Banking, still lists the imported accounts and offers « Importer un relevé », not « Relier une banque »', async () => {
    server.use(
      http.get('*/api/v1/banking/status', () => HttpResponse.json({ enabled: true, configured: false })),
      http.get('*/api/v1/banking/accounts', () => HttpResponse.json([account({ name: 'Relevé ·8901', ibanMasked: null, synced: false })])),
    );
    await renderApp('/settings/banks');

    expect(await screen.findByText(/pas encore configurée/)).toBeInTheDocument();
    expect(within(await screen.findByRole('region', { name: 'Comptes non synchronisés' })).getByRole('table')).toHaveTextContent('Relevé ·8901');
    expect(screen.getByRole('button', { name: 'Importer un relevé' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Relier une banque' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Synchroniser tout' })).toBeNull();
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

  it('lists and deletes a rule', async () => {
    serveBanks([connection()]);
    const loan = '00000000-0000-7000-8000-0000000000d2', property = '00000000-0000-7000-8000-0000000000d3';
    let rules = [
      rule(),
      rule({ id: 'r2', labelStem: 'PRLV CREDIT IMMO', module: 'realestate', itemId: loan, itemName: 'Prêt maison' }),
      rule({ id: 'r3', labelStem: 'TAXE FONCIERE', module: 'realestate', itemId: property, itemName: 'Maison', accountName: null, lastUsedAt: null }),
      rule({ id: 'r4', labelStem: 'CB SPOTIFY', itemName: null }),
      rule({ id: 'r5', labelStem: 'PRLV SYNDIC', module: 'realestate', itemId: '00000000-0000-7000-8000-0000000000d4', itemName: 'Studio' }),
    ];
    let deleted = '';
    server.use(
      http.get('*/api/v1/banking/rules', () => HttpResponse.json(rules)),
      // A real-estate target is a loan or a property: the two lists tell which.
      http.get('*/api/v1/realestate/loans', () => HttpResponse.json({ items: [{ id: loan }] })),
      http.get('*/api/v1/realestate/properties', () => HttpResponse.json({ items: [{ id: property }] })),
      http.delete('*/api/v1/banking/rules/:id', ({ params }) => {
        deleted = String(params.id);
        rules = rules.filter((r) => r.id !== deleted);
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const { user, container } = await renderApp('/settings/banks');

    const section = await screen.findByRole('region', { name: 'Règles mémorisées' });
    const table = within(await within(section).findByRole('table', { name: 'Règles mémorisées' }));
    const row = (stem: string) => table.getAllByRole('row').find((r) => r.textContent?.includes(stem))!;
    await waitFor(() => expect(within(row('PRLV CREDIT IMMO')).getByRole('link', { name: 'Prêt maison' })).toHaveAttribute('href', `/loans/${loan}`));
    expect(row('PRLV SEPA NETFLIX')).toHaveTextContent('Compte courant');
    expect(row('PRLV SEPA NETFLIX')).toHaveTextContent(formatInstantDay('2026-10-02T04:30:00+00:00'));
    expect(within(row('PRLV SEPA NETFLIX')).getByRole('link', { name: 'Netflix' })).toHaveAttribute('href', '/subscriptions/00000000-0000-7000-8000-0000000000d1');
    await waitFor(() => expect(within(row('TAXE FONCIERE')).getByRole('link', { name: 'Maison' })).toHaveAttribute('href', `/properties/${property}`));
    expect(row('TAXE FONCIERE')).toHaveTextContent('Jamais');
    // In neither list (an archived item): the name alone, never a guessed page.
    expect(row('PRLV SYNDIC')).toHaveTextContent('Studio');
    expect(within(row('PRLV SYNDIC')).queryByRole('link')).toBeNull();
    // The creator no longer reads the target: no name, no link.
    expect(row('CB SPOTIFY')).toHaveTextContent('Élément inaccessible');
    expect(within(row('CB SPOTIFY')).queryByRole('link')).toBeNull();
    await expectNoAxeViolations(container);

    await user.click(within(row('PRLV SEPA NETFLIX')).getByRole('button', { name: 'Actions sur la règle PRLV SEPA NETFLIX' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Supprimer' }));
    expect(await screen.findByText(/n'est plus reliée d'elle-même/)).toBeInTheDocument();
    expect(deleted).toBe('');
    await user.click(screen.getByRole('button', { name: 'Confirmer' }));

    await waitFor(() => expect(deleted).toBe('00000000-0000-7000-8000-0000000000c1'));
    await waitFor(() => expect(toasts()).toHaveTextContent('Règle supprimée.'));
    await waitFor(() => expect(within(section).queryByText('PRLV SEPA NETFLIX')).toBeNull());
  });
});
