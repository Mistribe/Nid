import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { pick } from '@/test/controls';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { BankCategory } from '@/api/banking';

const category = (over: Partial<BankCategory>): BankCategory => ({ id: 'c', parentId: null, key: null, name: '', kind: 'expense', archived: false, mergedIntoId: null, defaultName: null, lines: 0, ...over });
// Usage is the server's: a parent's lines count its children's.
const seed = () => [
  category({ id: 'food', key: 'food', name: 'Alimentation', defaultName: 'Alimentation', lines: 12, lastLineOn: '2026-11-02' }),
  category({ id: 'groceries', parentId: 'food', key: 'food.groceries', name: 'Courses du mois', defaultName: 'Courses', lines: 9, lastLineOn: '2026-11-02' }),
  category({ id: 'restaurants', parentId: 'food', key: 'food.restaurants', name: 'Restaurants & cafés', defaultName: 'Restaurants & cafés', lines: 3, lastLineOn: '2026-10-20' }),
  category({ id: 'leisure', key: 'leisure', name: 'Loisirs', defaultName: 'Loisirs', lines: 20, lastLineOn: '2026-11-10' }),
  category({ id: 'health', key: 'health', name: 'Santé', defaultName: 'Santé' }),
  category({ id: 'market', parentId: 'food', name: 'Marché du dimanche', archived: true }),
  category({ id: 'gone', parentId: 'food', name: 'Supérette', archived: true, mergedIntoId: 'groceries' }),
  category({ id: 'income', key: 'income', name: 'Revenus', kind: 'income', defaultName: 'Revenus' }),
  category({ id: 'internal', key: 'internal', name: 'Mouvements internes', kind: 'transfer', defaultName: 'Mouvements internes' }),
];

/** Banking on, the categories (changed by the writes), and every write as `[method, path, body]`. */
function serve() {
  let list = seed();
  const writes: [string, string, unknown][] = [];
  server.use(
    http.get('*/api/v1/banking/status', () => HttpResponse.json({ enabled: true, configured: false })),
    http.get('*/api/v1/banking/accounts', () => HttpResponse.json([])),
    http.get('*/api/v1/banking/categories', () => HttpResponse.json(list)),
    http.get('*/api/v1/banking/category-rules', () => HttpResponse.json([])),
    http.post('*/api/v1/banking/categories', async ({ request }) => {
      const body = await request.json() as { name: string; parentId?: string; kind?: string };
      writes.push(['POST', 'categories', body]);
      if (body.name === 'Courses') return HttpResponse.json({ status: 422, code: 'banking.category_name', detail: 'Taken.', errors: [{ path: 'name', code: 'banking.category_name', message: 'Taken.' }] }, { status: 422 });
      const made = category({ id: 'new', name: body.name, parentId: body.parentId ?? null, kind: body.kind ?? 'expense' });
      list = [...list, made];
      return HttpResponse.json(made, { status: 201 });
    }),
    http.patch('*/api/v1/banking/categories/:id', async ({ request, params }) => {
      const body = await request.json() as Partial<BankCategory> & { resetName?: boolean };
      writes.push(['PATCH', String(params.id), body]);
      list = list.map((c) => (c.id === params.id ? { ...c, ...(body.name ? { name: body.name } : {}), ...(body.archived != null ? { archived: body.archived } : {}) } : c));
      return HttpResponse.json(list.find((c) => c.id === params.id));
    }),
    http.post('*/api/v1/banking/categories/:id/merge', async ({ request, params }) => {
      const body = await request.json() as { into: string };
      writes.push(['MERGE', String(params.id), body]);
      list = list.map((c) => (c.id === params.id ? { ...c, archived: true, mergedIntoId: body.into } : c));
      return HttpResponse.json(list.find((c) => c.id === body.into));
    }),
  );
  return writes;
}

async function open(path = '/spending?tab=categories', region = 'Dépenses') {
  const app = await renderApp(path);
  await screen.findByRole('region', { name: region });
  return app;
}

const unfold = async (user: Awaited<ReturnType<typeof renderApp>>['user']) =>
  user.click(screen.getAllByRole('button', { name: 'Afficher les sous-catégories d’Alimentation' })[0]!);

const menu = async (user: Awaited<ReturnType<typeof renderApp>>['user'], name: string, item: string) => {
  await user.click(screen.getAllByRole('button', { name: `Actions sur la catégorie ${name}` })[0]!);
  await user.click(await screen.findByRole('menuitem', { name: item }));
};

