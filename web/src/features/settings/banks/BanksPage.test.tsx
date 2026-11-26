import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { pick } from '@/test/controls';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { toasts } from '@/test/toasts';
import { formatInstant, formatInstantDay, formatInstantTime } from '@/lib/format';
import { summary } from '../../investments/testData';
import { account, CARD, CHECKING, CONNECTION, connection, noReads, rule, serveBanks } from './testData';

describe('Réglages › Banques', () => {
  beforeAll(async () => { await import('./BanksPage'); await import('@/features/spending/SpendingPage'); }, 30_000);

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
    expect(rows[0]).toHaveTextContent('•••• 1234');
    expect(rows[0]).toHaveTextContent('1 520,30 €');
    expect(rows[1]).toHaveTextContent('Visa Premier');
    expect(rows[1]).toHaveTextContent('Carte');
    expect(screen.getByRole('region', { name: 'Crédit Agricole' })).toHaveTextContent('Expirée');
    expect(within(screen.getByRole('navigation', { name: 'Réglages' })).getByRole('link', { name: 'Banques' })).toHaveAttribute('aria-current', 'page');
    await expectNoAxeViolations(container);
  });

  it('counts the reads without the person as Nid’s estimate, and warns before « Synchroniser » after a refusal (#365)', async () => {
    const limitedAt = '2026-10-28T13:02:00+00:00';
    const freesAt = '2026-10-29T04:30:00+00:00';
    const refused = { ...noReads, limitedAt, warn: true };
    serveBanks([connection({
      reads: { ...noReads, unattended: 2, attended: 1, freesAt, limitedAt, warn: true },
      accounts: [account({ reads: refused }), account({ id: CARD, name: 'Visa Premier', ibanMasked: null, kind: 'card' })],
    }), connection({ id: 'c2', aspspName: 'Crédit Agricole', accounts: [] })]);
    const { user } = await renderApp('/settings/banks');

    const bnp = await screen.findByRole('region', { name: 'BNP Paribas' });
    expect(bnp).toHaveTextContent(`2 / 4 lectures sans vous sur 24 h · la plus ancienne ne compte plus à ${formatInstantTime(freesAt)} · 1 lecture avec vous`);
    await user.hover(within(bnp).getByRole('button', { name: 'À propos : Lectures de la banque' }));
    expect(await screen.findByRole('tooltip')).toHaveTextContent('Estimation de Nid, pas le quota de la banque.');
    expect(within(bnp).getByRole('status')).toHaveTextContent(`La banque a refusé une lecture le ${formatInstant(limitedAt)} ; prochaine tentative cette nuit. Vous pouvez quand même synchroniser.`);
    const [, first] = within(within(bnp).getByRole('table', { name: 'Comptes de BNP Paribas' })).getAllByRole('row');
    expect(first).toHaveTextContent(`limite de la banque le ${formatInstant(limitedAt)}`);
    // Nothing refused, nothing read: no warning.
    expect(within(screen.getByRole('region', { name: 'Crédit Agricole' })).queryByRole('status')).toBeNull();
    // The warning never blocks: « Synchroniser » stays in the menu.
    await user.click(within(bnp).getByRole('button', { name: 'Actions sur BNP Paribas' }));
    expect(await screen.findByRole('menuitem', { name: 'Synchroniser' })).toBeEnabled();
  });

  it('lets long cells wrap, the date under the balance, and « Régler » at the end, so the table never scrolls sideways (#364)', async () => {
    serveBanks([connection()]);
    await renderApp('/settings/banks');

    const table = within(await screen.findByRole('region', { name: 'BNP Paribas' })).getByRole('table', { name: 'Comptes de BNP Paribas' });
    const [, first] = within(table).getAllByRole('row');
    const cells = within(first!).getAllByRole('cell');
    // Compte, Type, Enveloppe, À qui, Dernier solde wrap; Actions sits at the end.
    for (const cell of cells.slice(0, 5)) expect(cell).toHaveClass('whitespace-normal');
    expect(cells[5]).toHaveClass('text-right');
    expect(cells[4]).toHaveTextContent('1 520,30 €au 30 septembre 2026');
    expect(within(cells[4]!).getByText('au 30 septembre 2026')).toHaveClass('block');
  });

  it('says whether a card is debited at once or later, when the bank says so', async () => {
    serveBanks([connection({ accounts: [
      account({ id: 'c1', name: 'Carte Visa', kind: 'card', ibanMasked: '4321', cardDebit: 'immediate' }),
      account({ id: 'c2', name: 'Carte Premier', kind: 'card', ibanMasked: null, cardDebit: 'deferred' }),
      account({ id: 'c3', name: 'Carte de Léa', kind: 'card', ibanMasked: null }),
    ] })]);
    await renderApp('/settings/banks');

    const rows = within(await screen.findByRole('table', { name: 'Comptes de BNP Paribas' })).getAllByRole('row').slice(1);
    expect(rows.map((r) => within(r).getAllByRole('cell')[1]!.textContent)).toEqual(['Carte, débit immédiat', 'Carte, débit différé', 'Carte']);
    expect(rows[0]).toHaveTextContent('•••• 4321');
  });

  it('labels a revoked connection and one in error', async () => {
    serveBanks([connection({ status: 'revoked' }), connection({ id: 'c2', aspspName: 'Crédit Agricole', status: 'error', accounts: [] })]);
    await renderApp('/settings/banks');

    expect(await screen.findByRole('region', { name: 'BNP Paribas' })).toHaveTextContent('Révoquée');
    expect(screen.getByRole('region', { name: 'Crédit Agricole' })).toHaveTextContent('En erreur');
  });

  it('says a connection the transfer of the installation application ended, and offers only to delete it (#414)', async () => {
    serveBanks([connection({ status: 'revoked', lastErrorCode: 'banking.application_transferred' })]);
    const { user } = await renderApp('/settings/banks');

    const region = await screen.findByRole('region', { name: 'BNP Paribas' });
    expect(region).toHaveTextContent('Terminée');
    expect(region).toHaveTextContent('reliez à nouveau cette banque avec votre propre application');
    await user.click(within(region).getByRole('button', { name: 'Actions sur BNP Paribas' }));
    expect(screen.queryByRole('menuitem', { name: 'Renouveler' })).toBeNull();
    expect(screen.getByRole('menuitem', { name: 'Supprimer' })).toBeInTheDocument();
  });

  it('invites to connect a bank when there is none', async () => {
    serveBanks([]);
    await renderApp('/settings/banks');

    expect(await screen.findByRole('heading', { name: 'Aucune banque reliée' })).toBeInTheDocument();
    expect(await screen.findByRole('region', { name: 'Règles mémorisées' })).toHaveTextContent('Aucune règle mémorisée.');
  });

  it('says banking is off, and hides the « Banques » entry, when the household has it off', async () => {
    await renderApp('/settings/banks');

    expect(await screen.findByText(/La synchronisation bancaire n’est pas activée/)).toBeInTheDocument();
    expect(within(screen.getByRole('navigation', { name: 'Réglages' })).queryByRole('link', { name: 'Banques' })).toBeNull();
    expect(within(screen.getByRole('navigation', { name: 'Réglages' })).getByRole('link', { name: 'Apparence' })).toBeInTheDocument();
  });

  it('offers the wizard instead of « Relier une banque » while the adult has no application of their own', async () => {
    server.use(http.get('*/api/v1/banking/status', () => HttpResponse.json({ enabled: true, configured: false })));
    await renderApp('/settings/banks');

    expect(await screen.findByText('Pour relier une banque, créez d’abord votre propre application Enable Banking.')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Créer mon application' })).toHaveAttribute('href', '/settings/banks?tab=application&step=1');
    expect(within(screen.getByRole('navigation', { name: 'Réglages' })).getByRole('link', { name: 'Banques' })).toBeInTheDocument();
  });

  it('tells the administrator which part is missing and links to Administration › Banque', async () => {
    server.use(http.get('*/api/v1/banking/status', () => HttpResponse.json({ enabled: true, configured: false, appIdSaved: true, keyStatus: 'directory' })));
    await renderApp('/settings/banks');

    expect(await screen.findByText(/Nid n’a pas chargé la clé privée/)).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Créer mon application' })).toBeNull();
    expect(screen.getByRole('link', { name: 'Voir Administration › Banque' })).toHaveAttribute('href', '/admin/banking');
  });

  it('offers the administrator the wizard too once the installation has no application (#414)', async () => {
    server.use(http.get('*/api/v1/banking/status', () => HttpResponse.json({ enabled: true, configured: false, appIdSaved: false, keyStatus: 'loaded' })));
    await renderApp('/settings/banks');

    expect(await screen.findByRole('link', { name: 'Créer mon application' })).toBeInTheDocument();
  });

  it('without Enable Banking, still lists the imported accounts and offers « Importer un relevé », not « Relier une banque »', async () => {
    server.use(
      http.get('*/api/v1/banking/status', () => HttpResponse.json({ enabled: true, configured: false })),
      http.get('*/api/v1/banking/accounts', () => HttpResponse.json([account({ name: 'Relevé ·8901', ibanMasked: null, synced: false })])),
    );
    await renderApp('/settings/banks');

    expect(await screen.findByRole('link', { name: 'Créer mon application' })).toBeInTheDocument();
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

  it('offers to renew a bank the person is already connected to (#381)', async () => {
    serveBanks([connection()]);
    server.use(http.post('*/api/v1/banking/connections/:id/renew', ({ params }) =>
      HttpResponse.json({ id: String(params.id), redirectUrl: `https://bank.example/renew/${String(params.id)}` })));
    const { user, location } = await renderApp('/settings/banks');

    await user.click(await screen.findByRole('button', { name: 'Relier une banque' }));
    const dialog = await screen.findByRole('dialog', { name: 'Relier une banque' });
    expect(dialog).toHaveTextContent("reliez-y d’abord un nouveau compte, puis revenez ici");
    expect(within(dialog).getByRole('link', { name: 'Ouvrir le portail Enable Banking' })).toHaveAttribute('href', 'https://enablebanking.com/cp/applications');
    await pick(user, 'Banque', 'BNP Paribas');

    expect(within(dialog).getByRole('status')).toHaveTextContent('Vous avez déjà relié BNP Paribas.');
    expect(within(dialog).queryByRole('button', { name: 'Continuer vers la banque' })).toBeNull();
    await user.click(within(dialog).getByRole('button', { name: 'Renouveler' }));
    await waitFor(() => expect(location.assign).toHaveBeenCalledWith(`https://bank.example/renew/${CONNECTION}`));
  });

  it('links another access to a bank the person is already connected to', async () => {
    serveBanks([connection()]);
    const posted: unknown[] = [];
    server.use(http.post('*/api/v1/banking/connections', async ({ request }) => {
      posted.push(await request.json());
      return HttpResponse.json({ id: 'c2', redirectUrl: 'https://bank.example/authorize' }, { status: 201 });
    }));
    const { user, location } = await renderApp('/settings/banks');

    await user.click(await screen.findByRole('button', { name: 'Relier une banque' }));
    const dialog = await screen.findByRole('dialog', { name: 'Relier une banque' });
    await pick(user, 'Banque', 'BNP Paribas');
    await user.click(within(dialog).getByRole('button', { name: 'Relier un autre accès' }));

    await waitFor(() => expect(location.assign).toHaveBeenCalledWith('https://bank.example/authorize'));
    expect(posted).toEqual([{ aspspName: 'BNP Paribas' }]);
  });

  it('back from the bank, says so and opens its first unassociated account', async () => {
    serveBanks([connection()]);
    const { router } = await renderApp(`/settings/banks?connected=${CONNECTION}`);

    expect(await screen.findByRole('dialog', { name: 'Régler Compte courant' })).toBeInTheDocument();
    expect(screen.getByText('Banque reliée.')).toBeInTheDocument();
    await waitFor(() => expect(router.state.location.search).toEqual({}));
  });

  it('back from a bank that gave no account, says to link them in the portal rather than hailing it', async () => {
    serveBanks([connection({ accounts: [] })]);
    await renderApp(`/settings/banks?connected=${CONNECTION}`);

    const told = 'Nid ne lit aucun compte de cette banque. En mode restreint, Enable Banking ne donne que les comptes reliés dans votre portail : reliez-les-y, puis renouvelez la connexion.';
    await waitFor(() => expect(screen.getAllByText(told)).toHaveLength(2)); // the toast, and under the connection
    expect(screen.queryByText('Banque reliée.')).not.toBeInTheDocument();
  });

  it('opens the account an envelope page asks for (#373)', async () => {
    serveBanks([connection()]);
    const { router } = await renderApp(`/settings/banks?account=${CHECKING}`);

    expect(await screen.findByRole('dialog', { name: 'Régler Compte courant' })).toBeInTheDocument();
    await waitFor(() => expect(router.state.location.search).toEqual({}));
  });

  it('back from a refusal, says the bank refused', async () => {
    serveBanks([]);
    await renderApp('/settings/banks?error=banking.auth_refused');

    expect(await screen.findByRole('alert')).toHaveTextContent('La banque a refusé ou annulé l’autorisation.');
  });

  it('synchronises a connection, and says when one is already running', async () => {
    serveBanks([connection()]);
    const sent: unknown[] = [];
    server.use(http.post('*/api/v1/banking/sync', async ({ request }) => {
      sent.push(await request.json());
      return sent.length === 1
        ? HttpResponse.json({ requested: true }, { status: 202 })
        : HttpResponse.json({ status: 409, code: 'banking.sync_running', detail: 'Running.' }, { status: 409 });
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

  it('names the person’s private envelope and a child’s, and offers both in « Régler »', async () => {
    const own = summary({ id: 'e-own', displayName: 'Compte perso', kind: 'checking', visibility: 'private' });
    const child = summary({ id: 'e-child', displayName: 'Compte de Léa', kind: 'checking' });
    const savings = account({ id: 'b2', name: 'Compte Léa', ibanMasked: '5678', envelopeId: 'e-child' });
    serveBanks([connection({ accounts: [account({ envelopeId: 'e-own' }), savings] })]);
    // `household` leaves a private envelope out, `me` an envelope only a child owns.
    server.use(http.get('*/api/v1/investments/envelopes', ({ request }) =>
      HttpResponse.json({ items: new URL(request.url).searchParams.get('scope') === 'me' ? [own] : [child] })));
    const { user } = await renderApp('/settings/banks');

    const rows = within(await screen.findByRole('table', { name: 'Comptes de BNP Paribas' })).getAllByRole('row').slice(1);
    await waitFor(() => expect(rows[0]).toHaveTextContent('Compte perso'));
    expect(rows[1]).toHaveTextContent('Compte de Léa');
    await user.click(within(rows[0]!).getByRole('button', { name: 'Régler Compte courant' }));
    expect(within(await screen.findByRole('dialog', { name: 'Régler Compte courant' })).getByRole('combobox')).toHaveTextContent('Compte perso');
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

  const headings = () => within(screen.getByRole('main')).getAllByRole('heading', { level: 2 }).map((h) => h.textContent);

  it('is one page of sections, « Mon application » last once the person has a connection (#413); « Catégories » moved to Dépenses', async () => {
    serveBanks([connection()]);
    const { router } = await renderApp('/settings/banks');

    await screen.findByRole('region', { name: 'Mon application Enable Banking' });
    expect(screen.queryByRole('tab')).toBeNull();
    expect(headings()).toEqual(['BNP Paribas', 'Règles mémorisées', 'Mon application Enable Banking']);

    await router.navigate({ href: '/settings/banks?tab=categories&kind=income&scope=me' });
    await waitFor(() => expect(router.state.location.pathname).toBe('/spending'));
    expect(router.state.location.search).toEqual({ scope: 'me', tab: 'categories', kind: 'income' });
  });

  it('puts « Mon application » first while the person has neither an application nor a connection', async () => {
    serveBanks([]);
    await renderApp('/settings/banks');

    await screen.findByText('Étape 1 sur 8');
    expect(headings()[0]).toBe('Mon application Enable Banking');
  });

  it('leads `?tab=application` to « Mon application », its heading focused', async () => {
    serveBanks([connection()]);
    await renderApp('/settings/banks?tab=application');

    const heading = await screen.findByRole('heading', { name: 'Mon application Enable Banking' });
    await waitFor(() => expect(heading).toHaveFocus());
    expect(heading.closest('section')).toHaveAttribute('id', 'application');
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
    expect(await screen.findByText(/n’est plus reliée d’elle-même/)).toBeInTheDocument();
    expect(deleted).toBe('');
    await user.click(screen.getByRole('button', { name: 'Confirmer' }));

    await waitFor(() => expect(deleted).toBe('00000000-0000-7000-8000-0000000000c1'));
    await waitFor(() => expect(toasts()).toHaveTextContent('Règle supprimée.'));
    await waitFor(() => expect(within(section).queryByText('PRLV SEPA NETFLIX')).toBeNull());
  });
});
