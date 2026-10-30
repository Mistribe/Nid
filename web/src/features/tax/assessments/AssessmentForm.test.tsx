import { File as NodeFile } from 'node:buffer';
import { screen, waitFor, within } from '@testing-library/react';
import { delay, http, HttpResponse } from 'msw';
import { afterAll, beforeAll, describe, expect, it, vi } from 'vitest';
import type { components } from '@/api/schema';
import { pick } from '@/test/controls';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { list as documentList, summary } from '../../documents/testData';
import { ALICE as SESSION_PERSON, BOB as SESSION_BOB } from '../../subscriptions/testData';
import { estimate } from '../taxTestData';
import { jointAssessment, ALICE, BOB, DOCUMENT } from './testData';

type Person = components['schemas']['PersonDto'];
const adult = (id: string, displayName: string): Person => ({ id, kind: 'adult', displayName, email: null, retirementAge: 64 });
const NOTICE = '0192f0aa-0000-7000-8000-0000000000e1';
const UPLOADED = '0192f0aa-0000-7000-8000-0000000000d2';
const pdf = (name: string) => new File(['%PDF-1.7'], name, { type: 'application/pdf' });

/** The household, the reader's documents, the writes (bodies kept) and the pages the form lands on. */
function serve(people: Person[], over: { post?: () => Response; put?: () => Response; readers?: { id: string; name: string }[]; documents?: ReturnType<typeof summary>[]; slowUpload?: boolean; version?: () => number } = {}) {
  const sent: { post: unknown[]; put: unknown[]; previews: string[][] } = { post: [], put: [], previews: [] };
  server.use(
    http.get('*/api/v1/core/persons', () => HttpResponse.json(people)),
    http.get('*/api/v1/documents', () => HttpResponse.json(documentList(over.documents ?? [summary({ id: DOCUMENT, title: 'avis 2025.pdf' })]))),
    http.get('*/api/v1/documents/:id/tax-notice-preview', ({ request }) => {
      sent.previews.push(new URL(request.url).searchParams.getAll('declarants'));
      return HttpResponse.json({ gainedReaders: over.readers ?? [] });
    }),
    http.post('*/api/v1/documents', async ({ request }) => { await request.formData(); if (over.slowUpload) await delay(300); return HttpResponse.json({ id: UPLOADED }, { status: 201 }); }),
    http.post('*/api/v1/tax/assessments', async ({ request }) => { sent.post.push(await request.json()); return over.post?.() ?? HttpResponse.json({ id: NOTICE }, { status: 201 }); }),
    http.put('*/api/v1/tax/assessments/:id', async ({ request }) => { sent.put.push(await request.json()); return over.put?.() ?? new HttpResponse(null, { status: 204 }); }),
    http.get('*/api/v1/tax/assessments/:id', () => HttpResponse.json(jointAssessment({ version: over.version?.() ?? 1 }))),
    http.get('*/api/v1/documents/:id', () => HttpResponse.json({ status: 404, code: 'not_found', title: 'Gone.' }, { status: 404 })),
    http.get('*/api/v1/tax/assessments', () => HttpResponse.json({ items: [] })),
    http.get('*/api/v1/tax', () => HttpResponse.json({}, { status: 404 })),
  );
  return sent;
}

const household = [adult(SESSION_PERSON, 'Alice Dev'), adult(SESSION_BOB, 'Bob Dev')];
type User = Awaited<ReturnType<typeof renderApp>>['user'];
const next = (user: User) => user.click(screen.getByRole('button', { name: 'Continuer' }));
const submit = (user: User) => user.click(screen.getByRole('button', { name: 'Enregistrer l’avis' }));
const step = () => screen.getByRole('listitem', { current: 'step' });
const eur = (amount: string) => ({ amount, currency: 'EUR' });

beforeAll(async () => {
  // jsdom's FormData and File do not cross into Node's Request (MSW): a browser sends them as is, the tests use Node's.
  const form = await new Response('a=1', { headers: { 'content-type': 'application/x-www-form-urlencoded' } }).formData();
  vi.stubGlobal('FormData', form.constructor);
  vi.stubGlobal('File', NodeFile);
  await import('./AssessmentForm');
}, 30_000);
afterAll(() => { vi.unstubAllGlobals(); });

