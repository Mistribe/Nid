import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it } from 'vitest';
import { pick } from '@/test/controls';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { persons as adults } from '../subscriptions/testData';
import { ARRANGEMENT_ID, CHILD, detail } from './testData';

const persons = [...adults, { id: CHILD, kind: 'dependent', displayName: 'Léa', email: null }];

function serve(bodies: unknown[], answer: () => Response = () => HttpResponse.json({ id: ARRANGEMENT_ID }, { status: 201 })) {
  server.use(
    http.get('*/api/v1/core/persons', () => HttpResponse.json(persons)),
    http.post('*/api/v1/childcare', async ({ request }) => { bodies.push(await request.json()); return answer(); }),
    http.get('*/api/v1/childcare/:id', () => HttpResponse.json(detail())),
  );
}

type User = Awaited<ReturnType<typeof renderApp>>['user'];
const next = (user: User) => user.click(screen.getByRole('button', { name: 'Continuer' }));
const submit = (user: User) => user.click(screen.getByRole('button', { name: 'Créer la garde' }));
const step = () => screen.getByRole('listitem', { current: 'step' });

async function fillUpToSchedule(user: User) {
  await user.type(await screen.findByLabelText('Nom'), 'Garde de Léa');
  await pick(user, 'Enfant', 'Léa');
  expect(screen.queryByRole('option', { name: 'Alice Dev' })).not.toBeInTheDocument();
  await user.type(screen.getByLabelText('Assistante maternelle'), 'Martine');
  await next(user);
  expect(step()).toHaveTextContent('Tarifs');
  await user.type(screen.getByLabelText('Début du contrat'), '01/09/2025');
  await user.type(screen.getByLabelText('Taux horaire net (€)'), '4,30');
  await user.type(screen.getByLabelText('Entretien pour 9 h (€)'), '3,80');
  await user.type(screen.getByLabelText('Prix du repas (€)'), '3,20');
  await next(user);
  expect(step()).toHaveTextContent('Planning');
}

beforeAll(async () => { await import('./NewChildcare'); }, 30_000);

