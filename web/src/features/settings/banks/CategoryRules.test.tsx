import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { pick } from '@/test/controls';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { BankCategory, BankTransaction, CategoryRule } from '@/api/banking';
import { account, CARD, CHECKING, JOINT } from './testData';

const category = (over: Partial<BankCategory>): BankCategory => ({ id: 'c', parentId: null, key: null, name: '', kind: 'expense', archived: false, mergedIntoId: null, defaultName: null, ...over });
const categories = [
  category({ id: 'food', key: 'food', name: 'Alimentation' }),
  category({ id: 'groceries', parentId: 'food', key: 'food.groceries', name: 'Courses' }),
  category({ id: 'leisure', key: 'leisure', name: 'Loisirs' }),
];

const rule = (over: Partial<CategoryRule>): CategoryRule => ({
  id: 'r1', pattern: 'CARREFOUR', accountId: null, accountName: null, direction: null, minAmount: null, maxAmount: null,
  categoryId: 'groceries', categoryName: 'Courses', canEdit: true, lines: 12, lastUsedAt: '2026-10-02T04:30:00+00:00', categoryArchived: false, ...over,
});
const rules = () => [
  rule({}),
  rule({ id: 'r2', pattern: 'CINEMA', accountId: JOINT, accountName: 'Compte joint', direction: 'debit', minAmount: '10.00', maxAmount: '50.00',
    categoryId: 'leisure', categoryName: 'Loisirs', canEdit: false, lines: 0, lastUsedAt: null }),
];

const line = (id: string, label: string, amount: string): BankTransaction => ({
  id, bookedOn: '2026-10-27', valueOn: null, amount, currency: 'EUR', label, counterparty: null, fromCard: false, cardName: null, fetchedVia: 'file',
  linkState: 'unlinked', link: null, accountName: 'Compte courant', category: null, categorySource: null, accountId: CHECKING, suggestedPattern: label, sharedAccount: true,
});

const refusal = (code: string, path: string) => HttpResponse.json(
  { status: 422, code, detail: 'Refused.', errors: [{ path, code, message: 'Refused.' }] }, { status: 422 });

/** Banking on, two accounts (the card only read), the categories and the rules; every write as `[method, rule id, body]`. */
function serve() {
  const writes: [string, unknown, unknown][] = [];
  server.use(
    http.get('*/api/v1/banking/status', () => HttpResponse.json({ enabled: true, configured: false })),
    http.get('*/api/v1/banking/accounts', () => HttpResponse.json([account(), account({ id: CARD, name: 'Visa', canWrite: false })])),
    http.get('*/api/v1/banking/categories', () => HttpResponse.json(categories)),
    http.get('*/api/v1/banking/category-rules', () => HttpResponse.json(rules())),
    http.post('*/api/v1/banking/category-rules', async ({ request }) => {
      const body = await request.json() as { pattern: string };
      writes.push(['POST', null, body]);
      if (body.pattern === 'X') return refusal('banking.rule_pattern', 'pattern');
      if (body.pattern === 'BORNES') return refusal('banking.rule_amounts', 'maxAmount');
      return HttpResponse.json(rule({ id: 'r3' }), { status: 201 });
    }),
    http.patch('*/api/v1/banking/category-rules/:id', async ({ request, params }) => {
      const body = await request.json() as { accountId: string | null; pattern: string };
      writes.push(['PATCH', params.id, body]);
      return body.accountId || body.pattern === 'FORBID'
        ? HttpResponse.json({ status: 403, detail: 'Only its author changes the account of this rule.' }, { status: 403 })
        : HttpResponse.json(rule({}));
    }),
    http.delete('*/api/v1/banking/category-rules/:id', ({ params }) => { writes.push(['DELETE', params.id, null]); return new HttpResponse(null, { status: 204 }); }),
    http.get('*/api/v1/banking/category-rules/:id/preview', () => HttpResponse.json({
      total: 51, items: [line('t1', 'CARREFOUR CITY', '-42.10'), line('t2', 'CARREFOUR MARKET', '-18.00')],
    })),
    http.post('*/api/v1/banking/category-rules/:id/apply', ({ params }) => { writes.push(['APPLY', params.id, null]); return HttpResponse.json({ changed: 51 }); }),
  );
  return writes;
}

async function open() {
  const app = await renderApp('/settings/banks?tab=categories');
  await screen.findByRole('table', { name: 'Règles de catégorie' });
  return app;
}