describe('AssessmentForm', () => {
  it('Creates_a_joint_notice_in_five_steps', async () => {
    const sent = serve(household);
    const { user, router } = await renderApp('/tax/assessments/new');

    await pick(user, 'Année des revenus', '2025');
    await pick(user, 'Déclarant 2', 'Bob Dev');
    await user.type(screen.getByLabelText('Nombre de parts'), '3');
    await next(user);
    expect(step()).toHaveTextContent('Déclarants');
    await next(user);
    expect(step()).toHaveTextContent('Revenus');
    await next(user);
    expect(step()).toHaveTextContent('Impôt');
    await user.type(screen.getByLabelText('Impôt net'), '2688');
    await user.type(screen.getByLabelText('Solde (à payer / remboursé)'), '312,00');
    await user.click(screen.getByRole('radio', { name: 'Remboursé' }));
    await next(user);
    expect(step()).toHaveTextContent('Avis');
    await pick(user, 'Avis d’imposition (PDF)', 'avis 2025.pdf');
    await submit(user);

    await waitFor(() => expect(router.state.location.pathname).toBe('/tax'));
    expect(router.state.location.search).toEqual({ year: '2025', tab: 'assessment' });
    expect(sent.post).toHaveLength(1);
    expect(sent.post[0]).toMatchObject({
      year: 2025, shares: '3', netTax: eur('2688'), balance: eur('-312.00'), documentId: DOCUMENT,
      declarants: [{ personId: SESSION_PERSON, rank: 1 }, { personId: SESSION_BOB, rank: 2 }],
    });
    expect(sent.post[0]).not.toHaveProperty('issuedYear');
  });

  it('Sends_entered_amounts_and_nulls_for_empty_fields', async () => {
    const sent = serve(household);
    const { user } = await renderApp('/tax/assessments/new');

    await pick(user, 'Année des revenus', '2025');
    await next(user);
    await user.type(within(screen.getByRole('group', { name: 'Déclarant 1' })).getByLabelText('Salaires'), '52 000,5');
    await next(user);
    await user.type(screen.getByLabelText('Revenu brut global'), '1 234,50');
    await user.type(screen.getByLabelText('Revenu imposable'), '72\u202f800');
    await next(user);
    await next(user);
    await submit(user);

    await waitFor(() => expect(sent.post).toHaveLength(1));
    expect(sent.post[0]).toMatchObject({
      grossIncome: eur('1234.50'), taxableIncome: eur('72800'), scaleTax: null, referenceIncome: null, shares: null, balance: null, documentId: null,
      declarants: [{ personId: SESSION_PERSON, rank: 1, grossSalaries: eur('52000.5'), deduction: null, perCeilingYear: null }],
    });
  });

  it('Leaves_the_lines_the_notice_only_adds_up_to_the_server', async () => {
    const sent = serve([adult(ALICE, 'Alice'), adult(BOB, 'Bob')]);
    const { user } = await renderApp(`/tax/assessments/${NOTICE}/edit`);

    expect(await screen.findByRole('combobox', { name: 'Année des revenus' })).toHaveTextContent('2025');
    await next(user);
    const first = within(screen.getByRole('group', { name: 'Déclarant 1' }));
    expect(first.queryByLabelText('Salaires, pensions et rentes nets')).toBeNull();
    await user.click(first.getByRole('button', { name: 'Plafond épargne retraite' }));
    expect(first.getByLabelText('Plafond calculé sur les revenus de 2025')).toBeInTheDocument();
    expect(first.queryByLabelText('Plafond pour les cotisations versées en 2026')).toBeNull();
    for (let i = 0; i < 3; i += 1) await next(user);
    await submit(user);

    await waitFor(() => expect(sent.put).toHaveLength(1));
    const [declarant] = (sent.put[0] as { declarants: object[] }).declarants;
    expect(declarant).toMatchObject({ grossSalaries: eur('52000.00'), deductionAmount: eur('5200.00'), perCeilingYear: eur('5843.13') });
    expect(declarant).not.toHaveProperty('netSalaries');
    expect(declarant).not.toHaveProperty('perCeilingNext');
  });

  it('Sends_the_detail_lines_under_the_notice_s_own_names', async () => {
    const sent = serve(household);
    const { user } = await renderApp('/tax/assessments/new');

    await pick(user, 'Année des revenus', '2025');
    await next(user);
    const first = within(screen.getByRole('group', { name: 'Déclarant 1' }));
    await user.type(first.getByLabelText('Heures supplémentaires et jours RTT'), '1250');
    await user.type(first.getByLabelText('RAS sur salaires et pensions'), '2700');
    await user.type(first.getByLabelText('Avance de 60 % de réductions et crédits d’impôt'), '1250');
    await next(user);
    await user.type(screen.getByLabelText('Dividendes ouvrant droit à abattement'), '5');
    await user.type(screen.getByLabelText('RCM déjà soumis aux prélèvements sociaux avec CSG déductible'), '5');
    await next(user);
    await user.type(screen.getByLabelText('Crédit d’impôt prélèvement forfaitaire déjà versé'), '1');
    await user.type(screen.getByLabelText('Investissements locatifs Pinel'), '4000');
    await user.type(screen.getByLabelText('Frais de garde des enfants de moins de 6 ans'), '1800');
    await next(user);
    await user.type(screen.getByLabelText('Déficit foncier imputable sur revenus fonciers'), '35');
    await user.type(screen.getByLabelText('Déficit foncier imputable sur revenu global'), '3200');
    await user.type(screen.getByLabelText('Déficits fonciers antérieurs non encore imputés'), '4300');
    await submit(user);

    await waitFor(() => expect(sent.post).toHaveLength(1));
    expect(sent.post[0]).toMatchObject({
      dividendsEligible: eur('5'), capitalIncomeAlreadyLevied: eur('5'), flatRatePrepaidCredit: eur('1'), pinelReduction: eur('4000'),
      childcareExpenses: eur('1800'), rentalDeficitOnRentalIncome: eur('35'), rentalDeficitOnGlobalIncome: eur('3200'), rentalDeficitCarried: eur('4300'),
      withholding: null,
      declarants: [{ rank: 1, overtimeExempt: eur('1250'), withholding: eur('2700'), creditsAdvance: eur('1250') }],
    });
  });

  it('Names_the_retirement_ceiling_lines_by_the_notice_s_years', async () => {
    serve(household);
    const { user } = await renderApp('/tax/assessments/new');

    await pick(user, 'Année des revenus', '2025');
    await next(user);
    const first = within(screen.getByRole('group', { name: 'Déclarant 1' }));
    await user.click(first.getByRole('button', { name: /Plafond épargne retraite/ }));

    for (const label of [
      'Plafond total de 2024', 'Plafond non utilisé pour les revenus de 2023', 'Plafond non utilisé pour les revenus de 2024',
      'Plafond non utilisé pour les revenus de 2025', 'Plafond calculé sur les revenus de 2025',
    ]) expect(first.getByLabelText(label)).toBeInTheDocument();
  });

  it('Warns_who_will_also_read_the_pdf', async () => {
    const sent = serve(household, { readers: [{ id: SESSION_BOB, name: 'Sam' }] });
    const { user } = await renderApp('/tax/assessments/new');

    await pick(user, 'Année des revenus', '2025');
    await pick(user, 'Déclarant 2', 'Bob Dev');
    for (let i = 0; i < 4; i += 1) await next(user);
    await pick(user, 'Avis d’imposition (PDF)', 'avis 2025.pdf');

    expect(await screen.findByText('Sam pourra aussi lire ce document')).toBeInTheDocument();
    expect(sent.previews[0]).toEqual([SESSION_PERSON, SESSION_BOB]);
  });

  it('Uploads_a_pdf_and_uses_it_as_source', async () => {
    const sent = serve(household);
    const { user } = await renderApp('/tax/assessments/new');

    await pick(user, 'Année des revenus', '2025');
    for (let i = 0; i < 4; i += 1) await next(user);
    await user.upload(screen.getByLabelText('Déposer un PDF'), pdf('avis.pdf'));
    await waitFor(() => expect(screen.getByRole('combobox', { name: 'Avis d’imposition (PDF)' })).toHaveTextContent('avis.pdf'));
    await submit(user);

    await waitFor(() => expect(sent.post).toHaveLength(1));
    expect(sent.post[0]).toMatchObject({ documentId: UPLOADED });
  });

  it('A_refused_declarant_goes_back_to_the_household_step', async () => {
    serve(household, {
      post: () => HttpResponse.json({ status: 422, code: 'request.invalid', title: 'Invalid', errors: [{ path: 'declarants[1].personId', code: 'request.invalid', message: 'Pas un adulte du foyer.' }] }, { status: 422 }),
    });
    const { user } = await renderApp('/tax/assessments/new');

    await pick(user, 'Année des revenus', '2025');
    await pick(user, 'Déclarant 2', 'Bob Dev');
    for (let i = 0; i < 4; i += 1) await next(user);
    await submit(user);

    await waitFor(() => expect(step()).toHaveTextContent('Foyer'));
    expect(screen.getByRole('combobox', { name: 'Déclarant 2' })).toHaveAttribute('aria-invalid', 'true');
    expect(screen.getByText('Pas un adulte du foyer.')).toBeInTheDocument();
  });

  it.each([
    ['tax.declarant_taken', 'declarants[0].personId', 'Cette personne a déjà un avis pour cette année.'],
    ['tax.declarant_required', 'declarants', 'Vous devez être l’un des déclarants.'],
    ['tax.invalid_assessment', 'netTax', 'Valeur refusée par le contrôle de l’avis.'],
  ])('Tells_the_refusal_%s_in_French', async (code, path, french) => {
    serve(household, { post: () => HttpResponse.json({ status: 422, code: 'request.invalid', title: 'Invalid', errors: [{ path, code, message: 'An English sentence.' }] }, { status: 422 }) });
    const { user } = await renderApp('/tax/assessments/new');

    await pick(user, 'Année des revenus', '2025');
    for (let i = 0; i < 4; i += 1) await next(user);
    await submit(user);

    expect(await screen.findByText(french)).toBeInTheDocument();
    expect(screen.queryByText('An English sentence.')).not.toBeInTheDocument();
  });

  it('Lands_on_the_new_past_year_without_estimating_the_current_one', async () => {
    const items = [jointAssessment({ year: 2023 })];
    const sent = serve(household, { post: () => { items.push(jointAssessment({ id: NOTICE, year: 2022 })); return HttpResponse.json({ id: NOTICE }, { status: 201 }); } });
    const years: (string | null)[] = [];
    server.use(
      http.get('*/api/v1/tax/assessments', () => HttpResponse.json({ items })),
      http.get('*/api/v1/tax', ({ request }) => { years.push(new URL(request.url).searchParams.get('year')); return HttpResponse.json(estimate()); }),
    );
    const { user, router } = await renderApp('/tax');
    await screen.findByText('Net fiscal');
    await user.click(screen.getByRole('link', { name: 'Ajouter un avis' }));

    await pick(user, 'Année des revenus', '2022');
    for (let i = 0; i < 4; i += 1) await next(user);
    years.length = 0;
    await submit(user);

    await waitFor(() => expect(router.state.location.search).toEqual({ year: '2022', scope: 'me' }));
    expect(await screen.findByRole('region', { name: 'Avis 2022' })).toBeInTheDocument();
    expect(years).toEqual([]);
    expect(sent.post).toHaveLength(1);
  });

  it('Keeps_the_year_in_the_breadcrumb_back_to_the_tax_page', async () => {
    serve(household);
    const { user } = await renderApp('/tax/assessments/new');

    await pick(user, 'Année des revenus', '2023');
    expect(within(screen.getByRole('navigation', { name: 'Fil d’Ariane' })).getByRole('link', { name: 'Impôts' })).toHaveAttribute('href', '/tax?year=%222023%22');
  });

  it('A_one_adult_household_creates_a_notice_without_a_second_declarant', async () => {
    const sent = serve([adult(SESSION_PERSON, 'Alice Dev')]);
    const { user, router } = await renderApp('/tax/assessments/new');

    await pick(user, 'Année des revenus', '2024');
    expect(screen.queryByRole('combobox', { name: 'Déclarant 2' })).not.toBeInTheDocument();
    for (let i = 0; i < 4; i += 1) await next(user);
    await submit(user);

    await waitFor(() => expect(router.state.location.pathname).toBe('/tax'));
    expect(router.state.location.search).toEqual({ year: '2024', scope: 'me' });
    expect((sent.post[0] as { declarants: unknown[] }).declarants).toHaveLength(1);
  });

  it('Edits_a_notice_with_its_version', async () => {
    const sent = serve([adult(ALICE, 'Alice'), adult(BOB, 'Bob')]);
    const { user, router } = await renderApp(`/tax/assessments/${NOTICE}/edit`);

    expect(await screen.findByRole('combobox', { name: 'Année des revenus' })).toHaveTextContent('2025');
    for (let i = 0; i < 3; i += 1) await next(user);
    expect(screen.getByRole('radio', { name: 'Remboursé' })).toBeChecked();
    await user.clear(screen.getByLabelText('Impôt net'));
    await next(user);
    await submit(user);

    await waitFor(() => expect(sent.put).toHaveLength(1));
    expect(sent.put[0]).toMatchObject({
      version: 1, year: 2025, netTax: null, scaleTax: eur('3200.00'), balance: eur('-312.00'), documentId: DOCUMENT, childcareExpenses: eur('1800.00'),
      declarants: [{ rank: 1 }, { rank: 2, overtimeExempt: eur('1250.00'), withholding: eur('6100.00'), creditsAdvance: eur('1250.00') }],
    });
    await waitFor(() => expect(router.state.location.pathname).toBe('/tax'));
  });

  it('A_conflict_keeps_the_values_and_says_why', async () => {
    serve([adult(ALICE, 'Alice'), adult(BOB, 'Bob')], { put: () => HttpResponse.json({ status: 409, code: 'tax.assessments.conflict', title: 'Changed.' }, { status: 409 }) });
    const { user, router } = await renderApp(`/tax/assessments/${NOTICE}/edit`);

    await screen.findByRole('combobox', { name: 'Année des revenus' });
    for (let i = 0; i < 4; i += 1) await next(user);
    await submit(user);

    expect(await screen.findByRole('alert')).toHaveTextContent('Cet élément a changé entre-temps');
    expect(router.state.location.pathname).toBe(`/tax/assessments/${NOTICE}/edit`);
    await user.click(screen.getByRole('button', { name: 'Retour' }));
    expect(screen.getByLabelText('Impôt net')).toHaveValue('2688.00');
  });

  it('Keeps_the_opened_version_after_a_refetch', async () => {
    let version = 1;
    const sent = serve([adult(ALICE, 'Alice'), adult(BOB, 'Bob')], { version: () => version });
    const { user, queryClient } = await renderApp(`/tax/assessments/${NOTICE}/edit`);

    await screen.findByRole('combobox', { name: 'Année des revenus' });
    version = 2;
    await queryClient.invalidateQueries();
    for (let i = 0; i < 4; i += 1) await next(user);
    await submit(user);

    await waitFor(() => expect(sent.put).toHaveLength(1));
    expect(sent.put[0]).toMatchObject({ version: 1 });
  });

  it('Keeps_a_source_the_reader_list_lacks', async () => {
    const sent = serve([adult(ALICE, 'Alice'), adult(BOB, 'Bob')], { documents: [] });
    const { user } = await renderApp(`/tax/assessments/${NOTICE}/edit`);

    await screen.findByRole('combobox', { name: 'Année des revenus' });
    for (let i = 0; i < 4; i += 1) await next(user);
    const field = screen.getByRole('combobox', { name: 'Avis d’imposition (PDF)' });
    expect(field).toHaveTextContent('Document actuel');
    await user.click(field);
    expect(within(await screen.findByRole('listbox')).queryByRole('option', { name: 'Aucun' })).not.toBeInTheDocument();
    await user.keyboard('{Escape}');
    await submit(user);

    await waitFor(() => expect(sent.put).toHaveLength(1));
    expect(sent.put[0]).toMatchObject({ documentId: DOCUMENT });
  });

  it('Waits_for_a_running_upload_before_saving', async () => {
    const sent = serve(household, { slowUpload: true });
    const { user } = await renderApp('/tax/assessments/new');

    await pick(user, 'Année des revenus', '2025');
    for (let i = 0; i < 4; i += 1) await next(user);
    await user.upload(screen.getByLabelText('Déposer un PDF'), pdf('avis.pdf'));
    expect(screen.getByRole('button', { name: 'Enregistrer l’avis' })).toBeDisabled();
    await waitFor(() => expect(screen.getByRole('button', { name: 'Enregistrer l’avis' })).toBeEnabled());
    await submit(user);

    await waitFor(() => expect(sent.post).toHaveLength(1));
    expect(sent.post[0]).toMatchObject({ documentId: UPLOADED });
  });
});
