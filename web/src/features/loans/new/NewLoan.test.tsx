import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { pick } from '@/test/controls';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { persons } from '../../subscriptions/testData';
import { todayIso } from '../../subscriptions/today';
import { LOAN, loanDetail, schedule } from '../testData';
import { PROPERTY, propertyList, propertySummary } from '../../properties/testData';
import { plusOneMonth } from './loanForm';

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
const next = (user: User) => user.click(screen.getByRole('button', { name: 'Continuer' }));
const submit = (user: User) => user.click(screen.getByRole('button', { name: 'Créer le crédit' }));
const sent = (bodies: unknown[]) => bodies[0] as Record<string, unknown>;
const frDate = (iso: string) => `${iso.slice(8, 10)}/${iso.slice(5, 7)}/${iso.slice(0, 4)}`;
/** Picks in a select that a plain `pick` by label cannot name: the same label under several groups. */
async function pickAt(user: User, trigger: HTMLElement, option: string) {
  await user.click(trigger);
  await user.click(await within(await screen.findByRole('listbox')).findByRole('option', { name: option }));
}
const step = () => screen.getByRole('listitem', { current: 'step' });

async function essentials(user: User, visibility = 'Oui, partagé') {
  await user.type(await screen.findByLabelText('Nom'), 'Prêt maison');
  await user.type(screen.getByLabelText('Prêteur'), 'Banque');
  await user.click(screen.getByRole('radio', { name: visibility }));
  await next(user);
}

/** A plain loan of 200 000 € at 3,5 % over 240 months, disbursed on 2026-09-12 (its first payment then proposed on 2026-10-12). */
async function fillLoan(user: User) {
  await user.type(await screen.findByLabelText('Capital emprunté'), '200000');
  await user.type(screen.getByLabelText('Taux annuel (%)'), '3,5');
  await user.type(screen.getByLabelText('Durée (mois)'), '240');
  await user.clear(screen.getByLabelText('Date de déblocage'));
  await user.type(screen.getByLabelText('Date de déblocage'), '12/09/2026');
}

const toLoan = (user: User, visibility?: string) => essentials(user, visibility);
async function toInsurance(user: User, visibility?: string) { await essentials(user, visibility); await fillLoan(user); await next(user); }
/** Opens « Phases » (the phase list lives under a fold). */
const openPhases = (user: User) => user.click(screen.getByRole('button', { name: 'Phases' }));

// Lazy chunks load cold on the first render; warm them so no findBy races its 1 s default.
beforeAll(async () => { await import('./NewLoan'); }, 30_000);

