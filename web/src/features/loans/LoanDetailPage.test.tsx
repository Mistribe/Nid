import { link, list, summary } from '../documents/testData';
import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { LoanDetail, LoanSchedule } from './api';
import { PROPERTY } from '../properties/testData';
import { pick } from '@/test/controls';
import { agreeing, LOAN, loanDetail, rows, schedule, withTable, year } from './testData';

function serve(detail: LoanDetail = loanDetail(), plan: LoanSchedule = schedule()) {
  server.use(
    http.get('*/api/v1/realestate/loans/:id', () => HttpResponse.json(detail)),
    http.get('*/api/v1/realestate/loans/:id/schedule', () => HttpResponse.json(plan)),
  );
}

const at = (tab: string) => `/loans/${LOAN}?tab=${tab}`;
const heading = () => screen.findByRole('heading', { name: 'Prêt maison', level: 1 });
const figure = (term: string) =>
  within(screen.getByRole('region', { name: 'Aujourd’hui' })).getByText(term, { selector: 'dt' }).nextElementSibling;
const detail = (term: string) =>
  within(screen.getByRole('region', { name: 'Coût du crédit' })).getByText(term, { selector: 'dt' }).nextElementSibling;
const table = () => screen.getByRole('table', { name: 'Échéancier par année' });
const yearButton = (y: number) => within(table()).getByRole('button', { name: `Année ${y}` });
const bodyRows = (scope: HTMLElement, name: string) => within(within(scope).getByRole('table', { name })).getAllByRole('row').slice(1);

