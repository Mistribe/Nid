import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { toasts } from '@/test/toasts';

type Body = Record<string, unknown>;

const ALICE = '01926b9e-0000-7000-8000-00000000000a';
const BOB = '01926b9e-0000-7000-8000-00000000000b';
const LEA = '01926b9e-0000-7000-8000-00000000000c';

const alice = { id: ALICE, kind: 'adult', displayName: 'Alice Martin', email: 'alice@example.test', birthDate: '1984-05-20', retirementAge: 64, retirementMonths: 0, age: 41, retirementOn: '2048-05', signedIn: true };
const bob = { id: BOB, kind: 'adult', displayName: 'Bob Martin', email: 'bob@example.test', birthDate: null, retirementAge: 62, retirementMonths: 9, age: null, retirementOn: null, signedIn: false };
const lea = { id: LEA, kind: 'dependent', displayName: 'Léa Martin', email: null, birthDate: '2020-03-01', retirementAge: 67, retirementMonths: 0, age: 5, retirementOn: null, signedIn: false };

function serve() {
  const persons: Body[] = [alice, bob, lea];
  const sent: { method: string; url: string; body?: unknown }[] = [];
  server.use(
    http.get('*/api/v1/core/persons', () => HttpResponse.json(persons)),
    http.put('*/api/v1/core/persons/:id/retirement', async ({ request, params }) => {
      const body = await request.json();
      sent.push({ method: 'PUT', url: `retirement/${String(params.id)}`, body });
      return HttpResponse.json({ ...persons.find((p) => p.id === params.id), ...(body as Body) });
    }),
  );
  return { persons, sent };
}

const table = async () => screen.findByRole('table', { name: 'Personnes' });
/** A person's row, its cells' text. */
const cells = async (name: string) => within(within(await table()).getByRole('row', { name: new RegExp(`^${name}`) })).getAllByRole('cell').map((c) => c.textContent);

async function openMenu(user: Awaited<ReturnType<typeof renderApp>>['user'], name: string) {
  await user.click(within(await table()).getByRole('button', { name: `Actions pour ${name}` }));
}

