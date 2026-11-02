import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { pick } from '@/test/controls';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { persons } from '../subscriptions/testData';
import { todayIso } from '../subscriptions/today';
import { detail, SALARY } from './testData';

function serveCreate(bodies: unknown[], answer: () => Response = () => HttpResponse.json({ id: SALARY }, { status: 201 })) {
  server.use(
    http.get('*/api/v1/core/persons', () => HttpResponse.json(persons)),
    http.post('*/api/v1/income/sources', async ({ request }) => { bodies.push(await request.json()); return answer(); }),
    http.get('*/api/v1/income/sources/:id', () => HttpResponse.json(detail())),
  );
}

type User = Awaited<ReturnType<typeof renderApp>>['user'];
const line = (n: number) => screen.getByRole('group', { name: `Composante ${n}` });
const next = (user: User) => user.click(screen.getByRole('button', { name: 'Continuer' }));
const submit = (user: User) => user.click(screen.getByRole('button', { name: 'Créer le revenu' }));
const step = () => screen.getByRole('listitem', { current: 'step' });
/** The cadence of one component line (two lines each have a « Fréquence »). */
async function cadence(user: User, n: number, option: string) {
  await user.click(within(line(n)).getByRole('combobox', { name: 'Fréquence' }));
  await user.click(await within(await screen.findByRole('listbox')).findByRole('option', { name: option }));
}

/** The first step, answered; the visibility is left unanswered when `visibility` is null. */
async function essentials(user: User, visibility: string | null = 'Oui, partagé') {
  await user.type(await screen.findByLabelText('Nom'), 'Salaire Alice');
  if (visibility) await user.click(screen.getByRole('radio', { name: visibility }));
  await next(user);
}

// Lazy chunks load cold on the first render; warm them so no findBy races its 1 s default.
beforeAll(async () => { await import('./NewIncomeSource'); }, 30_000);

const frDate = (iso: string) => iso.split('-').reverse().join('/');