describe('NewChildcare', () => {
  it('creates with the fixture and opens the detail', async () => {
    const bodies: unknown[] = [];
    serve(bodies);
    const { user, router } = await renderApp('/childcare/new');
    await fillUpToSchedule(user);
    for (const day of ['Lundi', 'Mardi', 'Jeudi', 'Vendredi']) await user.click(screen.getByRole('checkbox', { name: day }));
    await next(user);
    expect(step()).toHaveTextContent('Semaines sans accueil');
    await next(user);
    expect(step()).toHaveTextContent('Détenteurs');
    await submit(user);

    await waitFor(() => expect(router.state.location.pathname).toBe(`/childcare/${ARRANGEMENT_ID}`));
    const slot = (day: number) => ({ day, arrival: '08:00', departure: '17:30', meal: true });
    expect(bodies).toEqual([{
      name: 'Garde de Léa', childId: CHILD, carer: { name: 'Martine' },
      terms: { from: '2025-09-01', netHourlyRate: '4.30', overtimePct: '10', upkeepPer9h: '3.80', mealPrice: '3.20',
        weekTypes: [{ name: 'Semaine type', weeks: 46, schedule: [1, 2, 4, 5].map(slot) }] },
      gaps: [], visibility: 'shared', owners: [{ personId: '01926b9e-0000-7000-8000-00000000000a', shareBp: 10000 }],
    }]);
  });

  it('continuing with no weekday checked is refused', async () => {
    serve([]);
    const { user } = await renderApp('/childcare/new');
    await fillUpToSchedule(user);
    await next(user);
    expect(step()).toHaveTextContent('Planning');
    expect(screen.getByText('Cochez au moins un jour d’accueil.')).toBeInTheDocument();
  });

  it('a server schedule error returns to the schedule step', async () => {
    serve([], () => HttpResponse.json({
      type: 'about:blank', title: 'Unprocessable', status: 422, code: 'request.invalid',
      errors: [{ path: 'terms.weekTypes[0].schedule[0].departure', code: 'childcare.departure_before_arrival', message: 'The departure comes after the arrival.' }],
    }, { status: 422 }));
    const { user } = await renderApp('/childcare/new');
    await fillUpToSchedule(user);
    await user.click(screen.getByRole('checkbox', { name: 'Lundi' }));
    await next(user);
    await next(user);
    await submit(user);
    await waitFor(() => expect(step()).toHaveTextContent('Planning'));
    expect(screen.getByText('Le départ vient après l’arrivée.')).toBeInTheDocument();
  });

  it('a single week type shows no name and no remove button', async () => {
    serve([]);
    const { user } = await renderApp('/childcare/new');
    await fillUpToSchedule(user);
    expect(screen.queryByLabelText('Nom de la semaine type 1')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Retirer la semaine type/ })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Ajouter une semaine type' })).toBeInTheDocument();
  });

  it('creates with two week types', async () => {
    const bodies: unknown[] = [];
    serve(bodies);
    const { user, router } = await renderApp('/childcare/new');
    await fillUpToSchedule(user);
    for (const day of ['Lundi', 'Mardi', 'Jeudi', 'Vendredi']) await user.click(screen.getByRole('checkbox', { name: day }));
    await user.click(screen.getByRole('button', { name: 'Ajouter une semaine type' }));
    const second = screen.getByRole('group', { name: 'Semaine type 2' });
    await user.clear(screen.getByLabelText('Nom de la semaine type 1'));
    await user.type(screen.getByLabelText('Nom de la semaine type 1'), 'Semaine longue');
    await user.clear(screen.getByLabelText('Semaines de la semaine type 1'));
    await user.type(screen.getByLabelText('Semaines de la semaine type 1'), '23');
    await user.type(within(second).getByLabelText('Nom de la semaine type 2'), 'Semaine courte');
    await user.type(within(second).getByLabelText('Semaines de la semaine type 2'), '22');
    await user.clear(within(second).getByLabelText('Départ (Mardi)'));
    await user.type(within(second).getByLabelText('Départ (Mardi)'), '16:45');
    expect(screen.getByText('45 semaines programmées')).toBeInTheDocument();
    await next(user);
    await next(user);
    await submit(user);

    await waitFor(() => expect(router.state.location.pathname).toBe(`/childcare/${ARRANGEMENT_ID}`));
    const slot = (day: number, departure = '17:30') => ({ day, arrival: '08:00', departure, meal: true });
    expect((bodies[0] as { terms: { weekTypes: unknown } }).terms.weekTypes).toEqual([
      { name: 'Semaine longue', weeks: 23, schedule: [slot(1), slot(2), slot(4), slot(5)] },
      { name: 'Semaine courte', weeks: 22, schedule: [slot(1), slot(2, '16:45'), slot(4), slot(5)] },
    ]);
  });

  it('a server error on the second week type lands on its Tuesday', async () => {
    serve([], () => HttpResponse.json({
      type: 'about:blank', title: 'Unprocessable', status: 422, code: 'request.invalid',
      errors: [{ path: 'terms.weekTypes[1].schedule[1].departure', code: 'childcare.departure_before_arrival', message: 'The departure comes after the arrival.' }],
    }, { status: 422 }));
    const { user } = await renderApp('/childcare/new');
    await fillUpToSchedule(user);
    for (const day of ['Lundi', 'Mardi', 'Jeudi', 'Vendredi']) await user.click(screen.getByRole('checkbox', { name: day }));
    await user.click(screen.getByRole('button', { name: 'Ajouter une semaine type' }));
    const second = screen.getByRole('group', { name: 'Semaine type 2' });
    await user.clear(screen.getByLabelText('Nom de la semaine type 1'));
    await user.type(screen.getByLabelText('Nom de la semaine type 1'), 'Longue');
    await user.clear(screen.getByLabelText('Semaines de la semaine type 1'));
    await user.type(screen.getByLabelText('Semaines de la semaine type 1'), '23');
    await user.type(within(second).getByLabelText('Nom de la semaine type 2'), 'Courte');
    await user.type(within(second).getByLabelText('Semaines de la semaine type 2'), '22');
    await next(user);
    await next(user);
    await submit(user);
    await waitFor(() => expect(step()).toHaveTextContent('Planning'));
    expect(within(screen.getByRole('group', { name: 'Semaine type 2' })).getByText('Le départ vient après l’arrivée.')).toBeInTheDocument();
  });

  it('removing the second type sends the first one\'s weeks back to the rates step, validated there', async () => {
    serve([]);
    const { user } = await renderApp('/childcare/new');
    await fillUpToSchedule(user);
    await user.click(screen.getByRole('checkbox', { name: 'Lundi' }));
    await user.click(screen.getByRole('button', { name: 'Ajouter une semaine type' }));
    await user.clear(screen.getByLabelText('Semaines de la semaine type 1'));
    await user.type(screen.getByLabelText('Semaines de la semaine type 1'), '30');
    await user.click(screen.getByRole('button', { name: 'Retirer la semaine type 2' }));
    expect(screen.queryByLabelText('Nom de la semaine type 1')).not.toBeInTheDocument();
    expect(screen.getByRole('checkbox', { name: 'Lundi' })).toBeChecked();
    await user.click(screen.getByRole('button', { name: 'Retour' }));
    expect(step()).toHaveTextContent('Tarifs');
    const weeks = screen.getByLabelText('Semaines programmées');
    expect(weeks).toHaveValue('30');
    await user.clear(weeks);
    await user.type(weeks, '47');
    await next(user);
    expect(step()).toHaveTextContent('Tarifs');
    expect(screen.getByText('Entre 1 et 46 semaines.')).toBeInTheDocument();
  });

  it('with two types, a blank weeks field blocks Next on the schedule step and shows its error', async () => {
    serve([]);
    const { user } = await renderApp('/childcare/new');
    await fillUpToSchedule(user);
    await user.click(screen.getByRole('checkbox', { name: 'Lundi' }));
    await user.click(screen.getByRole('button', { name: 'Ajouter une semaine type' }));
    await user.type(screen.getByLabelText('Nom de la semaine type 2'), 'Courte');
    await user.type(screen.getByLabelText('Semaines de la semaine type 2'), '10');
    // The first weeks are 46 (valid) and visible here; blank the second: the error shows on this step.
    await user.clear(screen.getByLabelText('Semaines de la semaine type 2'));
    await next(user);
    expect(step()).toHaveTextContent('Planning');
    expect(screen.getByText('Entre 1 et 46 semaines.')).toBeVisible();
  });
});