describe('Réglages › Personnes', () => {
  it('lists each person with role, birth date, age, retirement and sign-in', async () => {
    serve();
    await renderApp('/settings/persons');

    // Its own tab under the « Réglages » title (issue 387).
    expect(await screen.findByRole('heading', { level: 1, name: 'Réglages' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Personnes' })).toHaveAttribute('aria-current', 'page');
    expect(screen.getByText(/propriétaires des biens et des comptes/)).toBeInTheDocument();
    expect(await cells('Alice Martin')).toEqual(['Alice Martin', 'Adulte', '20/05/1984', '41 ans', '64 ans · mai 2048', 'Compte actif', '']);
    expect(await cells('Bob Martin')).toEqual(['Bob Martin', 'Adulte', '—', '—', '62 ans et 9 mois', 'Invitation en attente', '']);
    expect(await cells('Léa Martin')).toEqual(['Léa Martin', 'Enfant', '01/03/2020', '5 ans', '—', 'Pas de compte', '']);
  });

  it('offers the birth date and retirement for an adult, and rename and removal for a child only', async () => {
    serve();
    const { user } = await renderApp('/settings/persons');

    await openMenu(user, 'Alice Martin');
    expect((await screen.findAllByRole('menuitem')).map((i) => i.textContent)).toEqual(['Date de naissance et retraite']);
    await user.keyboard('{Escape}');
    await openMenu(user, 'Léa Martin');
    expect((await screen.findAllByRole('menuitem')).map((i) => i.textContent)).toEqual(['Date de naissance', 'Renommer', 'Retirer du foyer']);
  });

  it('sets an adult’s birth date and retirement age, its fields explained', async () => {
    const { sent } = serve();
    const { user } = await renderApp('/settings/persons');

    await openMenu(user, 'Bob Martin');
    await user.click(await screen.findByRole('menuitem', { name: 'Date de naissance et retraite' }));
    const dialog = await screen.findByRole('dialog', { name: 'Retraite de Bob Martin' });
    expect(within(dialog).getAllByRole('button', { name: /^À propos : / }).map((b) => b.getAttribute('aria-label')))
      .toEqual(['À propos : Date de naissance', 'À propos : Âge de départ à la retraite']);
    await user.type(within(dialog).getByLabelText('Date de naissance'), '20/05/1984');
    const age = within(dialog).getByLabelText('Âge de départ à la retraite');
    await user.clear(age);
    await user.type(age, '64');
    await user.clear(within(dialog).getByLabelText('Mois'));
    await user.click(within(dialog).getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(sent).toEqual([{ method: 'PUT', url: `retirement/${BOB}`, body: { birthDate: '1984-05-20', retirementAge: 64, retirementMonths: 0 } }]));
    await waitFor(() => expect(toasts()).toHaveTextContent('Retraite de Bob Martin enregistrée.'));
  });

  it('shows a server error on the months field', async () => {
    serve();
    server.use(http.put('*/api/v1/core/persons/:id/retirement', () => HttpResponse.json(
      { status: 422, code: 'core.invalid_retirement_age', errors: [{ path: 'retirementMonths', code: 'core.invalid_retirement_age', message: 'Mois hors limites.' }] },
      { status: 422, headers: { 'content-type': 'application/problem+json' } })));
    const { user } = await renderApp('/settings/persons');

    await openMenu(user, 'Alice Martin');
    await user.click(await screen.findByRole('menuitem', { name: 'Date de naissance et retraite' }));
    const dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(within(dialog).getByLabelText('Mois')).toHaveAccessibleDescription('Mois hors limites.'));
  });

  it('sets a child’s birth date alone and sends the stored retirement back', async () => {
    const { sent } = serve();
    const { user } = await renderApp('/settings/persons');

    await openMenu(user, 'Léa Martin');
    await user.click(await screen.findByRole('menuitem', { name: 'Date de naissance' }));
    const dialog = await screen.findByRole('dialog', { name: 'Date de naissance de Léa Martin' });
    expect(within(dialog).queryByLabelText('Âge de départ à la retraite')).not.toBeInTheDocument();
    const date = within(dialog).getByLabelText('Date de naissance');
    await user.clear(date);
    await user.type(date, '02/03/2020');
    await user.click(within(dialog).getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(sent).toEqual([{ method: 'PUT', url: `retirement/${LEA}`, body: { birthDate: '2020-03-02', retirementAge: 67, retirementMonths: 0 } }]));
    await waitFor(() => expect(toasts()).toHaveTextContent('Date de naissance de Léa Martin enregistrée.'));
  });

  it('renames a child, and shows a refused name on its field', async () => {
    const { persons } = serve();
    const names: unknown[] = [];
    server.use(http.put('*/api/v1/core/persons/dependents/:id/name', async ({ request }) => {
      const body = (await request.json()) as { displayName: string };
      names.push(body);
      if (body.displayName.trim() === '') return HttpResponse.json(
        { status: 422, code: 'core.name_required', title: 'A name is required.', errors: [{ path: 'displayName', code: 'core.name_required', message: 'A name is required.' }] },
        { status: 422, headers: { 'content-type': 'application/problem+json' } });
      persons[2] = { ...lea, displayName: body.displayName };
      return HttpResponse.json(persons[2]);
    }));
    const { user } = await renderApp('/settings/persons');

    await openMenu(user, 'Léa Martin');
    await user.click(await screen.findByRole('menuitem', { name: 'Renommer' }));
    const dialog = await screen.findByRole('dialog', { name: 'Renommer Léa Martin' });
    const name = within(dialog).getByLabelText('Prénom et nom');
    expect(name).toHaveValue('Léa Martin');
    await user.clear(name);
    await user.click(within(dialog).getByRole('button', { name: 'Enregistrer' }));
    await waitFor(() => expect(name).toHaveAccessibleDescription('A name is required.'));

    await user.type(name, 'Léa Dupont');
    await user.click(within(dialog).getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(toasts()).toHaveTextContent('Nom enregistré : Léa Dupont.'));
    expect(names).toEqual([{ displayName: '' }, { displayName: 'Léa Dupont' }]);
    expect(await within(await table()).findByText('Léa Dupont')).toBeInTheDocument();
  });

  it('removes a child after a confirmation', async () => {
    const { persons } = serve();
    let deleted = '';
    server.use(http.delete('*/api/v1/core/persons/dependents/:id', ({ params }) => {
      deleted = String(params.id);
      persons.splice(2, 1);
      return new HttpResponse(null, { status: 204 });
    }));
    const { user } = await renderApp('/settings/persons');

    await openMenu(user, 'Léa Martin');
    await user.click(await screen.findByRole('menuitem', { name: 'Retirer du foyer' }));
    expect(await screen.findByText(/Léa Martin ne fera plus partie du foyer/)).toBeInTheDocument();
    expect(deleted).toBe('');
    await user.click(screen.getByRole('button', { name: 'Confirmer' }));

    await waitFor(() => expect(deleted).toBe(LEA));
    await waitFor(() => expect(toasts()).toHaveTextContent('Léa Martin ne fait plus partie du foyer.'));
    await waitFor(() => expect(within(screen.getByRole('table', { name: 'Personnes' })).queryByText('Léa Martin')).not.toBeInTheDocument());
  });

  it('says in plain words what still keeps a child in the household', async () => {
    serve();
    server.use(http.delete('*/api/v1/core/persons/dependents/:id', () => HttpResponse.json(
      { status: 409, code: 'core.person_in_use', title: 'Léa Martin is still named elsewhere: ownerships, childcare.', uses: ['ownerships', 'childcare'] },
      { status: 409, headers: { 'content-type': 'application/problem+json' } })));
    const { user } = await renderApp('/settings/persons');

    await openMenu(user, 'Léa Martin');
    await user.click(await screen.findByRole('menuitem', { name: 'Retirer du foyer' }));
    await user.click(await screen.findByRole('button', { name: 'Confirmer' }));

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Impossible de retirer Léa Martin du foyer : il reste des biens ou des comptes et un contrat de garde à son nom. Changez-les d’abord.');
    expect(within(await table()).getByText('Léa Martin')).toBeInTheDocument();
  });

  it('adds a child with a birth date', async () => {
    const { persons } = serve();
    const sent: unknown[] = [];
    server.use(http.post('*/api/v1/core/persons/dependents', async ({ request }) => {
      const body = (await request.json()) as { displayName: string; birthDate: string | null };
      sent.push(body);
      const child = { ...lea, id: '01926b9e-0000-7000-8000-00000000000d', displayName: body.displayName, birthDate: body.birthDate, age: 1 };
      persons.push(child);
      return HttpResponse.json(child, { status: 201 });
    }));
    const { user } = await renderApp('/settings/persons');

    await user.click(await screen.findByRole('button', { name: 'Ajouter un enfant' }));
    const dialog = screen.getByRole('dialog', { name: 'Ajouter un enfant' });
    await user.type(within(dialog).getByLabelText('Prénom et nom'), 'Léo Martin');
    await user.type(within(dialog).getByLabelText('Date de naissance (facultatif)'), '15/06/2024');
    await user.click(within(dialog).getByRole('button', { name: 'Ajouter' }));

    await waitFor(() => expect(toasts()).toHaveTextContent('Léo Martin fait maintenant partie du foyer.'));
    expect(sent).toEqual([{ displayName: 'Léo Martin', birthDate: '2024-06-15' }]);
    expect(await cells('Léo Martin')).toEqual(['Léo Martin', 'Enfant', '15/06/2024', '1 an', '—', 'Pas de compte', '']);
  });

  it('adds a child without a birth date, and shows the API problem when the name is refused', async () => {
    serve();
    const sent: unknown[] = [];
    server.use(http.post('*/api/v1/core/persons/dependents', async ({ request }) => {
      sent.push(await request.json());
      return HttpResponse.json({ status: 400, code: 'core.name_required', title: 'A name is required.', traceId: '00-t-24' }, { status: 400 });
    }));
    const { user } = await renderApp('/settings/persons');

    await user.click(await screen.findByRole('button', { name: 'Ajouter un enfant' }));
    const dialog = screen.getByRole('dialog', { name: 'Ajouter un enfant' });
    await user.click(within(dialog).getByRole('button', { name: 'Ajouter' }));

    expect(await within(dialog).findByRole('alert')).toHaveTextContent('A name is required.');
    expect(sent).toEqual([{ displayName: '', birthDate: null }]);
    expect(screen.getByRole('dialog', { name: 'Ajouter un enfant' })).toBeInTheDocument();
  });

  it('has no axe violations', async () => {
    serve();
    const { container } = await renderApp('/settings/persons');
    await cells('Alice Martin');
    await expectNoAxeViolations(container);
  });
});