describe('Dépenses › Catégories', () => {
  beforeAll(async () => { await import('./SpendingPage'); await import('./CategoriesTab'); }, 30_000);

  it('says what categories are for, and lists one kind by use, children folded, the unused and the archived folded', async () => {
    serve();
    const { container, user } = await open();

    expect(screen.getByText(/Elles sont communes au foyer/)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Voir la vue d’ensemble' })).toHaveAttribute('href', '/spending');
    const expenses = within(screen.getByRole('region', { name: 'Dépenses' }));
    const table = expenses.getByRole('table', { name: 'Dépenses' });
    const names = () => within(table).getAllByRole('row').slice(1).map((r) => within(r).getAllByRole('cell')[0]!.textContent);
    expect(names()).toEqual(['Loisirs', 'Alimentation · 2 sous-catégories']);
    expect(within(table).getAllByRole('row')[2]).toHaveTextContent('12 opérations');
    await unfold(user);
    expect(names()).toEqual(['Loisirs', 'Alimentation · 2 sous-catégories', 'Courses du mois', 'Restaurants & cafés']);
    // Unused and archived, folded; merged ones never listed.
    expect(within(table).queryByText('Santé')).toBeNull();
    await user.click(expenses.getByRole('button', { name: 'Non utilisée (1)' }));
    expect(expenses.getByRole('table', { name: 'Non utilisée (1)' })).toHaveTextContent('Santé0 opération');
    expect(screen.queryByText('Supérette')).toBeNull();
    expect(screen.queryByText(/Marché du dimanche/)).toBeNull();
    expect(screen.getByRole('button', { name: 'Archivées' })).toBeInTheDocument();
    expect(screen.queryByRole('region', { name: 'Revenus' })).toBeNull();
    await expectNoAxeViolations(container);
  });

  it('shows one kind at a time, kept in the address', async () => {
    serve();
    const { user, router } = await open();

    await user.click(screen.getByRole('radio', { name: 'Mouvements internes' }));
    const internal = await screen.findByRole('region', { name: 'Mouvements internes' });
    expect(within(internal).getByRole('button', { name: 'À propos : Mouvements internes' })).toBeInTheDocument();
    expect(router.state.location.search).toEqual({ tab: 'categories', kind: 'transfer' });
    expect(screen.queryByRole('region', { name: 'Dépenses' })).toBeNull();
  });

  it('says so under a kind without any category', async () => {
    serve();
    server.use(http.get('*/api/v1/banking/categories', () => HttpResponse.json(seed().filter((c) => c.kind !== 'income'))));
    await open('/spending?tab=categories&kind=income', 'Revenus');

    const income = screen.getByRole('region', { name: 'Revenus' });
    expect(income).toHaveTextContent('Aucune catégorie.');
    expect(within(income).queryByRole('table')).toBeNull();
  });

  it('opens the lines of an expense category over 12 months', async () => {
    serve();
    let asked = '';
    server.use(http.get('*/api/v1/banking/spending/lines', ({ request }) => {
      asked = new URL(request.url).search;
      return HttpResponse.json({ items: [], total: '0.00', count: 0, incomplete: false, currency: 'EUR' });
    }));
    const { user } = await open();

    await user.click(screen.getAllByRole('button', { name: 'Voir les opérations de Loisirs' })[0]!);
    expect(await screen.findByRole('dialog', { name: /Loisirs/ })).toBeInTheDocument();
    await waitFor(() => expect(new URLSearchParams(asked).get('categoryId')).toBe('leisure'));
    expect(new URLSearchParams(asked).get('scope')).toBe('me');
  });

  it('renames a category', async () => {
    const writes = serve();
    const { user } = await open();

    await menu(user, 'Loisirs', 'Renommer');
    const dialog = await screen.findByRole('dialog', { name: 'Renommer Loisirs' });
    const field = within(dialog).getByRole('textbox', { name: 'Nom' });
    await user.clear(field);
    await user.type(field, 'Sorties');
    await user.click(within(dialog).getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(writes).toEqual([['PATCH', 'leisure', { name: 'Sorties' }]]));
    expect(await screen.findByText('Catégorie renommée.')).toBeInTheDocument();
    expect(within(screen.getByRole('region', { name: 'Dépenses' })).getByRole('table')).toHaveTextContent('Sorties');
  });

  it('offers « Nom par défaut » only once a seeded category was renamed', async () => {
    const writes = serve();
    const { user } = await open();

    await user.click(screen.getAllByRole('button', { name: 'Actions sur la catégorie Alimentation' })[0]!);
    expect(await screen.findByRole('menuitem', { name: 'Renommer' })).toBeInTheDocument();
    expect(screen.queryByRole('menuitem', { name: 'Nom par défaut' })).toBeNull();
    await user.keyboard('{Escape}');

    await unfold(user);
    await menu(user, 'Alimentation › Courses du mois', 'Nom par défaut');
    await waitFor(() => expect(writes).toEqual([['PATCH', 'groceries', { resetName: true }]]));
  });

  it('merges a category into another of its kind after a confirmation', async () => {
    const writes = serve();
    const { user } = await open();

    await unfold(user);
    await menu(user, 'Alimentation › Restaurants & cafés', 'Fusionner avec…');
    const dialog = await screen.findByRole('dialog', { name: 'Fusionner Restaurants & cafés' });
    await user.click(within(dialog).getByRole('combobox', { name: 'Fusionner dans' }));
    const list = await screen.findByRole('listbox');
    // Same kind, in use, not itself: neither the income nor the archived ones.
    expect(within(list).getAllByRole('option').map((o) => o.textContent)).toEqual(['Alimentation', 'Alimentation › Courses du mois', 'Loisirs', 'Santé']);
    await user.click(within(list).getByRole('option', { name: 'Alimentation › Courses du mois' }));
    await user.click(within(dialog).getByRole('button', { name: 'Fusionner' }));
    expect(await screen.findByText(/passent dans « Alimentation › Courses du mois », et « Restaurants & cafés » est archivée/)).toBeInTheDocument();
    expect(writes).toEqual([]);
    await user.click(screen.getByRole('button', { name: 'Confirmer' }));

    await waitFor(() => expect(writes).toEqual([['MERGE', 'restaurants', { into: 'groceries' }]]));
    await waitFor(() => expect(screen.queryByText('Restaurants & cafés')).toBeNull());
  });

  it('a parent merges only into a top-level category', async () => {
    serve();
    const { user } = await open();

    await menu(user, 'Alimentation', 'Fusionner avec…');
    await user.click(await screen.findByRole('combobox', { name: 'Fusionner dans' }));
    expect(within(await screen.findByRole('listbox')).getAllByRole('option').map((o) => o.textContent)).toEqual(['Loisirs', 'Santé']);
  });

  it('archives a category under « Archivées », and brings it back', async () => {
    const writes = serve();
    const { user } = await open();

    await menu(user, 'Loisirs', 'Archiver');
    await waitFor(() => expect(within(screen.getByRole('region', { name: 'Dépenses' })).queryByText('Loisirs')).toBeNull());
    await user.click(screen.getByRole('button', { name: 'Archivées' }));
    const archived = screen.getByRole('table', { name: 'Archivées' });
    expect(archived).toHaveTextContent('Loisirs');
    expect(archived).toHaveTextContent('Alimentation › Marché du dimanche');

    await menu(user, 'Loisirs', 'Réactiver');
    await waitFor(() => expect(writes).toEqual([['PATCH', 'leisure', { archived: true }], ['PATCH', 'leisure', { archived: false }]]));
    await waitFor(() => expect(within(screen.getByRole('region', { name: 'Dépenses' })).getByRole('table', { name: 'Dépenses' })).toHaveTextContent('Loisirs'));
  });

  it('creates a category under a parent, or a top-level one of a kind', async () => {
    const writes = serve();
    const { user } = await open();

    await user.click(screen.getByRole('button', { name: 'Nouvelle catégorie' }));
    let dialog = await screen.findByRole('dialog', { name: 'Nouvelle catégorie' });
    await user.type(within(dialog).getByRole('textbox', { name: 'Nom' }), 'Animaux');
    await user.click(within(dialog).getByRole('radio', { name: 'Revenus' }));
    await user.click(within(dialog).getByRole('button', { name: 'Créer' }));
    await waitFor(() => expect(writes).toEqual([['POST', 'categories', { name: 'Animaux', kind: 'income' }]]));
    expect(await screen.findByText('Catégorie Animaux créée.')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Nouvelle catégorie' }));
    dialog = await screen.findByRole('dialog', { name: 'Nouvelle catégorie' });
    await user.type(within(dialog).getByRole('textbox', { name: 'Nom' }), 'Boulangerie');
    await pick(user, 'Dans la catégorie', 'Alimentation');
    expect(within(dialog).queryByRole('radiogroup')).toBeNull();
    await user.click(within(dialog).getByRole('button', { name: 'Créer' }));
    await waitFor(() => expect(writes[1]).toEqual(['POST', 'categories', { name: 'Boulangerie', parentId: 'food' }]));
  });

  it('shows a refused name under the field', async () => {
    serve();
    const { user } = await open();

    await user.click(screen.getByRole('button', { name: 'Nouvelle catégorie' }));
    const dialog = await screen.findByRole('dialog', { name: 'Nouvelle catégorie' });
    const field = within(dialog).getByRole('textbox', { name: 'Nom' });
    await user.type(field, 'Courses');
    await user.click(within(dialog).getByRole('button', { name: 'Créer' }));

    await waitFor(() => expect(field).toHaveAccessibleDescription('Un nom compte de 1 à 60 caractères, différent de celui des catégories voisines.'));
    expect(field).toHaveAttribute('aria-invalid', 'true');
    expect(within(dialog).queryByRole('alert')).toBeNull();
  });
});