const menu = async (user: Awaited<ReturnType<typeof renderApp>>['user'], pattern: string, item: string) => {
  await user.click(screen.getAllByRole('button', { name: `Actions sur la règle ${pattern}` })[0]!);
  await user.click(await screen.findByRole('menuitem', { name: item }));
};

describe('Réglages › Banques › Catégories › Règles de catégorie', () => {
  beforeAll(async () => { await import('./BanksPage'); await import('./CategoriesTab'); }, 30_000);

  it('lists the rules with their account, conditions, category and use; one the person cannot change offers only « Appliquer »', async () => {
    serve();
    const { container, user } = await open();

    const rows = within(screen.getByRole('table', { name: 'Règles de catégorie' })).getAllByRole('row').slice(1);
    expect(rows[0]).toHaveTextContent('CARREFOUR');
    expect(rows[0]).toHaveTextContent('Tous les comptes');
    expect(rows[0]).toHaveTextContent('Courses');
    expect(rows[0]).toHaveTextContent('12');
    expect(rows[1]).toHaveTextContent('Débits · dès 10,00 · jusqu\'à 50,00');
    expect(rows[1]).toHaveTextContent('Compte joint');
    expect(rows[1]).toHaveTextContent('Jamais');
    await expectNoAxeViolations(container);

    await user.click(screen.getAllByRole('button', { name: 'Actions sur la règle CINEMA' })[0]!);
    expect((await screen.findAllByRole('menuitem')).map((m) => m.textContent)).toEqual(['Appliquer aux opérations passées']);
  });

  it('creates a rule, its account, direction and amounts under « Personnaliser »', async () => {
    const writes = serve();
    const { user } = await open();

    await user.click(screen.getByRole('button', { name: 'Nouvelle règle' }));
    const dialog = await screen.findByRole('dialog', { name: 'Nouvelle règle' });
    await user.type(within(dialog).getByRole('textbox', { name: 'Motif' }), 'Carrefour');
    await pick(user, 'Catégorie', 'Alimentation › Courses');
    await user.click(within(dialog).getByRole('button', { name: 'Personnaliser' }));
    await user.click(within(dialog).getByRole('combobox', { name: 'Compte' }));
    // Only the accounts the person writes.
    expect((await screen.findAllByRole('option')).map((o) => o.textContent)).toEqual(['Tous les comptes', 'Compte courant']);
    await user.click(screen.getByRole('option', { name: 'Compte courant' }));
    await user.click(within(dialog).getByRole('radio', { name: 'Débits' }));
    await user.type(within(dialog).getByRole('textbox', { name: 'Montant minimum' }), '10,5');
    await expectNoAxeViolations(dialog);
    await user.click(within(dialog).getByRole('button', { name: 'Créer la règle' }));

    await waitFor(() => expect(writes).toEqual([['POST', null,
      { pattern: 'Carrefour', categoryId: 'groceries', accountId: CHECKING, direction: 'debit', minAmount: '10.5', maxAmount: null }]]));
    expect(await screen.findByText('Règle créée.')).toBeInTheDocument();
  });

  it('shows a refused pattern under its field', async () => {
    serve();
    const { user } = await open();

    await user.click(screen.getByRole('button', { name: 'Nouvelle règle' }));
    const dialog = await screen.findByRole('dialog', { name: 'Nouvelle règle' });
    const field = within(dialog).getByRole('textbox', { name: 'Motif' });
    await user.type(field, 'X');
    await pick(user, 'Catégorie', 'Loisirs');
    await user.click(within(dialog).getByRole('button', { name: 'Créer la règle' }));

    await waitFor(() => expect(field).toHaveAttribute('aria-invalid', 'true'));
    expect(field).toHaveAccessibleDescription(/Un motif compte de 2 à 60 caractères, dont au moins une lettre, sans compter les préfixes de banque \(CB, VIR, PRLV…\)\./);
  });

  it('shows a refused bound under the field the server names', async () => {
    serve();
    const { user } = await open();

    await user.click(screen.getByRole('button', { name: 'Nouvelle règle' }));
    const dialog = await screen.findByRole('dialog', { name: 'Nouvelle règle' });
    await user.type(within(dialog).getByRole('textbox', { name: 'Motif' }), 'BORNES');
    await pick(user, 'Catégorie', 'Loisirs');
    await user.click(within(dialog).getByRole('button', { name: 'Personnaliser' }));
    await user.type(within(dialog).getByRole('textbox', { name: 'Montant minimum' }), '50');
    await user.type(within(dialog).getByRole('textbox', { name: 'Montant maximum' }), '10');
    await user.click(within(dialog).getByRole('button', { name: 'Créer la règle' }));

    const max = within(dialog).getByRole('textbox', { name: 'Montant maximum' });
    await waitFor(() => expect(max).toHaveAttribute('aria-invalid', 'true'));
    expect(within(dialog).getByRole('textbox', { name: 'Montant minimum' })).not.toHaveAttribute('aria-invalid');
  });

  it('saves a rule as a whole, and says who may change its account', async () => {
    const writes = serve();
    const { user } = await open();

    await menu(user, 'CARREFOUR', 'Modifier');
    const dialog = await screen.findByRole('dialog', { name: 'Modifier la règle « CARREFOUR »' });
    await pick(user, 'Catégorie', 'Loisirs');
    await user.click(within(dialog).getByRole('button', { name: 'Enregistrer' }));
    await waitFor(() => expect(writes).toEqual([['PATCH', 'r1',
      { pattern: 'CARREFOUR', categoryId: 'leisure', accountId: null, direction: null, minAmount: null, maxAmount: null }]]));
    expect(await screen.findByText('Règle enregistrée.')).toBeInTheDocument();

    await menu(user, 'CARREFOUR', 'Modifier');
    const again = await screen.findByRole('dialog', { name: 'Modifier la règle « CARREFOUR »' });
    await user.click(within(again).getByRole('button', { name: 'Personnaliser' }));
    await pick(user, 'Compte', 'Compte courant');
    await user.click(within(again).getByRole('button', { name: 'Enregistrer' }));
    expect(await within(again).findByText('Seule la personne qui a créé la règle peut changer son compte.')).toBeInTheDocument();
  });

  it('says « Vous ne pouvez pas modifier cette règle. » for a refusal that is not about the account', async () => {
    serve();
    const { user } = await open();

    await menu(user, 'CARREFOUR', 'Modifier');
    const dialog = await screen.findByRole('dialog', { name: 'Modifier la règle « CARREFOUR »' });
    const field = within(dialog).getByRole('textbox', { name: 'Motif' });
    await user.clear(field);
    await user.type(field, 'FORBID');
    await user.click(within(dialog).getByRole('button', { name: 'Enregistrer' }));
    expect(await within(dialog).findByText('Vous ne pouvez pas modifier cette règle.')).toBeInTheDocument();
  });

  it('marks a rule whose category is archived, and says why it no longer applies', async () => {
    serve();
    server.use(http.get('*/api/v1/banking/category-rules', () => HttpResponse.json([rule({ categoryArchived: true })])));
    const { user } = await open();

    const row = within(screen.getByRole('table', { name: 'Règles de catégorie' })).getAllByRole('row')[1]!;
    expect(row).toHaveTextContent('Courses (archivée)');
    await user.hover(within(row).getByRole('button', { name: 'À propos : Courses' }));
    expect((await screen.findAllByText('Cette règle ne s\'applique plus : sa catégorie est archivée.')).length).toBeGreaterThan(0);
  });

  it('previews the past lines, then applies the rule to them', async () => {
    const writes = serve();
    const { user } = await open();

    await menu(user, 'CARREFOUR', 'Appliquer aux opérations passées');
    const dialog = await screen.findByRole('dialog', { name: 'Appliquer « CARREFOUR » aux opérations passées' });
    expect(await within(dialog).findByText('51 opérations passeront en Courses.')).toBeInTheDocument();
    const table = within(dialog).getByRole('table');
    expect(table).toHaveTextContent('CARREFOUR CITY');
    expect(table).toHaveTextContent('Sans catégorie');
    expect(within(dialog).getByText('Seules les 2 plus récentes sont listées ici.')).toBeInTheDocument();
    await expectNoAxeViolations(dialog);
    expect(writes).toEqual([]);

    await user.click(within(dialog).getByRole('button', { name: 'Appliquer à 51 opérations' }));
    await waitFor(() => expect(writes).toEqual([['APPLY', 'r1', null]]));
    expect(await screen.findByText('51 opérations classées.')).toBeInTheDocument();
  });

  it('deletes a rule after a confirmation', async () => {
    const writes = serve();
    const { user } = await open();

    await menu(user, 'CARREFOUR', 'Supprimer');
    expect(await screen.findByText(/gardent leur catégorie/)).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Confirmer' }));

    await waitFor(() => expect(writes).toEqual([['DELETE', 'r1', null]]));
    expect(await screen.findByText('Règle supprimée.')).toBeInTheDocument();
  });
});