describe('LoanDetailPage', () => {
  // The lazy route chunk and the lazy schedule chart loaded cold inside the first test race their timeouts under load.
  beforeAll(async () => { await import('./LoanDetailPage'); await import('./chart/ScheduleChart'); }, 30_000);
  // Today's payment and the open year read the browser's today: pinned to 2026-10-12.
  beforeEach(() => { vi.useFakeTimers({ toFake: ['Date'] }); vi.setSystemTime(new Date(2026, 9, 12, 12)); });
  afterEach(() => { vi.useRealTimers(); });

  it('overview answers where the loan stands', async () => {
    serve();
    const { container } = await renderApp(`/loans/${LOAN}`);

    expect(await heading()).toBeInTheDocument();
    expect(screen.getByText('Banque')).toBeInTheDocument();
    expect(screen.getByText('Privé')).toBeInTheDocument();
    expect(figure('Restant dû')).toHaveTextContent('193 564,23 €');
    expect(figure('Prochaine échéance')).toHaveTextContent('15 octobre 2026 : 1 159,92 €');
    expect(figure('Taux actuel')).toHaveTextContent('3,5 % (phase 1)');
    expect(figure('Fin')).toHaveTextContent('15 octobre 2045 · 229 échéances restantes');
    // The bar is sized by plotting numbers; what a person reads is the server's two strings.
    const spaced = (text: string) => text.replace(/\s/g, ' ');
    const bar = screen.getByRole('progressbar', { name: (name) => spaced(name) === '6 435,77 € remboursés sur 200 000,00 €' });
    expect(spaced(bar.getAttribute('aria-valuetext') ?? '')).toBe('6 435,77 € remboursés sur 200 000,00 €');
    expect(bar).not.toHaveTextContent('%');
    expect(Number(bar.getAttribute('aria-valuenow'))).toBeGreaterThan(3);
    expect(Number(bar.getAttribute('aria-valuenow'))).toBeLessThan(4);
    expect(detail('Intérêts à ce jour')).toHaveTextContent('6 323,35 €');
    expect(detail('Assurance payée')).toHaveTextContent('0,00 €');
    expect(detail('Frais payés')).toHaveTextContent('0,00 €');
    expect(detail('Coût total du crédit')).toHaveTextContent('78 380,66 €');
    expect(detail('Coût à ce jour')).toHaveTextContent('6 323,35 €');
    expect(detail('Coût à venir')).toHaveTextContent('72 057,31 €');
    // The next three payments, from the one the figures name.
    const next = await screen.findByRole('table', { name: 'Prochaines échéances' });
    const upcoming = within(next).getAllByRole('row').slice(1);
    expect(upcoming.map((r) => within(r).getAllByRole('cell')[0]!.textContent)).toEqual(['12', '13', '14']);
    expect(upcoming[0]).toHaveTextContent('15 octobre 2026');
    expect(await screen.findByRole('figure', { name: 'Graphique de l’échéancier par année' })).toBeInTheDocument();
    expect(screen.queryByText('Terminé')).not.toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('shows the five tabs, the first out of the URL', async () => {
    serve();
    const { router } = await renderApp(`/loans/${LOAN}`);

    await heading();
    expect(screen.getAllByRole('tab').map((tab) => tab.textContent)).toEqual(['Vue d’ensemble', 'Échéancier', 'Opérations', 'Contrat et réglages', 'Documents']);
    expect(screen.getByRole('tab', { name: 'Vue d’ensemble' })).toHaveAttribute('aria-selected', 'true');
    expect(router.state.location.search).not.toHaveProperty('tab');
  });

  it('says an ended loan has no next payment and no current phase, though the server names its last one', async () => {
    const figures = { ...loanDetail().figures, balance: '0.00', paymentsLeft: 0, nextPaymentOn: null, nextPaymentTotal: null };
    serve(loanDetail({ ended: true, figures }));
    const { container } = await renderApp(`/loans/${LOAN}`);

    await heading();
    expect(screen.getByText('Terminé')).toBeInTheDocument();
    expect(figure('Prochaine échéance')).toHaveTextContent('Aucune : le crédit est terminé.');
    expect(within(screen.getByRole('region', { name: 'Aujourd’hui' })).queryByText('Taux actuel')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Simuler un remboursement' })).not.toBeInTheDocument();
    expect(screen.queryByRole('table', { name: 'Prochaines échéances' })).not.toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('lists each warning in French with its payment number', async () => {
    serve(loanDetail({
      warnings: [
        { code: 'projection_uses_last_rate', row: 61 }, { code: 'exceeds_total_payments', row: 241 },
        { code: 'event_off_payment_date', row: 12 }, { code: 'event_off_payment_date', row: 0 },
        { code: 'override_ignored_after_early_repayment', row: 30 }, { code: 'brand_new', row: 3 },
      ],
    }));
    const { container } = await renderApp(`/loans/${LOAN}`);

    const warnings = within(await screen.findByRole('list', { name: 'Avertissements' })).getAllByRole('listitem');
    expect(warnings.map((w) => w.textContent)).toEqual([
      'Au-delà des phases connues, le calcul prolonge le dernier taux (à partir de l’échéance 61)',
      'La mensualité imposée ne suffit pas : le prêt dépasse sa durée prévue (échéance 241)',
      'Remboursement daté hors échéance (appliqué après l’échéance 12)',
      'Remboursement daté avant la première échéance',
      'Mensualité imposée ignorée après le remboursement de l’échéance 30',
      'brand_new',
    ]);
    await expectNoAxeViolations(container);
  });

  it('reads the contract: its terms, its phases and its insurances', async () => {
    const contract = {
      ...loanDetail().contract,
      phases: [
        { seq: 1, mode: 'deferred_partial', annualRate: '0.035', rateType: 'fixed', payments: 24, paymentOverride: null, openingBalanceOverride: null, note: 'travaux' },
        { seq: 2, mode: 'annuity', annualRate: '0.0412', rateType: 'variable', payments: null, paymentOverride: '1250.00', openingBalanceOverride: '150000.00', note: null },
      ],
      insurances: [
        { mode: 'fixed_monthly', monthlyAmount: '20.00', annualRate: null, startsOn: '2025-11-15', endsOn: null, note: null },
        { mode: 'rate_on_initial', monthlyAmount: null, annualRate: '0.0036', startsOn: '2025-11-15', endsOn: '2035-11-15', note: 'Alice' },
      ],
    };
    serve(loanDetail({ contract }));
    const { container } = await renderApp(at('contract'));

    await heading();
    const region = screen.getByRole('region', { name: 'Contrat' });
    expect(region).toHaveTextContent('15 octobre 2025');
    expect(region).toHaveTextContent('15 novembre 2025');
    expect(region).toHaveTextContent('240 mois');
    expect(region).toHaveTextContent('Au plus proche');
    expect(region).toHaveTextContent('Mois complet');
    const phases = bodyRows(region, 'Phases');
    expect(phases[0]).toHaveTextContent('Différé partiel (intérêts seuls)');
    expect(phases[0]).toHaveTextContent('3,5 %');
    expect(phases[0]).toHaveTextContent('Fixe');
    expect(phases[0]).toHaveTextContent('24');
    expect(phases[0]).toHaveTextContent('travaux');
    expect(phases[1]).toHaveTextContent('Échéances constantes');
    expect(phases[1]).toHaveTextContent('4,12 %');
    expect(phases[1]).toHaveTextContent('Variable');
    expect(phases[1]).toHaveTextContent('jusqu’au terme');
    expect(phases[1]).toHaveTextContent('1 250,00 €');
    expect(phases[1]).toHaveTextContent('150 000,00 €');
    const insurances = bodyRows(region, 'Assurances');
    expect(insurances[0]).toHaveTextContent('Montant fixe par mois');
    expect(insurances[0]).toHaveTextContent('20,00 € par mois');
    expect(insurances[0]).toHaveTextContent('sans fin');
    expect(insurances[1]).toHaveTextContent('Taux sur le capital initial');
    expect(insurances[1]).toHaveTextContent('0,36 %');
    expect(insurances[1]).toHaveTextContent('15 novembre 2035');
    expect(insurances[1]).toHaveTextContent('Alice');
    await expectNoAxeViolations(container);
  });

  it('reads the schedule by year, the current year open and today’s payment marked in words', async () => {
    serve();
    const { user, container } = await renderApp(at('schedule'));

    await screen.findByRole('table', { name: 'Échéancier par année' });
    expect(yearButton(2026)).toHaveAttribute('aria-expanded', 'true');
    expect(yearButton(2025)).toHaveAttribute('aria-expanded', 'false');
    expect(yearButton(2027)).toHaveAttribute('aria-expanded', 'false');
    // The header, three year lines, and the twelve months of 2026.
    expect(within(table()).getAllByRole('row')).toHaveLength(1 + 3 + 12);
    const y2025 = yearButton(2025).closest('tr')!;
    expect(y2025).toHaveTextContent('2 319,84 €');
    expect(y2025).toHaveTextContent('1 164,98 €');
    expect(y2025).toHaveTextContent('1 154,86 €');
    expect(y2025).toHaveTextContent('198 845,14 €');
    const marked = within(table()).getByText('prochaine échéance').closest('tr')!;
    expect(marked).toHaveTextContent('15 octobre 2026');
    expect(within(marked).getAllByRole('cell')[0]).toHaveTextContent('12');
    await expectNoAxeViolations(container);

    await user.click(yearButton(2027));
    expect(yearButton(2027)).toHaveAttribute('aria-expanded', 'true');
    expect(within(table()).getAllByRole('row')).toHaveLength(1 + 3 + 12 + 10);
    await user.click(yearButton(2026));
    expect(yearButton(2026)).toHaveAttribute('aria-expanded', 'false');
    expect(within(table()).getAllByRole('row')).toHaveLength(1 + 3 + 10);
  });

  it('opens the next payment’s year late in December, though this year still has payments, and marks that payment', async () => {
    vi.setSystemTime(new Date(2026, 11, 20, 12));
    serve(loanDetail({ figures: { ...loanDetail().figures, nextPaymentOn: '2027-01-15' } }));
    await renderApp(at('schedule'));

    await screen.findByRole('table', { name: 'Échéancier par année' });
    expect(yearButton(2027)).toHaveAttribute('aria-expanded', 'true');
    expect(yearButton(2026)).toHaveAttribute('aria-expanded', 'false');
    expect(within(table()).getByText('prochaine échéance').closest('tr')).toHaveTextContent('15 janvier 2027');
  });

  it('marks the figures’ next payment, a payment due today counting as paid', async () => {
    vi.setSystemTime(new Date(2026, 9, 15, 12));
    serve(loanDetail({ figures: { ...loanDetail().figures, nextPaymentOn: '2026-11-15' } }));
    await renderApp(at('schedule'));

    await screen.findByRole('table', { name: 'Échéancier par année' });
    const marked = within(table()).getByText('prochaine échéance').closest('tr')!;
    expect(marked).toHaveTextContent('15 novembre 2026');
    expect(within(marked).getAllByRole('cell')[0]).toHaveTextContent('13');
  });

  it('folds every year and marks nothing for an ended loan', async () => {
    vi.setSystemTime(new Date(2028, 2, 1, 12));
    serve(loanDetail({ ended: true, figures: { ...loanDetail().figures, nextPaymentOn: null, nextPaymentTotal: null } }));
    await renderApp(at('schedule'));

    await screen.findByRole('table', { name: 'Échéancier par année' });
    expect(within(table()).getAllByRole('row')).toHaveLength(1 + 3);
    expect(within(table()).queryByText('prochaine échéance')).not.toBeInTheDocument();
  });

  it('renders only the open year’s months of a 600-payment schedule', async () => {
    const years = Array.from({ length: 50 }, (_, i) => year({ year: 2026 + i }));
    serve(loanDetail(), schedule({ rows: rows('2026-01-15', 600), years }));
    const { user } = await renderApp(at('schedule'));

    await screen.findByRole('table', { name: 'Échéancier par année' });
    expect(within(table()).getAllByRole('row')).toHaveLength(1 + 50 + 12);
    await user.click(yearButton(2060));
    expect(within(table()).getAllByRole('row')).toHaveLength(1 + 50 + 24);
  });

  it('draws the yearly chart on demand, described by a text summary of the server’s totals', async () => {
    serve();
    const { container } = await renderApp(`/loans/${LOAN}`);

    const chart = await screen.findByRole('figure', { name: 'Graphique de l’échéancier par année' });
    const summary = screen.getByText('Échéancier de 2025 à 2027 : 78 380,66 € d’intérêts et 200 000,00 € de capital au total.');
    expect(chart).toHaveAttribute('aria-describedby', summary.id);
    await expectNoAxeViolations(container);
  });

  it('a reader sees no write action', async () => {
    serve(loanDetail({ canWrite: false, visibility: 'shared', fees: [{ id: 'f1', kind: 'file', amount: '1000.00', onDate: '2025-10-15', note: null, repaymentId: null, version: 1 }] }));
    const { user, container } = await renderApp(`/loans/${LOAN}`);

    expect(await screen.findByText('Vous pouvez consulter ce crédit, pas le modifier.')).toBeInTheDocument();
    expect(screen.getByText('Partagé')).toBeInTheDocument();
    // The simulation reads the loan as stored: it stays.
    expect(screen.getByRole('button', { name: 'Simuler un remboursement' })).toBeInTheDocument();
    await user.click(screen.getByRole('tab', { name: 'Opérations' }));
    await screen.findByRole('table', { name: 'Frais du crédit' });
    expect(screen.queryByRole('button', { name: 'Ajouter des frais' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Ajouter un remboursement' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /^Actions pour/ })).not.toBeInTheDocument();
    await user.click(screen.getByRole('tab', { name: 'Contrat et réglages' }));
    expect(screen.queryByRole('form', { name: 'Réglages' })).not.toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Modifier le contrat' })).not.toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('the schedule tab keeps today’s row marked and the bank comparison', async () => {
    serve(withTable({ comparison: agreeing({ maxGap: '0.02', interestGap: '-0.03' }) }));
    const { container } = await renderApp(at('schedule'));

    const marked = (await within(await screen.findByRole('table', { name: 'Échéancier par année' })).findByText('prochaine échéance')).closest('tr')!;
    expect(marked).toHaveTextContent('15 octobre 2026');
    const bank = await screen.findByRole('region', { name: 'Tableau de la banque' });
    expect(within(bank).getByRole('list', { name: 'Comparaison avec le calcul' })).toHaveTextContent('Les deux concordent');
    await expectNoAxeViolations(container);
  });

  it('the simulation is reachable from the header', async () => {
    serve();
    const { user, router } = await renderApp(`/loans/${LOAN}`);

    await heading();
    await user.click(screen.getByRole('button', { name: 'Simuler un remboursement' }));

    expect(await screen.findByRole('region', { name: 'Simuler un remboursement anticipé' })).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: 'Opérations' })).toHaveAttribute('aria-selected', 'true');
    expect(router.state.location.search).toMatchObject({ tab: 'operations' });
    await waitFor(() => expect(screen.getByRole('heading', { name: 'Simuler un remboursement anticipé' })).toHaveFocus());
  });

  it('operations add a fee and a repayment in dialogs', async () => {
    const sent: { path: string; body: unknown }[] = [];
    serve();
    server.use(
      http.post('*/api/v1/realestate/loans/:id/fees', async ({ request }) => { sent.push({ path: 'fees', body: await request.json() }); return HttpResponse.json({ id: 'f9' }, { status: 201 }); }),
      http.post('*/api/v1/realestate/loans/:id/repayments', async ({ request }) => { sent.push({ path: 'repayments', body: await request.json() }); return HttpResponse.json({ id: 'r9' }, { status: 201 }); }),
    );
    const { user, container } = await renderApp(at('operations'));

    await user.click(await screen.findByRole('button', { name: 'Ajouter des frais' }));
    const fee = within(await screen.findByRole('dialog', { name: 'Nouveaux frais' }));
    await pick(user, 'Nature', 'Garantie');
    await user.type(fee.getByLabelText('Montant'), '1500,50');
    await user.clear(fee.getByLabelText('Date'));
    await user.type(fee.getByLabelText('Date'), '20/10/2025');
    await expectNoAxeViolations(container);
    await user.click(fee.getByRole('button', { name: 'Ajouter' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(sent).toEqual([{ path: 'fees', body: { kind: 'guarantee', amount: '1500.50', onDate: '2025-10-20' } }]);
    expect(screen.getByText('Frais ajoutés.')).toHaveAttribute('role', 'status');
    expect(screen.getByRole('heading', { name: 'Frais' })).toHaveFocus();

    await user.click(screen.getByRole('button', { name: 'Ajouter un remboursement' }));
    const repayment = within(await screen.findByRole('dialog', { name: 'Nouveau remboursement anticipé' }));
    await user.type(repayment.getByLabelText('Montant'), '5000');
    await user.click(repayment.getByRole('radio', { name: 'Réduire la durée' }));
    await user.click(repayment.getByRole('button', { name: 'Ajouter' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(sent[1]).toEqual({ path: 'repayments', body: expect.objectContaining({ amount: '5000', effect: 'reduce_term' }) as unknown });
    expect(screen.getByText('Remboursement anticipé ajouté.')).toHaveAttribute('role', 'status');
  });

  it('says an unknown or unreadable loan is not found', async () => {
    server.use(http.get('*/api/v1/realestate/loans/:id', () => HttpResponse.json({ status: 404, code: null, title: 'Not found' }, { status: 404 })));
    await renderApp(`/loans/${LOAN}`);

    expect(await screen.findByText('Cet élément est introuvable.')).toBeInTheDocument();
  });

  it('names the property the loan finances, with a link to it', async () => {
    serve(loanDetail({ propertyId: PROPERTY, propertyName: 'Maison de Nantes' }));
    const { container } = await renderApp(`/loans/${LOAN}`);

    await heading();
    const link = screen.getByRole('link', { name: 'Maison de Nantes' });
    expect(link).toHaveAttribute('href', `/properties/${PROPERTY}`);
    expect(link.parentElement).toHaveTextContent('Bien financé : Maison de Nantes');
    await expectNoAxeViolations(container);
  });

  it('says a financed property the reader cannot read is one, without naming it', async () => {
    serve(loanDetail({ propertyId: PROPERTY, propertyName: null }));
    await renderApp(`/loans/${LOAN}`);

    await heading();
    expect(screen.getByText('Bien financé : un bien que vous ne pouvez pas consulter')).toBeInTheDocument();
  });

  it('says nothing of a property for a loan without one', async () => {
    serve();
    await renderApp(`/loans/${LOAN}`);

    await heading();
    // The line only: the settings' select keeps its label « Bien financé », without the colon.
    expect(screen.queryByText(/^Bien financé :/)).not.toBeInTheDocument();
  });

  it('has a documents tab, last, listing the documents anchored on the loan', async () => {
    serve();
    server.use(http.get('*/api/v1/documents', () => HttpResponse.json(list([summary({ title: 'Pièce du dossier', links: [link({ targetId: LOAN, anchorItemId: LOAN })] })]))));
    const { user } = await renderApp(`/loans/${LOAN}`);

    await screen.findAllByRole('tab');
    const names = screen.getAllByRole('tab').map((tab) => tab.textContent);
    expect(names.at(-1)).toBe('Documents');
    await user.click(screen.getByRole('tab', { name: 'Documents' }));
    expect((await screen.findAllByText('Pièce du dossier')).length).toBeGreaterThan(0);
  });
});
