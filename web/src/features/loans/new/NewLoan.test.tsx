import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { persons } from '../../subscriptions/testData';
import { todayIso } from '../../subscriptions/today';
import { LOAN, loanDetail, schedule } from '../testData';
import { PROPERTY, propertyList, propertySummary } from '../../properties/testData';
import { plusOneMonth } from './newLoan';

function serveCreate(bodies: unknown[], answer: () => Response = () => HttpResponse.json({ id: LOAN }, { status: 201 })) {
  server.use(
    http.get('*/api/v1/core/persons', () => HttpResponse.json(persons)),
    http.post('*/api/v1/realestate/loans', async ({ request }) => { bodies.push(await request.json()); return answer(); }),
    http.get('*/api/v1/realestate/loans/:id', () => HttpResponse.json(loanDetail())),
    http.get('*/api/v1/realestate/loans/:id/schedule', () => HttpResponse.json(schedule())),
  );
}

type User = Awaited<ReturnType<typeof renderApp>>['user'];
const group = (name: string) => screen.getByRole('group', { name });
const submit = (user: User) => user.click(screen.getByRole('button', { name: 'Créer le crédit' }));
const sent = (bodies: unknown[]) => bodies[0] as Record<string, unknown>;

/** A plain loan of 200 000 € at 3,5 % over 240 months, disbursed on 2026-09-12 (its first payment then proposed on 2026-10-12). */
async function fillPlain(user: User) {
  await user.type(await screen.findByLabelText('Nom'), 'Prêt maison');
  await user.type(screen.getByLabelText('Prêteur'), 'Banque');
  await user.type(screen.getByLabelText('Capital emprunté'), '200000');
  await user.type(screen.getByLabelText('Taux annuel (%)'), '3,5');
  await user.type(screen.getByLabelText('Durée (mois)'), '240');
  await user.clear(screen.getByLabelText('Date de déblocage'));
  await user.type(screen.getByLabelText('Date de déblocage'), '2026-09-12');
}

// Lazy chunks load cold on the first render; warm them so no findBy races its 1 s default.
beforeAll(async () => { await import('./NewLoan'); }, 30_000);