describe('NewLoan', () => {
  it('creates the same loan body as before, through three steps, its rate as a fraction, then shows it', async () => {
    const bodies: unknown[] = [];
    serveCreate(bodies);
    const { user, router, container } = await renderApp('/loans/new');

    await essentials(user, 'Non, privé');
    expect(step()).toHaveTextContent('Le prêt');
    await fillLoan(user);
    expect(screen.getByLabelText('Première échéance')).toHaveValue('12/10/2026');
    expect(screen.queryByRole('group', { name: /^Phase/ })).not.toBeInTheDocument();
    await next(user);
    expect(step()).toHaveTextContent('Les assurances');
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

  it('asks the visibility question with neither answer checked, and stays on the step until it is answered', async () => {
    const bodies: unknown[] = [];
    serveCreate(bodies);
    const { user } = await renderApp('/loans/new');

    await user.type(await screen.findByLabelText('Nom'), 'Prêt maison');
    expect(screen.getByRole('radiogroup', { name: 'Ce crédit est-il visible de l’autre adulte ?' })).toBeInTheDocument();
    expect(screen.getByRole('radio', { name: 'Oui, partagé' })).not.toBeChecked();
    expect(screen.getByRole('radio', { name: 'Non, privé' })).not.toBeChecked();
    await next(user);

    expect(await screen.findByText('Répondez à cette question.')).toBeInTheDocument();
    expect(step()).toHaveTextContent('Le crédit');
    expect(bodies).toEqual([]);
  });

  it('stays on a step with an invalid field, focusing it', async () => {
    serveCreate([]);
    const { user } = await renderApp('/loans/new');

    await toLoan(user);
    await user.type(await screen.findByLabelText('Taux annuel (%)'), '3,5');
    await user.type(screen.getByLabelText('Durée (mois)'), '240');
    await next(user);

    expect(await screen.findByText('Un montant positif, avec deux décimales au plus, comme 1500,50.')).toBeInTheDocument();
    expect(step()).toHaveTextContent('Le prêt');
    await waitFor(() => expect(screen.getByLabelText('Capital emprunté')).toHaveFocus());
  });

  it('refuses a rate with a fifth decimal before moving on, and takes one typed with a dot', async () => {
    const bodies: unknown[] = [];
    serveCreate(bodies);
    const { user } = await renderApp('/loans/new');

    await toLoan(user);
    await fillLoan(user);
    await user.clear(screen.getByLabelText('Taux annuel (%)'));
    await user.type(screen.getByLabelText('Taux annuel (%)'), '3,12345');
    await next(user);

    expect(await screen.findByText('Un taux de 0 à moins de 100 %, avec quatre décimales au plus, comme 3,5.')).toBeInTheDocument();
    expect(step()).toHaveTextContent('Le prêt');
    await user.clear(screen.getByLabelText('Taux annuel (%)'));
    await user.type(screen.getByLabelText('Taux annuel (%)'), '3.5');
    await next(user);
    await submit(user);

    await waitFor(() => expect(bodies).toHaveLength(1));
    expect(sent(bodies).phases).toEqual([{ seq: 1, mode: 'annuity', annualRate: '0.035', rateType: 'fixed' }]);
  });

  it('proposes the first payment a month after the disbursement, until the person types it', async () => {
    serveCreate([]);
    const { user } = await renderApp('/loans/new');

    await toLoan(user);
    const disbursed = await screen.findByLabelText('Date de déblocage');
    const first = screen.getByLabelText('Première échéance');
    expect(disbursed).toHaveValue(frDate(todayIso()));
    expect(first).toHaveValue(frDate(plusOneMonth(todayIso())));
    await user.clear(disbursed);
    await user.type(disbursed, '31/01/2026');
    expect(first).toHaveValue('28/02/2026');
    await user.clear(first);
    await user.type(first, '05/03/2026');
    await user.clear(disbursed);
    await user.type(disbursed, '10/02/2026');
    expect(first).toHaveValue('05/03/2026');
  });

  it('turns into a phase list, the plain rate becoming phase 1, and sends each phase', async () => {
    const bodies: unknown[] = [];
    serveCreate(bodies);
    const { user, container } = await renderApp('/loans/new');

    await toLoan(user);
    await fillLoan(user);
    await openPhases(user);
    await user.click(screen.getByRole('button', { name: 'Ajouter une phase' }));
    const one = group('Phase 1');
    expect(within(one).getByLabelText('Taux annuel (%)')).toHaveValue('3,5');
    await pickAt(user, within(one).getByLabelText('Type d’échéance'), 'Différé partiel (intérêts seuls)');
    await user.type(within(one).getByLabelText('Nombre d’échéances'), '24');
    const two = group('Phase 2');
    await user.type(within(two).getByLabelText('Taux annuel (%)'), '3.9');
    await pickAt(user, within(two).getByLabelText('Nature du taux'), 'Variable');
    const options = within(two).getByRole('button', { name: 'Plus d’options' });
    expect(options).toHaveAttribute('aria-expanded', 'false');
    await user.click(options);
    expect(options).toHaveAttribute('aria-expanded', 'true');
    await user.type(within(two).getByLabelText('Mensualité imposée'), '1250,5');
    await expectNoAxeViolations(container);
    await next(user);
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

    await toLoan(user);
    await fillLoan(user);
    await openPhases(user);
    await user.click(screen.getByRole('button', { name: 'Ajouter une phase' }));
    const one = group('Phase 1');
    await pickAt(user, within(one).getByLabelText('Nature du taux'), 'Variable');
    await user.type(within(one).getByLabelText('Nombre d’échéances'), '12');
    await user.click(within(one).getByRole('button', { name: 'Plus d’options' }));
    await user.type(within(one).getByLabelText('Note de la phase'), 'travaux');
    await user.click(within(group('Phase 2')).getByRole('button', { name: 'Retirer la phase 2' }));

    expect(screen.queryByRole('group', { name: /^Phase/ })).not.toBeInTheDocument();
    expect(screen.getByLabelText('Taux annuel (%)')).toHaveValue('3,5');
    expect(screen.getByRole('button', { name: 'Ajouter une phase' })).toHaveFocus();
    await next(user);
    await submit(user);

    await waitFor(() => expect(bodies).toHaveLength(1));
    expect(sent(bodies).phases).toEqual([{ seq: 1, mode: 'annuity', annualRate: '0.035', rateType: 'variable' }]);
  });

  it('keeps the phase list when the line left is a deferral, so its mode is seen', async () => {
    serveCreate([]);
    const { user } = await renderApp('/loans/new');

    await toLoan(user);
    await fillLoan(user);
    await openPhases(user);
    await user.click(screen.getByRole('button', { name: 'Ajouter une phase' }));
    await pickAt(user, within(group('Phase 2')).getByLabelText('Type d’échéance'), 'Différé total');
    await user.click(within(group('Phase 1')).getByRole('button', { name: 'Retirer la phase 1' }));

    expect(within(group('Phase 1')).getByLabelText('Type d’échéance')).toHaveTextContent('Différé total');
    expect(within(group('Phase 1')).queryByRole('button', { name: /^Retirer la phase/ })).not.toBeInTheDocument();
  });

  it('refuses more phase payments than the loan has, before moving on, under the duration', async () => {
    serveCreate([]);
    const { user } = await renderApp('/loans/new');

    await toLoan(user);
    await fillLoan(user);
    await openPhases(user);
    await user.click(screen.getByRole('button', { name: 'Ajouter une phase' }));
    await user.type(within(group('Phase 1')).getByLabelText('Nombre d’échéances'), '200');
    await user.type(within(group('Phase 2')).getByLabelText('Taux annuel (%)'), '4');
    await user.type(within(group('Phase 2')).getByLabelText('Nombre d’échéances'), '41');
    await next(user);

    expect(await screen.findByText('Les phases comptent plus d’échéances que la durée du prêt.')).toBeInTheDocument();
    expect(screen.getByLabelText('Durée (mois)')).toHaveAttribute('aria-invalid', 'true');
    expect(step()).toHaveTextContent('Le prêt');
  });

  it('folds the calculation options and sends what is chosen there', async () => {
    const bodies: unknown[] = [];
    serveCreate(bodies);
    const { user } = await renderApp('/loans/new');

    await toLoan(user);
    await fillLoan(user);
    const options = screen.getByRole('button', { name: 'Options de calcul' });
    expect(options).toHaveAttribute('aria-expanded', 'false');
    // A long label wraps under its chevron on a phone instead of overflowing (Folded).
    expect(options).toHaveClass('h-auto', 'min-h-8', 'max-w-full', 'text-left', 'whitespace-normal');
    expect(screen.queryByLabelText('Arrondi des intérêts')).not.toBeInTheDocument();
    await user.click(options);
    await pick(user, 'Arrondi des intérêts', 'Tronqué');
    await pick(user, 'Première période', 'Au prorata des jours');
    await next(user);
    await submit(user);

    await waitFor(() => expect(bodies).toHaveLength(1));
    expect(sent(bodies)).toMatchObject({ rounding: 'truncate', firstPeriod: 'prorata_actual_365' });
  });

  it('folds the currency and the note, and sends the currency chosen there', async () => {
    const bodies: unknown[] = [];
    serveCreate(bodies);
    const { user } = await renderApp('/loans/new');

    await user.type(await screen.findByLabelText('Nom'), 'Prêt maison');
    expect(screen.queryByLabelText('Devise')).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Plus d’options' }));
    await pick(user, 'Devise', /^CHF/);
    await user.type(screen.getByLabelText('Note'), 'Prêt relais');
    await user.click(screen.getByRole('radio', { name: 'Oui, partagé' }));
    await next(user);
    await fillLoan(user);
    await next(user);
    await submit(user);

    await waitFor(() => expect(bodies).toHaveLength(1));
    expect(sent(bodies)).toMatchObject({ currency: 'CHF', note: 'Prêt relais' });
  });

  it('adds an insurance from the first payment, its rate sent as a fraction; an end before its start is refused', async () => {
    const bodies: unknown[] = [];
    serveCreate(bodies);
    const { user, container } = await renderApp('/loans/new');

    await toInsurance(user);
    await user.click(screen.getByRole('button', { name: 'Ajouter une assurance' }));
    const insurance = group('Assurance 1');
    expect(within(insurance).getByLabelText('Début')).toHaveValue('12/10/2026');
    expect(within(insurance).getByLabelText('Montant par mois')).toBeInTheDocument();
    await pickAt(user, within(insurance).getByLabelText('Mode de calcul'), 'Taux sur le capital restant dû');
    expect(within(insurance).queryByLabelText('Montant par mois')).not.toBeInTheDocument();
    await user.type(within(insurance).getByLabelText('Taux annuel (%)'), '0,36');
    await user.type(within(insurance).getByLabelText('Fin'), '01/10/2026');
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

    await toInsurance(user); // disbursed 2026-09-12, first payment proposed on 2026-10-12
    await user.click(screen.getByRole('button', { name: 'Ajouter une assurance' }));
    await user.click(screen.getByRole('button', { name: 'Ajouter une assurance' }));
    const two = group('Assurance 2');
    await user.clear(within(two).getByLabelText('Début'));
    await user.type(within(two).getByLabelText('Début'), '12/01/2027');

    // The first payment re-proposed from the disbursement carries the untouched start along, back on the previous step.
    await user.click(screen.getByRole('button', { name: 'Retour' }));
    await user.clear(screen.getByLabelText('Date de déblocage'));
    await user.type(screen.getByLabelText('Date de déblocage'), '20/09/2026');
    await next(user);
    expect(within(group('Assurance 1')).getByLabelText('Début')).toHaveValue('20/10/2026');
    // So does a first payment typed in.
    await user.click(screen.getByRole('button', { name: 'Retour' }));
    await user.clear(screen.getByLabelText('Première échéance'));
    await user.type(screen.getByLabelText('Première échéance'), '05/11/2026');
    await next(user);
    expect(within(group('Assurance 1')).getByLabelText('Début')).toHaveValue('05/11/2026');
    expect(within(group('Assurance 2')).getByLabelText('Début')).toHaveValue('12/01/2027');
  });

  it('stops offering insurances at twenty', async () => {
    serveCreate([]);
    const { user } = await renderApp('/loans/new');

    await toInsurance(user);
    for (let i = 0; i < 20; i += 1) await user.click(screen.getByRole('button', { name: 'Ajouter une assurance' }));

    expect(group('Assurance 20')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Ajouter une assurance' })).not.toBeInTheDocument();
    await user.click(within(group('Assurance 20')).getByRole('button', { name: 'Retirer l’assurance 20' }));
    expect(screen.getByRole('button', { name: 'Ajouter une assurance' })).toBeInTheDocument();
  }, 30_000);

  it('shows a server refusal under the line it names, back on its step with its options opened, and keeps the visibility answered', async () => {
    serveCreate([], () => HttpResponse.json(
      { status: 422, code: 'realestate.invalid_loan', title: 'x', errors: [{ path: 'phases[1].openingBalanceOverride', code: 'realestate.invalid_loan', message: 'x' }] },
      { status: 422 }));
    const { user } = await renderApp('/loans/new');

    await essentials(user, 'Non, privé');
    await fillLoan(user);
    await openPhases(user);
    await user.click(screen.getByRole('button', { name: 'Ajouter une phase' }));
    await user.type(within(group('Phase 1')).getByLabelText('Nombre d’échéances'), '24');
    await user.type(within(group('Phase 2')).getByLabelText('Taux annuel (%)'), '3');
    await next(user);
    await submit(user);

    const two = await screen.findByRole('group', { name: 'Phase 2' });
    expect(await within(two).findByText('Cette valeur n’est pas acceptée.')).toBeInTheDocument();
    expect(step()).toHaveTextContent('Le prêt');
    expect(within(two).getByRole('button', { name: 'Plus d’options' })).toHaveAttribute('aria-expanded', 'true');
    expect(within(two).getByLabelText('Capital au début de la phase')).toHaveAttribute('aria-invalid', 'true');
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Retour' }));
    expect(screen.getByRole('radio', { name: 'Non, privé' })).toBeChecked();
  });

  it('shows a refusal no field owns above the form', async () => {
    serveCreate([], () => HttpResponse.json(
      { status: 422, code: 'realestate.invalid_loan', title: 'x', errors: [{ path: 'phases[0].seq', code: 'realestate.invalid_loan', message: 'x' }] },
      { status: 422 }));
    const { user } = await renderApp('/loans/new');

    await toInsurance(user);
    await submit(user);

    expect(await screen.findByRole('alert')).toBeInTheDocument();
  });

  it('the financed property « Aucun » sends no property', async () => {
    const bodies: unknown[] = [];
    serveCreate(bodies);
    server.use(http.get('*/api/v1/realestate/properties', () => HttpResponse.json(propertyList([propertySummary()]))));
    const { user } = await renderApp('/loans/new');

    await toLoan(user);
    await fillLoan(user);
    expect(screen.getByLabelText('Bien financé')).toHaveTextContent('Aucun');
    await next(user);
    await submit(user);

    await waitFor(() => expect(bodies).toHaveLength(1));
    expect(sent(bodies)).not.toHaveProperty('propertyId');
  });

  it('finances a property chosen in « Bien financé », from the household’s list', async () => {
    const bodies: unknown[] = [];
    const seen: string[] = [];
    serveCreate(bodies);
    server.use(http.get('*/api/v1/realestate/properties', ({ request }) => {
      seen.push(new URL(request.url).search);
      return HttpResponse.json(propertyList([propertySummary()]));
    }));
    const { user, container } = await renderApp('/loans/new?scope=me');

    await toLoan(user);
    await fillLoan(user);
    await pick(user, 'Bien financé', 'Maison de Nantes');
    await expectNoAxeViolations(container);
    await next(user);
    await submit(user);

    await waitFor(() => expect(bodies).toHaveLength(1));
    expect(sent(bodies).propertyId).toBe(PROPERTY);
    expect(seen[0]).toBe('?scope=me');
  });

  it('opened from a new property (issue #17), finances that property without choosing it again', async () => {
    const bodies: unknown[] = [];
    serveCreate(bodies);
    server.use(http.get('*/api/v1/realestate/properties', () => HttpResponse.json(propertyList([propertySummary()]))));
    const { user } = await renderApp(`/loans/new?property=${PROPERTY}`);

    await toLoan(user);
    await fillLoan(user);
    expect(screen.getByLabelText('Bien financé')).toHaveTextContent('Maison de Nantes');
    await next(user);
    await submit(user);

    await waitFor(() => expect(bodies).toHaveLength(1));
    expect(sent(bodies).propertyId).toBe(PROPERTY);
  });

  it('shows realestate.invalid_property under « Bien financé », back on its step', async () => {
    serveCreate([], () => HttpResponse.json(
      { status: 422, code: 'realestate.invalid_property', title: 'x', errors: [{ path: 'propertyId', code: 'realestate.invalid_property', message: 'x' }] },
      { status: 422 }));
    server.use(http.get('*/api/v1/realestate/properties', () => HttpResponse.json(propertyList([propertySummary()]))));
    const { user } = await renderApp('/loans/new');

    await toLoan(user);
    await fillLoan(user);
    await pick(user, 'Bien financé', 'Maison de Nantes');
    await next(user);
    await submit(user);

    expect(await screen.findByText('Ce bien n’existe pas ou n’est pas accessible.')).toBeInTheDocument();
    expect(step()).toHaveTextContent('Le prêt');
    expect(screen.getByLabelText('Bien financé')).toHaveAttribute('aria-invalid', 'true');
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });
});