describe('NewIncomeSource', () => {
  it('creates the same income body as before, two components, through two steps, then shows it', async () => {
    const bodies: unknown[] = [];
    serveCreate(bodies);
    const { user, router, container } = await renderApp('/income/new');

    await user.type(await screen.findByLabelText('Nom'), 'Salaire Alice');
    await pick(user, 'Type', 'Salaire');
    await user.type(screen.getByLabelText(/Payeur/), 'Employeur');
    await user.click(screen.getByRole('radio', { name: 'Non, privé' }));
    await next(user);
    expect(step()).toHaveTextContent('Les composantes');
    expect(within(line(1)).getByLabelText('Libellé')).toHaveValue('Salaire de base');
    expect(within(line(1)).queryByLabelText('Montant brut')).not.toBeInTheDocument();
    await user.type(within(line(1)).getByLabelText('Montant net'), '2300,50');
    await user.clear(within(line(1)).getByLabelText('Début'));
    await user.type(within(line(1)).getByLabelText('Début'), '01/01/2025');
    await user.click(screen.getByRole('button', { name: 'Ajouter une composante' }));
    await user.type(within(line(2)).getByLabelText('Libellé'), 'Prime annuelle');
    await user.type(within(line(2)).getByLabelText('Montant net'), '1800');
    await cadence(user, 2, 'Chaque année');
    await user.clear(within(line(2)).getByLabelText('Début'));
    await user.type(within(line(2)).getByLabelText('Début'), '15/12/2024');
    await expectNoAxeViolations(container);
    await submit(user);

    await waitFor(() => expect(router.state.location.pathname).toBe(`/income/${SALARY}`));
    expect(bodies).toEqual([{
      displayName: 'Salaire Alice', kind: 'salary', currency: 'EUR', visibility: 'private', payerLabel: 'Employeur',
      owners: [{ personId: '01926b9e-0000-7000-8000-00000000000a', shareBp: 10000 }],
      phases: [
        { label: 'Salaire de base', startsOn: '2025-01-01', netAmount: { amount: '2300.50', currency: 'EUR' }, every: { count: 1, unit: 'month' } },
        { label: 'Prime annuelle', startsOn: '2024-12-15', netAmount: { amount: '1800', currency: 'EUR' }, every: { count: 1, unit: 'year' } },
      ],
    }]);
  });

  it('stays on the first step with an invalid field, then asks the visibility question with no answer checked', async () => {
    const bodies: unknown[] = [];
    serveCreate(bodies);
    const { user } = await renderApp('/income/new');

    await essentials(user, null);
    expect(step()).toHaveTextContent('La source');
    expect(screen.getByRole('radiogroup', { name: 'Ce revenu est-il visible de l’autre adulte ?' })).toBeInTheDocument();
    expect(screen.getByRole('radio', { name: 'Oui, partagé' })).not.toBeChecked();
    expect(screen.getByRole('radio', { name: 'Non, privé' })).not.toBeChecked();
    expect(await screen.findByText('Répondez à cette question.')).toBeInTheDocument();
    expect(screen.getByLabelText('Nom')).toHaveValue('Salaire Alice');
    expect(bodies).toEqual([]);
  });

  it('folds the currency and the note, and a component’s gross amount, number of payments and note', async () => {
    const bodies: unknown[] = [];
    serveCreate(bodies);
    const { user } = await renderApp('/income/new');

    expect(screen.queryByLabelText('Note')).not.toBeInTheDocument();
    await essentials(user);
    expect(within(line(1)).getByRole('button', { name: 'Plus d’options' })).toHaveAttribute('aria-expanded', 'false');
    await user.type(within(line(1)).getByLabelText('Montant net'), '2300');
    await user.click(within(line(1)).getByRole('button', { name: 'Plus d’options' }));
    await user.type(within(line(1)).getByLabelText('Montant brut'), '3000');
    await user.type(within(line(1)).getByLabelText('Nombre de versements'), '12');
    await submit(user);

    await waitFor(() => expect(bodies).toHaveLength(1));
    expect(bodies[0]).toMatchObject({ phases: [expect.objectContaining({ grossAmount: { amount: '3000', currency: 'EUR' }, maxCycles: 12 })] });
  });

  it('removes a component, even the last one: a source may start without any', async () => {
    const bodies: unknown[] = [];
    serveCreate(bodies);
    const { user, router } = await renderApp('/income/new');

    await essentials(user);
    await user.click(screen.getByRole('button', { name: 'Ajouter une composante' }));
    await user.click(screen.getByRole('button', { name: 'Retirer la composante 2' }));
    await user.click(screen.getByRole('button', { name: 'Retirer la composante 1' }));
    expect(screen.queryByRole('group', { name: /Composante/ })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Ajouter une composante' })).toHaveFocus();
    await submit(user);

    await waitFor(() => expect(router.state.location.pathname).toBe(`/income/${SALARY}`));
    expect(bodies).toEqual([expect.objectContaining({ visibility: 'shared', phases: [] })]);
  });

  it('stays on the components step with an invalid field, and refuses two overlapping lines under the later one', async () => {
    const bodies: unknown[] = [];
    serveCreate(bodies);
    const { user } = await renderApp('/income/new');

    await essentials(user);
    await submit(user);
    expect(step()).toHaveTextContent('Les composantes');
    expect(await within(line(1)).findByLabelText('Montant net')).toHaveAttribute('aria-invalid', 'true');
    await user.type(within(line(1)).getByLabelText('Montant net'), '2300');
    await user.clear(within(line(1)).getByLabelText('Début'));
    await user.type(within(line(1)).getByLabelText('Début'), '01/01/2025');
    await user.click(screen.getByRole('button', { name: 'Ajouter une composante' }));
    await user.type(within(line(2)).getByLabelText('Libellé'), 'Salaire de base');
    await user.type(within(line(2)).getByLabelText('Montant net'), '2400');
    await user.clear(within(line(2)).getByLabelText('Début'));
    await user.type(within(line(2)).getByLabelText('Début'), '01/06/2025');
    await submit(user);

    expect(await within(line(2)).findByText('Une autre période de ce libellé couvre déjà une partie de ces dates.')).toBeInTheDocument();
    expect(within(line(1)).queryByText('Une autre période de ce libellé couvre déjà une partie de ces dates.')).not.toBeInTheDocument();
    expect(bodies).toEqual([]);
  });

  it('shows a server refusal under the line it names, and keeps the visibility answered', async () => {
    serveCreate([], () => HttpResponse.json(
      { status: 422, code: 'income.phase_overlap', title: 'x', errors: [{ path: 'phases[1].startsOn', code: 'income.phase_overlap', message: 'x' }] },
      { status: 422 }));
    const { user } = await renderApp('/income/new');

    await essentials(user, 'Non, privé');
    await user.type(within(line(1)).getByLabelText('Montant net'), '2300');
    await user.click(screen.getByRole('button', { name: 'Ajouter une composante' }));
    await user.type(within(line(2)).getByLabelText('Libellé'), 'Prime');
    await user.type(within(line(2)).getByLabelText('Montant net'), '100');
    await submit(user);

    expect(await within(line(2)).findByText('Une autre période de ce libellé couvre déjà une partie de ces dates.')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Retour' }));
    expect(screen.getByRole('radio', { name: 'Non, privé' })).toBeChecked();
  });

  it('sends the person back to the first step when the server refuses a field of it', async () => {
    serveCreate([], () => HttpResponse.json(
      { status: 422, code: 'income.invalid_source', title: 'x', errors: [{ path: 'payerLabel', code: 'income.invalid_source', message: 'x' }] },
      { status: 422 }));
    const { user } = await renderApp('/income/new');

    await essentials(user);
    await user.type(within(line(1)).getByLabelText('Montant net'), '2300');
    await submit(user);

    await waitFor(() => expect(step()).toHaveTextContent('La source'));
    expect(await screen.findByLabelText(/Payeur/)).toHaveAttribute('aria-invalid', 'true');
  });

  it('shows a refusal no field owns above the form', async () => {
    serveCreate([], () => HttpResponse.json({ status: 422, code: 'income.invalid_source', title: 'x' }, { status: 422 }));
    const { user } = await renderApp('/income/new');

    await essentials(user);
    await user.type(within(line(1)).getByLabelText('Montant net'), '2300');
    await submit(user);

    expect(await screen.findByRole('alert')).toBeInTheDocument();
  });

  it('asks for « Tous les N mois » with N between 2 and 12, and starts a new line today', async () => {
    const bodies: unknown[] = [];
    serveCreate(bodies);
    const { user } = await renderApp('/income/new');

    await essentials(user);
    expect(within(line(1)).getByLabelText('Début')).toHaveValue(frDate(todayIso()));
    await user.type(within(line(1)).getByLabelText('Montant net'), '600');
    await cadence(user, 1, 'Tous les N mois');
    await user.type(within(line(1)).getByLabelText('Nombre de mois'), '13');
    await submit(user);

    expect(await within(line(1)).findByText('Entre 2 et 12 mois.')).toBeInTheDocument();
    expect(bodies).toEqual([]);
  });
});