describe('NewLoan', () => {
  it('creates a plain loan in one request, its rate as a fraction, then shows it', async () => {
    const bodies: unknown[] = [];
    serveCreate(bodies);
    const { user, router, container } = await renderApp('/loans/new');

    await fillPlain(user);
    await user.click(screen.getByRole('radio', { name: 'Non, privé' }));
    expect(screen.getByLabelText('Première échéance')).toHaveValue('2026-10-12');
    expect(screen.queryByRole('group', { name: /^Phase/ })).not.toBeInTheDocument();
    await expectNoAxeViolations(container);
    await submit(user);

    await waitFor(() => expect(router.state.location.pathname).toBe(`/loans/${LOAN}`));
    expect(bodies).toEqual([{
      name: 'Prêt maison', lender: 'Banque', currency: 'EUR', visibility: 'private',
      borrowers: [{ personId: '01926b9e-0000-7000-8000-00000000000a', shareBp: 10000 }],
      principal: '200000', totalPayments: 240, disbursedOn: '2026-09-12', firstPaymentDate: '2026-10-12', rounding: 'half_up',
      firstPeriod: 'full', phases: [{ seq: 1, mode: 'annuity', annualRate: '0.035', rateType: 'fixed' }], insurances: [],
    }]);
  });

  it('asks the visibility question with neither answer checked, and refuses to send until it is answered', async () => {
    const bodies: unknown[] = [];
    serveCreate(bodies);
    const { user } = await renderApp('/loans/new');

    await fillPlain(user);
    expect(screen.getByRole('group', { name: 'Ce crédit est-il visible de l’autre adulte ?' })).toBeInTheDocument();
    expect(screen.getByRole('radio', { name: 'Oui, partagé' })).not.toBeChecked();
    expect(screen.getByRole('radio', { name: 'Non, privé' })).not.toBeChecked();
    await submit(user);

    expect(await screen.findByText('Répondez à cette question.')).toBeInTheDocument();
    expect(bodies).toEqual([]);
  });

  it('refuses a rate with a fifth decimal before sending, and takes one typed with a dot', async () => {
    const bodies: unknown[] = [];
    serveCreate(bodies);
    const { user } = await renderApp('/loans/new');

    await fillPlain(user);
    await user.click(screen.getByRole('radio', { name: 'Oui, partagé' }));
    await user.clear(screen.getByLabelText('Taux annuel (%)'));
    await user.type(screen.getByLabelText('Taux annuel (%)'), '3,12345');
    await submit(user);

    expect(await screen.findByText('Un taux de 0 à moins de 100 %, avec quatre décimales au plus, comme 3,5.')).toBeInTheDocument();
    expect(bodies).toEqual([]);
    await user.clear(screen.getByLabelText('Taux annuel (%)'));
    await user.type(screen.getByLabelText('Taux annuel (%)'), '3.5');
    await submit(user);

    await waitFor(() => expect(bodies).toHaveLength(1));
    expect(sent(bodies).phases).toEqual([{ seq: 1, mode: 'annuity', annualRate: '0.035', rateType: 'fixed' }]);
  });

  it('proposes the first payment a month after the disbursement, until the person types it', async () => {
    serveCreate([]);
    const { user } = await renderApp('/loans/new');

    const disbursed = await screen.findByLabelText('Date de déblocage');
    const first = screen.getByLabelText('Première échéance');
    expect(disbursed).toHaveValue(todayIso());
    expect(first).toHaveValue(plusOneMonth(todayIso()));
    await user.clear(disbursed);
    await user.type(disbursed, '2026-01-31');
    expect(first).toHaveValue('2026-02-28');
    await user.clear(first);
    await user.type(first, '2026-03-05');
    await user.clear(disbursed);
    await user.type(disbursed, '2026-02-10');
    expect(first).toHaveValue('2026-03-05');
  });

  it('turns into a phase list, the plain rate becoming phase 1, and sends each phase', async () => {
    const bodies: unknown[] = [];
    serveCreate(bodies);
    const { user, container } = await renderApp('/loans/new');

    await fillPlain(user);
    await user.click(screen.getByRole('radio', { name: 'Oui, partagé' }));
    await user.click(screen.getByRole('button', { name: 'Ajouter une phase' }));
    const one = group('Phase 1');
    expect(within(one).getByLabelText('Taux annuel (%)')).toHaveValue('3,5');
    await user.selectOptions(within(one).getByLabelText('Type d’échéance'), 'deferred_partial');
    await user.type(within(one).getByLabelText('Nombre d’échéances'), '24');
    const two = group('Phase 2');
    await user.type(within(two).getByLabelText('Taux annuel (%)'), '3.9');
    await user.selectOptions(within(two).getByLabelText('Nature du taux'), 'variable');
    const options = within(two).getByRole('button', { name: 'Plus d’options' });
    expect(options).toHaveAttribute('aria-expanded', 'false');
    await user.click(options);
    expect(options).toHaveAttribute('aria-expanded', 'true');
    await user.type(within(two).getByLabelText('Mensualité imposée'), '1250,5');
    await expectNoAxeViolations(container);
    await submit(user);

    await waitFor(() => expect(bodies).toHaveLength(1));
    expect(sent(bodies).phases).toEqual([
      { seq: 1, mode: 'deferred_partial', annualRate: '0.035', rateType: 'fixed', payments: 24 },
      { seq: 2, mode: 'annuity', annualRate: '0.039', rateType: 'variable', paymentOverride: '1250.5' },
    ]);
  });

  it('is a plain loan again once one line is left, keeping its mode, rate and type only', async () => {
    const bodies: unknown[] = [];
    serveCreate(bodies);
    const { user } = await renderApp('/loans/new');

    await fillPlain(user);
    await user.click(screen.getByRole('radio', { name: 'Oui, partagé' }));
    await user.click(screen.getByRole('button', { name: 'Ajouter une phase' }));
    const one = group('Phase 1');
    await user.selectOptions(within(one).getByLabelText('Nature du taux'), 'variable');
    await user.type(within(one).getByLabelText('Nombre d’échéances'), '12');
    await user.click(within(one).getByRole('button', { name: 'Plus d’options' }));
    await user.type(within(one).getByLabelText('Note de la phase'), 'travaux');
    await user.click(within(group('Phase 2')).getByRole('button', { name: 'Retirer la phase 2' }));

    expect(screen.queryByRole('group', { name: /^Phase/ })).not.toBeInTheDocument();
    expect(screen.getByLabelText('Taux annuel (%)')).toHaveValue('3,5');
    expect(screen.getByRole('button', { name: 'Ajouter une phase' })).toHaveFocus();
    await submit(user);

    await waitFor(() => expect(bodies).toHaveLength(1));
    expect(sent(bodies).phases).toEqual([{ seq: 1, mode: 'annuity', annualRate: '0.035', rateType: 'variable' }]);
  });

  it('keeps the phase list when the line left is a deferral, so its mode is seen', async () => {
    serveCreate([]);
    const { user } = await renderApp('/loans/new');

    await fillPlain(user);
    await user.click(screen.getByRole('button', { name: 'Ajouter une phase' }));
    await user.selectOptions(within(group('Phase 2')).getByLabelText('Type d’échéance'), 'deferred_total');
    await user.click(within(group('Phase 1')).getByRole('button', { name: 'Retirer la phase 1' }));

    expect(within(group('Phase 1')).getByLabelText('Type d’échéance')).toHaveValue('deferred_total');
    expect(within(group('Phase 1')).queryByRole('button', { name: /^Retirer la phase/ })).not.toBeInTheDocument();
  });

  it('refuses more phase payments than the loan has, before sending, under the duration', async () => {
    const bodies: unknown[] = [];
    serveCreate(bodies);
    const { user } = await renderApp('/loans/new');

    await fillPlain(user);
    await user.click(screen.getByRole('radio', { name: 'Oui, partagé' }));
    await user.click(screen.getByRole('button', { name: 'Ajouter une phase' }));
    await user.type(within(group('Phase 1')).getByLabelText('Nombre d’échéances'), '200');
    await user.type(within(group('Phase 2')).getByLabelText('Taux annuel (%)'), '4');
    await user.type(within(group('Phase 2')).getByLabelText('Nombre d’échéances'), '41');
    await submit(user);

    expect(await screen.findByText('Les phases comptent plus d’échéances que la durée du prêt.')).toBeInTheDocument();
    expect(screen.getByLabelText('Durée (mois)')).toHaveAttribute('aria-invalid', 'true');
    expect(bodies).toEqual([]);
  });

  it('folds the calculation options and sends what is chosen there', async () => {
    const bodies: unknown[] = [];
    serveCreate(bodies);
    const { user } = await renderApp('/loans/new');

    await fillPlain(user);
    await user.click(screen.getByRole('radio', { name: 'Oui, partagé' }));
    const options = screen.getByRole('button', { name: 'Options de calcul' });
    expect(options).toHaveAttribute('aria-expanded', 'false');
    expect(screen.queryByLabelText('Arrondi des intérêts')).not.toBeInTheDocument();
    await user.click(options);
    await user.selectOptions(screen.getByLabelText('Arrondi des intérêts'), 'truncate');
    await user.selectOptions(screen.getByLabelText('Première période'), 'prorata_actual_365');
    await submit(user);

    await waitFor(() => expect(bodies).toHaveLength(1));
    expect(sent(bodies)).toMatchObject({ rounding: 'truncate', firstPeriod: 'prorata_actual_365' });
  });

  it('adds an insurance from the first payment, its rate sent as a fraction; an end before its start is refused', async () => {
    const bodies: unknown[] = [];
    serveCreate(bodies);
    const { user, container } = await renderApp('/loans/new');

    await fillPlain(user);
    await user.click(screen.getByRole('radio', { name: 'Oui, partagé' }));
    await user.click(screen.getByRole('button', { name: 'Ajouter une assurance' }));
    const insurance = group('Assurance 1');
    expect(within(insurance).getByLabelText('Début')).toHaveValue('2026-10-12');
    expect(within(insurance).getByLabelText('Montant par mois')).toBeInTheDocument();
    await user.selectOptions(within(insurance).getByLabelText('Mode de calcul'), 'rate_on_remaining');
    expect(within(insurance).queryByLabelText('Montant par mois')).not.toBeInTheDocument();
    await user.type(within(insurance).getByLabelText('Taux annuel (%)'), '0,36');
    await user.type(within(insurance).getByLabelText('Fin'), '2026-10-01');
    await submit(user);

    expect(await within(insurance).findByText('La fin vient après le début.')).toBeInTheDocument();
    expect(bodies).toEqual([]);
    await user.clear(within(insurance).getByLabelText('Fin'));
    await expectNoAxeViolations(container);
    await submit(user);

    await waitFor(() => expect(bodies).toHaveLength(1));
    expect(sent(bodies).insurances).toEqual([{ mode: 'rate_on_remaining', annualRate: '0.0036', startsOn: '2026-10-12' }]);
  });

  it('keeps an added insurance’s start on the first payment until its start is typed in', async () => {
    serveCreate([]);
    const { user } = await renderApp('/loans/new');

    await fillPlain(user); // disbursed 2026-09-12, first payment proposed on 2026-10-12
    await user.click(screen.getByRole('button', { name: 'Ajouter une assurance' }));
    await user.click(screen.getByRole('button', { name: 'Ajouter une assurance' }));
    const one = group('Assurance 1');
    const two = group('Assurance 2');
    await user.clear(within(two).getByLabelText('Début'));
    await user.type(within(two).getByLabelText('Début'), '2027-01-12');

    // The first payment re-proposed from the disbursement carries the untouched start along.
    await user.clear(screen.getByLabelText('Date de déblocage'));
    await user.type(screen.getByLabelText('Date de déblocage'), '2026-09-20');
    expect(within(one).getByLabelText('Début')).toHaveValue('2026-10-20');
    // So does a first payment typed in.
    await user.clear(screen.getByLabelText('Première échéance'));
    await user.type(screen.getByLabelText('Première échéance'), '2026-11-05');
    expect(within(one).getByLabelText('Début')).toHaveValue('2026-11-05');
    expect(within(two).getByLabelText('Début')).toHaveValue('2027-01-12');
  });

  it('stops offering insurances at twenty', async () => {
    serveCreate([]);
    const { user } = await renderApp('/loans/new');

    await screen.findByRole('button', { name: 'Ajouter une assurance' });
    for (let i = 0; i < 20; i += 1) await user.click(screen.getByRole('button', { name: 'Ajouter une assurance' }));

    expect(group('Assurance 20')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Ajouter une assurance' })).not.toBeInTheDocument();
    await user.click(within(group('Assurance 20')).getByRole('button', { name: 'Retirer l’assurance 20' }));
    expect(screen.getByRole('button', { name: 'Ajouter une assurance' })).toBeInTheDocument();
  }, 20_000);

  it('shows a server refusal under the line it names, its options opened, and keeps the visibility answered', async () => {
    serveCreate([], () => HttpResponse.json(
      { status: 422, code: 'realestate.invalid_loan', title: 'x', errors: [{ path: 'phases[1].openingBalanceOverride', code: 'realestate.invalid_loan', message: 'x' }] },
      { status: 422 }));
    const { user } = await renderApp('/loans/new');

    await fillPlain(user);
    await user.click(screen.getByRole('radio', { name: 'Non, privé' }));
    await user.click(screen.getByRole('button', { name: 'Ajouter une phase' }));
    await user.type(within(group('Phase 1')).getByLabelText('Nombre d’échéances'), '24');
    await user.type(within(group('Phase 2')).getByLabelText('Taux annuel (%)'), '3');
    await submit(user);

    const two = group('Phase 2');
    expect(await within(two).findByText('Cette valeur n’est pas acceptée.')).toBeInTheDocument();
    expect(within(two).getByRole('button', { name: 'Plus d’options' })).toHaveAttribute('aria-expanded', 'true');
    expect(within(two).getByLabelText('Capital au début de la phase')).toHaveAttribute('aria-invalid', 'true');
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(screen.getByRole('radio', { name: 'Non, privé' })).toBeChecked();
  });

  it('shows a refusal no field owns above the form', async () => {
    serveCreate([], () => HttpResponse.json(
      { status: 422, code: 'realestate.invalid_loan', title: 'x', errors: [{ path: 'phases[0].seq', code: 'realestate.invalid_loan', message: 'x' }] },
      { status: 422 }));
    const { user } = await renderApp('/loans/new');

    await fillPlain(user);
    await user.click(screen.getByRole('radio', { name: 'Oui, partagé' }));
    await submit(user);

    expect(await screen.findByRole('alert')).toBeInTheDocument();
  });

  it('finances a property chosen in « Bien financé », « Aucun » by default, from the household’s list', async () => {
    const bodies: unknown[] = [];
    const seen: string[] = [];
    serveCreate(bodies);
    server.use(http.get('*/api/v1/realestate/properties', ({ request }) => {
      seen.push(new URL(request.url).search);
      return HttpResponse.json(propertyList([propertySummary()]));
    }));
    const { user, container } = await renderApp('/loans/new?scope=me');

    await fillPlain(user);
    await user.click(screen.getByRole('radio', { name: 'Oui, partagé' }));
    const select = screen.getByLabelText('Bien financé');
    expect(select).toHaveValue('');
    expect(select).toHaveDisplayValue('Aucun');
    await screen.findByRole('option', { name: 'Maison de Nantes' });
    await user.selectOptions(select, 'Maison de Nantes');
    await expectNoAxeViolations(container);
    await submit(user);

    await waitFor(() => expect(bodies).toHaveLength(1));
    expect(sent(bodies).propertyId).toBe(PROPERTY);
    expect(seen[0]).toBe('?scope=household');
  });

  it('shows realestate.invalid_property under « Bien financé »', async () => {
    serveCreate([], () => HttpResponse.json(
      { status: 422, code: 'realestate.invalid_property', title: 'x', errors: [{ path: 'propertyId', code: 'realestate.invalid_property', message: 'x' }] },
      { status: 422 }));
    server.use(http.get('*/api/v1/realestate/properties', () => HttpResponse.json(propertyList([propertySummary()]))));
    const { user } = await renderApp('/loans/new');

    await fillPlain(user);
    await user.click(screen.getByRole('radio', { name: 'Oui, partagé' }));
    await screen.findByRole('option', { name: 'Maison de Nantes' });
    await user.selectOptions(screen.getByLabelText('Bien financé'), 'Maison de Nantes');
    await submit(user);

    expect(await screen.findByText('Ce bien n’existe pas ou n’est pas accessible.')).toBeInTheDocument();
    expect(screen.getByLabelText('Bien financé')).toHaveAttribute('aria-invalid', 'true');
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });
});
