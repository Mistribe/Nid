import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { pick } from '@/test/controls';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { householdPersons } from '@/test/importServer';
import { summary } from '../../investments/testData';
import { account, CARD, CHECKING, connection, JOINT, serveBanks } from './testData';

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
    expect(options).toEqual(['Aucune', 'Compte joint · Compte courant', 'Livret A · Livret', 'Créer une enveloppe']);
    await user.keyboard('{Escape}');
    await pick(user, 'Enveloppe', /^Compte joint/);
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

  it('shows what identifies a card and preselects the account its operations suggest, saved only on « Enregistrer »', async () => {
    const card = account({
      id: CARD, name: 'Visa Premier', ibanMasked: '4321', kind: 'card', cardDebit: 'deferred', holder: 'MME ALICE MARTIN', product: 'Visa Classic',
      lastBalance: '-52.50', lastBalanceOn: '2026-10-08',
    });
    serveBanks([connection({ accounts: [account(), card] })], envelopes);
    server.use(http.get('*/api/v1/banking/accounts/:id/overview', () => HttpResponse.json({
      recent: [
        { id: 'l1', bookedOn: '2026-10-07', valueOn: null, amount: '-12.00', currency: 'EUR', label: 'CB BOULANGERIE', counterparty: null, fromCard: false,
          cardName: null, fetchedVia: 'api', linkState: 'unlinked', link: null, accountName: 'Visa Premier' },
      ],
      suggestedCardOf: CHECKING,
    })));
    const sent = patches();
    const { user, container } = await renderApp('/settings/banks');

    expect((await screen.findAllByText('•••• 4321 · MME ALICE MARTIN · Visa Classic')).length).toBeGreaterThan(0);
    await user.click((await screen.findAllByRole('button', { name: 'Régler Visa Premier' }))[0]!);
    const dialog = await screen.findByRole('dialog', { name: 'Régler Visa Premier' });
    const identity = within(dialog).getByRole('region', { name: 'Ce que la banque en dit' });
    for (const text of ['•••• 4321', 'MME ALICE MARTIN', 'Visa Classic', 'Carte, débit différé']) expect(within(identity).getByText(text)).toBeInTheDocument();
    expect(await within(dialog).findByText('CB BOULANGERIE')).toBeInTheDocument();
    expect(await within(dialog).findByText("Suggéré d'après ses opérations : vérifiez avant d'enregistrer.")).toBeInTheDocument();
    expect(within(dialog).getByRole('combobox', { name: 'Carte rattachée à' })).toHaveTextContent('Compte courant');
    await expectNoAxeViolations(container);
    await user.click(within(dialog).getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(sent).toEqual([{ id: CARD, body: { cardOfAccountId: CHECKING } }]));
  });

  it('shows only the type of an account the bank says little of, and no suggestion', async () => {
    serveBanks([connection({ accounts: [account(), account({ id: CARD, name: 'Carte', ibanMasked: null, kind: 'card', lastBalance: null, lastBalanceOn: null })] })], envelopes);
    const { user } = await renderApp('/settings/banks');

    await user.click((await screen.findAllByRole('button', { name: 'Régler Carte' }))[0]!);
    const dialog = await screen.findByRole('dialog', { name: 'Régler Carte' });
    const identity = within(dialog).getByRole('region', { name: 'Ce que la banque en dit' });
    expect(within(identity).getAllByRole('term').map((term) => term.textContent)).toEqual(['Type']);
    expect(await within(dialog).findByText("Aucune opération lue pour l'instant.")).toBeInTheDocument();
    expect(within(dialog).getByRole('combobox', { name: 'Carte rattachée à' })).toHaveTextContent('Aucun');
  });

  it('names an account the bank sent no name for by its masked IBAN, else by its kind, everywhere', async () => {
    const SECOND = '00000000-0000-7000-8000-0000000000b4';
    const card = account({ id: CARD, name: 'Visa Premier', ibanMasked: null, kind: 'card', cardOfAccountId: CHECKING });
    serveBanks([connection({ accounts: [account({ name: '' }), account({ id: SECOND, name: ' ', ibanMasked: null }), card] })], envelopes);
    const sent = patches();
    const { user } = await renderApp('/settings/banks');

    const table = await screen.findByRole('table', { name: 'Comptes de BNP Paribas' });
    const rows = within(table).getAllByRole('row').slice(1);
    expect(rows[0]).toHaveTextContent('•••• 1234');
    expect(rows[2]).toHaveTextContent('Carte de •••• 1234');
    expect(within(table).getByRole('button', { name: 'Régler •••• 1234' })).toBeInTheDocument();
    expect(within(table).getByRole('button', { name: 'Régler Compte courant' })).toBeInTheDocument();

    await user.click(within(table).getByRole('button', { name: 'Régler Visa Premier' }));
    const dialog = await screen.findByRole('dialog', { name: 'Régler Visa Premier' });
    await user.click(within(dialog).getByRole('combobox', { name: 'Carte rattachée à' }));
    const options = within(await screen.findByRole('listbox')).getAllByRole('option').map((o) => o.textContent);
    expect(options).toEqual(['Aucun', '•••• 1234', 'Compte courant']);
    await user.keyboard('{Escape}');
    await pick(user, 'Carte rattachée à', 'Compte courant');
    await user.click(within(dialog).getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(sent).toEqual([{ id: CARD, body: { cardOfAccountId: SECOND } }]));
  });

  it('gives the account the household’s own name, the bank’s as its placeholder', async () => {
    serveBanks([connection()], envelopes);
    const sent = patches();
    const { user, container } = await renderApp('/settings/banks');

    await user.click((await screen.findAllByRole('button', { name: 'Régler Compte courant' }))[0]!);
    const dialog = await screen.findByRole('dialog', { name: 'Régler Compte courant' });
    const name = within(dialog).getByRole('textbox', { name: 'Nom' });
    expect(name).toHaveValue('');
    expect(name).toHaveAttribute('placeholder', 'Compte courant');
    expect(within(dialog).queryByRole('button', { name: 'Rétablir le nom de la banque' })).toBeNull();
    await user.type(name, '  Compte joint ');
    await expectNoAxeViolations(container);
    await user.click(within(dialog).getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(sent).toEqual([{ id: CHECKING, body: { name: 'Compte joint' } }]));
    expect(await screen.findByText('Compte joint enregistré.')).toBeInTheDocument();
  });

  it('goes back to the bank’s name', async () => {
    serveBanks([connection({ accounts: [account({ name: 'Compte joint', bankName: 'Compte de dépôt', renamed: true })] })], envelopes);
    const sent = patches();
    const { user } = await renderApp('/settings/banks');

    await user.click((await screen.findAllByRole('button', { name: 'Régler Compte joint' }))[0]!);
    const dialog = await screen.findByRole('dialog', { name: 'Régler Compte joint' });
    expect(within(dialog).getByRole('textbox', { name: 'Nom' })).toHaveValue('Compte joint');
    await user.click(within(dialog).getByRole('button', { name: 'Rétablir le nom de la banque' }));
    expect(within(dialog).getByRole('textbox', { name: 'Nom' })).toHaveValue('');
    await user.click(within(dialog).getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(sent).toEqual([{ id: CHECKING, body: { clearName: true } }]));
    expect(await screen.findByText('Compte de dépôt enregistré.')).toBeInTheDocument();
  });

  it('shows a refused name on its field', async () => {
    serveBanks([connection()], envelopes);
    server.use(http.patch('*/api/v1/banking/accounts/:id', () => HttpResponse.json({
      status: 422, detail: 'Invalid.', errors: [{ detail: 'Too long.', pointer: '#/name', code: 'banking.invalid_name' }],
    }, { status: 422 })));
    const { user } = await renderApp('/settings/banks');

    await user.click((await screen.findAllByRole('button', { name: 'Régler Compte courant' }))[0]!);
    const dialog = await screen.findByRole('dialog', { name: 'Régler Compte courant' });
    await user.type(within(dialog).getByRole('textbox', { name: 'Nom' }), 'Courant');
    await user.click(within(dialog).getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(within(dialog).getByRole('textbox', { name: 'Nom' }))
      .toHaveAccessibleDescription(/Un nom compte de 1 à 100 caractères\./));
  });

  it('names each envelope with its owners and kind, those that fit the account first, none another bank account holds', async () => {
    const [alice, bob, leo] = householdPersons;
    const own = (...ids: string[]) => ids.map((personId) => ({ personId, shareBp: 10_000 / ids.length }));
    serveBanks([connection({ accounts: [account(), account({ id: 'other', name: 'Livret', kind: 'savings', envelopeId: 'held' })] })], [
      summary({ id: 'la-alice', displayName: 'Livret A', kind: 'savings', owners: own(alice!.id) }),
      summary({ id: 'cc-joint', displayName: 'Compte joint', kind: 'checking', owners: own(alice!.id, bob!.id) }),
      summary({ id: 'cc-usd', displayName: 'Compte dollars', kind: 'checking', currency: 'USD', owners: own(bob!.id) }),
      summary({ id: 'la-leo', displayName: 'Livret A', kind: 'savings', owners: own(leo!.id) }),
      summary({ id: 'held', displayName: 'Livret tenu', kind: 'savings' }),
    ]);
    server.use(http.get('*/api/v1/core/persons', () => HttpResponse.json(householdPersons)));
    const { user } = await renderApp('/settings/banks');

    await user.click((await screen.findAllByRole('button', { name: 'Régler Compte courant' }))[0]!);
    const dialog = await screen.findByRole('dialog', { name: 'Régler Compte courant' });
    await waitFor(async () => {
      await user.click(within(dialog).getByRole('combobox', { name: 'Enveloppe' }));
      const list = await screen.findByRole('listbox');
      expect(within(list).getAllByRole('option').map((o) => o.textContent)).toEqual([
        'Aucune', 'Compte joint · Alice Dev et Bob Dev · Compte courant',
        'Livret A · Alice Dev · Livret', 'Compte dollars · Bob Dev · Compte courant · USD', 'Livret A · Léo · Livret', 'Créer une enveloppe',
      ]);
      expect(within(list).getByRole('group', { name: 'Pour ce compte' })).toHaveTextContent('Compte joint');
      expect(within(list).getByRole('group', { name: 'Autres enveloppes' })).toHaveTextContent('Compte dollars');
      await user.keyboard('{Escape}');
    });
  });

  it('offers a search box once the list is long', async () => {
    serveBanks([connection()], Array.from({ length: 8 }, (_, i) => summary({ id: `livret-${i}`, displayName: `Livret ${i}`, kind: 'savings' })));
    const sent = patches();
    const { user } = await renderApp('/settings/banks');

    await user.click((await screen.findAllByRole('button', { name: 'Régler Compte courant' }))[0]!);
    const dialog = await screen.findByRole('dialog', { name: 'Régler Compte courant' });
    await user.click(within(dialog).getByRole('combobox', { name: 'Enveloppe' }));
    await user.type(await screen.findByPlaceholderText('Rechercher une enveloppe'), 'Livret 7');
    await user.click(await screen.findByRole('option', { name: 'Livret 7 · Livret' }));
    await user.click(within(dialog).getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(sent).toEqual([{ id: CHECKING, body: { envelopeId: 'livret-7' } }]));
  });

  it('creates the envelope from the account and links it, in one request', async () => {
    serveBanks([connection()], envelopes);
    server.use(http.get('*/api/v1/core/persons', () => HttpResponse.json(householdPersons)));
    const sent: unknown[] = [];
    server.use(http.post('*/api/v1/banking/accounts/:id/envelope', async ({ params, request }) => {
      sent.push({ id: params.id, body: await request.json() });
      return HttpResponse.json({ id: 'new' }, { status: 201 });
    }));
    const { user, container } = await renderApp('/settings/banks');

    await user.click((await screen.findAllByRole('button', { name: 'Régler Compte courant' }))[0]!);
    const dialog = await screen.findByRole('dialog', { name: 'Régler Compte courant' });
    await pick(user, 'Enveloppe', 'Créer une enveloppe');
    const step = within(dialog).getByRole('form', { name: 'Créer une enveloppe' });
    expect(within(step).getByRole('textbox', { name: 'Nom de l\'enveloppe' })).toHaveValue('BNP Paribas Compte courant');
    expect(within(step).getByRole('radio', { name: 'Compte courant' })).toBeChecked();
    expect(within(step).getByRole('radio', { name: 'À moi' })).toBeChecked();
    expect(step).toHaveTextContent('En EUR, comme le compte, et reliée à lui.');
    await user.click(within(step).getByRole('radio', { name: 'Commun' }));
    await expectNoAxeViolations(container);
    await user.click(within(step).getByRole('button', { name: 'Créer et relier' }));

    await waitFor(() => expect(sent).toEqual([{ id: CHECKING, body: { displayName: 'BNP Paribas Compte courant', kind: 'checking', joint: true } }]));
    expect(await screen.findByText('Enveloppe BNP Paribas Compte courant créée et reliée.')).toBeInTheDocument();
  });

  it('creates a child’s envelope, theirs alone, for an account both adults manage', async () => {
    serveBanks([connection()], envelopes);
    server.use(http.get('*/api/v1/core/persons', () => HttpResponse.json(householdPersons)));
    const sent: unknown[] = [];
    server.use(http.post('*/api/v1/banking/accounts/:id/envelope', async ({ request }) => {
      sent.push(await request.json());
      return HttpResponse.json({ id: 'new' }, { status: 201 });
    }));
    const { user } = await renderApp('/settings/banks');

    await user.click((await screen.findAllByRole('button', { name: 'Régler Compte courant' }))[0]!);
    const dialog = await screen.findByRole('dialog', { name: 'Régler Compte courant' });
    await pick(user, 'Enveloppe', 'Créer une enveloppe');
    const step = within(dialog).getByRole('form', { name: 'Créer une enveloppe' });
    await user.click(within(step).getByRole('radio', { name: 'Compte de Léo' }));
    expect(step).toHaveTextContent('L\'enveloppe est à Léo et partagée');
    await user.click(within(step).getByRole('button', { name: 'Créer et relier' }));

    await waitFor(() => expect(sent).toEqual([{ displayName: 'BNP Paribas Compte courant', kind: 'checking', dependentId: householdPersons[2]!.id }]));
  });

  it('shows whose account each associated one is, a card its account’s', async () => {
    const child = summary({ id: JOINT, displayName: 'Compte de Léo', kind: 'checking', owners: [{ personId: householdPersons[2]!.id, shareBp: 10000 }] });
    serveBanks([connection({ accounts: [account({ envelopeId: JOINT }), account({ id: CARD, name: 'Visa Premier', ibanMasked: null, kind: 'card', cardOfAccountId: CHECKING })] })], [child]);
    server.use(http.get('*/api/v1/core/persons', () => HttpResponse.json(householdPersons)));
    await renderApp('/settings/banks');

    const table = (await screen.findAllByRole('table'))[0]!;
    // The account's chip and its card's.
    await waitFor(() => expect(within(table).getAllByText('Léo')).toHaveLength(2));
  });

  it('goes back from the creation step, and shows a refused link there', async () => {
    serveBanks([connection()], envelopes);
    server.use(http.post('*/api/v1/banking/accounts/:id/envelope', () =>
      HttpResponse.json({ status: 409, code: 'banking.envelope_taken', detail: 'Taken.' }, { status: 409 })));
    const { user } = await renderApp('/settings/banks');

    await user.click((await screen.findAllByRole('button', { name: 'Régler Compte courant' }))[0]!);
    const dialog = await screen.findByRole('dialog', { name: 'Régler Compte courant' });
    await pick(user, 'Enveloppe', 'Créer une enveloppe');
    // Alone in the household: nothing to share it with.
    expect(within(dialog).queryByRole('radio', { name: 'Commun' })).toBeNull();
    await user.click(within(dialog).getByRole('button', { name: 'Créer et relier' }));
    expect(await within(dialog).findByRole('alert')).toHaveTextContent('Un autre compte bancaire est déjà relié à cette enveloppe.');

    await user.click(within(dialog).getByRole('button', { name: 'Retour' }));
    expect(within(dialog).getByRole('combobox', { name: 'Enveloppe' })).toBeInTheDocument();
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
      HttpResponse.json({ status: 409, code: 'banking.envelope_taken', detail: 'Taken.' }, { status: 409 })));
    const { user } = await renderApp('/settings/banks');

    await user.click((await screen.findAllByRole('button', { name: 'Régler Compte courant' }))[0]!);
    const dialog = await screen.findByRole('dialog', { name: 'Régler Compte courant' });
    await pick(user, 'Enveloppe', /^Compte joint/);
    await user.click(within(dialog).getByRole('button', { name: 'Enregistrer' }));

    const field = await within(dialog).findByRole('combobox', { name: 'Enveloppe' });
    await waitFor(() => expect(field).toHaveAttribute('aria-invalid', 'true'));
    expect(field).toHaveAccessibleDescription('Un autre compte bancaire est déjà relié à cette enveloppe.');
  });

  it('shows a currency mismatch on the envelope field', async () => {
    serveBanks([connection()], envelopes);
    server.use(http.patch('*/api/v1/banking/accounts/:id', () => HttpResponse.json({
      status: 422, detail: 'Invalid.', errors: [{ detail: 'Mismatch.', pointer: '#/envelopeId', code: 'banking.currency_mismatch' }],
    }, { status: 422 })));
    const { user } = await renderApp('/settings/banks');

    await user.click((await screen.findAllByRole('button', { name: 'Régler Compte courant' }))[0]!);
    const dialog = await screen.findByRole('dialog', { name: 'Régler Compte courant' });
    await pick(user, 'Enveloppe', /^Compte joint/);
    await user.click(within(dialog).getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(within(dialog).getByRole('combobox', { name: 'Enveloppe' }))
      .toHaveAccessibleDescription('La devise du compte et celle de l\'enveloppe diffèrent.'));
  });
});
