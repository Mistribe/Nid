import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
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

const line = (n: number) => screen.getByRole('group', { name: `Composante ${n}` });

async function fillName(user: Awaited<ReturnType<typeof renderApp>>['user']) {
  await user.type(await screen.findByLabelText('Nom'), 'Salaire Alice');
}

// Lazy chunks load cold on the first render; warm them so no findBy races its 1 s default.
beforeAll(async () => { await import('./NewIncomeSource'); }, 30_000);

describe('NewIncomeSource', () => {
  it('creates a private salary with two components in one request, then shows it', async () => {
    const bodies: unknown[] = [];
    serveCreate(bodies);
    const { user, router, container } = await renderApp('/income/new');

    await fillName(user);
    await user.type(screen.getByLabelText(/Payeur/), 'Employeur');
    await user.click(screen.getByRole('radio', { name: 'Non, privé' }));
    expect(within(line(1)).getByLabelText('Libellé')).toHaveValue('Salaire de base');
    await user.type(within(line(1)).getByLabelText('Montant net'), '2300,50');
    await user.clear(within(line(1)).getByLabelText('Début'));
    await user.type(within(line(1)).getByLabelText('Début'), '2025-01-01');
    await user.click(screen.getByRole('button', { name: 'Ajouter une composante' }));
    await user.type(within(line(2)).getByLabelText('Libellé'), 'Prime annuelle');
    await user.type(within(line(2)).getByLabelText('Montant net'), '1800');
    await user.selectOptions(within(line(2)).getByLabelText('Fréquence'), 'yearly');
    await user.clear(within(line(2)).getByLabelText('Début'));
    await user.type(within(line(2)).getByLabelText('Début'), '2024-12-15');
    await expectNoAxeViolations(container);
    await user.click(screen.getByRole('button', { name: 'Créer le revenu' }));

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

  it('asks the visibility question with neither answer checked, and refuses to send until it is answered', async () => {
    const bodies: unknown[] = [];
    serveCreate(bodies);
    const { user } = await renderApp('/income/new');

    await fillName(user);
    await user.type(within(line(1)).getByLabelText('Montant net'), '2300');
    expect(screen.getByRole('group', { name: 'Ce revenu est-il visible de l’autre adulte ?' })).toBeInTheDocument();
    expect(screen.getByRole('radio', { name: 'Oui, partagé' })).not.toBeChecked();
    expect(screen.getByRole('radio', { name: 'Non, privé' })).not.toBeChecked();
    await user.click(screen.getByRole('button', { name: 'Créer le revenu' }));

    expect(await screen.findByText('Répondez à cette question.')).toBeInTheDocument();
    expect(bodies).toEqual([]);
  });

  it('removes a component, even the last one: a source may start without any', async () => {
    const bodies: unknown[] = [];
    serveCreate(bodies);
    const { user, router } = await renderApp('/income/new');

    await fillName(user);
    await user.click(screen.getByRole('radio', { name: 'Oui, partagé' }));
    await user.click(screen.getByRole('button', { name: 'Ajouter une composante' }));
    await user.click(screen.getByRole('button', { name: 'Retirer la composante 2' }));
    await user.click(screen.getByRole('button', { name: 'Retirer la composante 1' }));
    expect(screen.queryByRole('group', { name: /Composante/ })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Ajouter une composante' })).toHaveFocus();
    await user.click(screen.getByRole('button', { name: 'Créer le revenu' }));

    await waitFor(() => expect(router.state.location.pathname).toBe(`/income/${SALARY}`));
    expect(bodies).toEqual([expect.objectContaining({ visibility: 'shared', phases: [] })]);
  });

  it('refuses two overlapping lines of one label before sending, under the later one', async () => {
    const bodies: unknown[] = [];
    serveCreate(bodies);
    const { user } = await renderApp('/income/new');

    await fillName(user);
    await user.click(screen.getByRole('radio', { name: 'Oui, partagé' }));
    await user.type(within(line(1)).getByLabelText('Montant net'), '2300');
    await user.clear(within(line(1)).getByLabelText('Début'));
    await user.type(within(line(1)).getByLabelText('Début'), '2025-01-01');
    await user.click(screen.getByRole('button', { name: 'Ajouter une composante' }));
    await user.type(within(line(2)).getByLabelText('Libellé'), 'Salaire de base');
    await user.type(within(line(2)).getByLabelText('Montant net'), '2400');
    await user.clear(within(line(2)).getByLabelText('Début'));
    await user.type(within(line(2)).getByLabelText('Début'), '2025-06-01');
    await user.click(screen.getByRole('button', { name: 'Créer le revenu' }));

    expect(await within(line(2)).findByText('Une autre période de ce libellé couvre déjà une partie de ces dates.')).toBeInTheDocument();
    expect(within(line(1)).queryByText('Une autre période de ce libellé couvre déjà une partie de ces dates.')).not.toBeInTheDocument();
    expect(bodies).toEqual([]);
  });

  it('shows a server refusal under the line it names, and keeps the visibility answered', async () => {
    serveCreate([], () => HttpResponse.json(
      { status: 422, code: 'income.phase_overlap', title: 'x', errors: [{ path: 'phases[1].startsOn', code: 'income.phase_overlap', message: 'x' }] },
      { status: 422 }));
    const { user } = await renderApp('/income/new');

    await fillName(user);
    await user.click(screen.getByRole('radio', { name: 'Non, privé' }));
    await user.type(within(line(1)).getByLabelText('Montant net'), '2300');
    await user.click(screen.getByRole('button', { name: 'Ajouter une composante' }));
    await user.type(within(line(2)).getByLabelText('Libellé'), 'Prime');
    await user.type(within(line(2)).getByLabelText('Montant net'), '100');
    await user.click(screen.getByRole('button', { name: 'Créer le revenu' }));

    expect(await within(line(2)).findByText('Une autre période de ce libellé couvre déjà une partie de ces dates.')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(screen.getByRole('radio', { name: 'Non, privé' })).toBeChecked();
  });

  it('shows a refusal no field owns above the form', async () => {
    serveCreate([], () => HttpResponse.json({ status: 422, code: 'income.invalid_source', title: 'x' }, { status: 422 }));
    const { user } = await renderApp('/income/new');

    await fillName(user);
    await user.click(screen.getByRole('radio', { name: 'Oui, partagé' }));
    await user.type(within(line(1)).getByLabelText('Montant net'), '2300');
    await user.click(screen.getByRole('button', { name: 'Créer le revenu' }));

    expect(await screen.findByRole('alert')).toBeInTheDocument();
  });

  it('asks for « Tous les N mois » with N between 2 and 12, and starts a new line today', async () => {
    const bodies: unknown[] = [];
    serveCreate(bodies);
    const { user } = await renderApp('/income/new');

    await fillName(user);
    await user.click(screen.getByRole('radio', { name: 'Oui, partagé' }));
    expect(within(line(1)).getByLabelText('Début')).toHaveValue(todayIso());
    await user.type(within(line(1)).getByLabelText('Montant net'), '600');
    await user.selectOptions(within(line(1)).getByLabelText('Fréquence'), 'months');
    await user.type(within(line(1)).getByLabelText('Nombre de mois'), '13');
    await user.click(screen.getByRole('button', { name: 'Créer le revenu' }));

    expect(await within(line(1)).findByText('Entre 2 et 12 mois.')).toBeInTheDocument();
    expect(bodies).toEqual([]);
  });
});
