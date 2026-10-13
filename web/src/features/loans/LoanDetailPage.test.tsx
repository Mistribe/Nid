import { screen, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { LoanDetail, LoanSchedule } from './api';
import { LOAN, loanDetail, rows, schedule, year } from './testData';

function serve(detail: LoanDetail = loanDetail(), plan: LoanSchedule = schedule()) {
  server.use(
    http.get('*/api/v1/realestate/loans/:id', () => HttpResponse.json(detail)),
    http.get('*/api/v1/realestate/loans/:id/schedule', () => HttpResponse.json(plan)),
  );
}

const heading = () => screen.findByRole('heading', { name: 'Prêt maison', level: 1 });
const figure = (term: string) =>
  within(screen.getByRole('region', { name: 'Aujourd’hui' })).getByText(term, { selector: 'dt' }).nextElementSibling;
const table = () => screen.getByRole('table', { name: 'Échéancier par année' });
const yearButton = (y: number) => within(table()).getByRole('button', { name: `Année ${y}` });
const bodyRows = (scope: HTMLElement, name: string) => within(within(scope).getByRole('table', { name })).getAllByRole('row').slice(1);

describe('LoanDetailPage', () => {
  // The lazy route chunk loaded cold inside the first test races its timeouts under load.
  beforeAll(async () => { await import('./LoanDetailPage'); }, 30_000);
  // Today's payment and the open year read the browser's today: pinned to 2026-10-12.
  beforeEach(() => { vi.useFakeTimers({ toFake: ['Date'] }); vi.setSystemTime(new Date(2026, 9, 12, 12)); });
  afterEach(() => { vi.useRealTimers(); });

  it('shows the header and every figure of today, as the server computed them', async () => {
    serve();
    const { container } = await renderApp(`/loans/${LOAN}`);

    expect(await heading()).toBeInTheDocument();
    expect(screen.getByText('Banque')).toBeInTheDocument();
    expect(screen.getByText('Privé')).toBeInTheDocument();
    expect(figure('Capital restant dû')).toHaveTextContent('193 564,23 €');
    expect(figure('Capital emprunté')).toHaveTextContent('200 000,00 €');
    expect(figure('Prochaine échéance')).toHaveTextContent('15 octobre 2026 : 1 159,92 €, assurance comprise');
    expect(figure('Taux actuel')).toHaveTextContent('3,5 % (phase 1)');
    expect(figure('Échéances restantes')).toHaveTextContent('229');
    expect(figure('Dernière échéance')).toHaveTextContent('15 octobre 2045');
    expect(figure('Capital remboursé')).toHaveTextContent('6 435,77 €');
    expect(figure('Intérêts à ce jour')).toHaveTextContent('6 323,35 €');
    expect(figure('Assurance payée')).toHaveTextContent('0,00 €');
    expect(figure('Frais payés')).toHaveTextContent('0,00 €');
    expect(figure('Coût total du crédit')).toHaveTextContent('78 380,66 €');
    expect(figure('Coût à ce jour')).toHaveTextContent('6 323,35 €');
    expect(figure('Coût à venir')).toHaveTextContent('72 057,31 €');
    expect(screen.queryByText('Terminé')).not.toBeInTheDocument();
    await screen.findByRole('table', { name: 'Échéancier par année' });
    await expectNoAxeViolations(container);
  });

  it('says an ended loan has no next payment and no current phase, though the server names its last one', async () => {
    const figures = { ...loanDetail().figures, balance: '0.00', paymentsLeft: 0, nextPaymentOn: null, nextPaymentTotal: null };
    serve(loanDetail({ ended: true, figures }));
    const { container } = await renderApp(`/loans/${LOAN}`);

    await heading();
    expect(screen.getByText('Terminé')).toBeInTheDocument();
    expect(figure('Prochaine échéance')).toHaveTextContent('Aucune : le crédit est terminé.');
    expect(within(screen.getByRole('region', { name: 'Aujourd’hui' })).queryByText('Taux actuel')).not.toBeInTheDocument();
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
    const { container } = await renderApp(`/loans/${LOAN}`);

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

  it('lists the fees and early repayments when there are any', async () => {
    serve(loanDetail({
      fees: [
        { id: 'f1', kind: 'file', amount: '1000.00', onDate: '2025-10-15', note: 'dossier', repaymentId: null, version: 1 },
        { id: 'f2', kind: 'early_repayment_penalty', amount: '300.00', onDate: '2026-03-20', note: null, repaymentId: 'r1', version: 2 },
      ],
      repayments: [{ id: 'r1', onDate: '2026-03-20', amount: '10000.00', effect: 'reduce_payment', penalty: '300.00', note: 'prime', version: 3 }],
    }));
    const { container } = await renderApp(`/loans/${LOAN}`);

    await heading();
    const region = screen.getByRole('region', { name: 'Frais et remboursements anticipés' });
    const fees = bodyRows(region, 'Frais');
    expect(fees[0]).toHaveTextContent('15 octobre 2025');
    expect(fees[0]).toHaveTextContent('Frais de dossier');
    expect(fees[0]).toHaveTextContent('1 000,00 €');
    expect(fees[0]).toHaveTextContent('dossier');
    expect(fees[1]).toHaveTextContent('Pénalité de remboursement anticipé');
    const repayments = bodyRows(region, 'Remboursements anticipés');
    expect(repayments[0]).toHaveTextContent('20 mars 2026');
    expect(repayments[0]).toHaveTextContent('Mensualité réduite');
    expect(repayments[0]).toHaveTextContent('10 000,00 €');
    expect(repayments[0]).toHaveTextContent('300,00 €');
    expect(repayments[0]).toHaveTextContent('prime');
    await expectNoAxeViolations(container);
  });

  it('shows no fee or repayment section without any', async () => {
    serve();
    await renderApp(`/loans/${LOAN}`);

    await heading();
    expect(screen.queryByRole('region', { name: 'Frais et remboursements anticipés' })).not.toBeInTheDocument();
  });

  it('reads the schedule by year, the current year open and today’s payment marked in words', async () => {
    serve();
    const { user, container } = await renderApp(`/loans/${LOAN}`);

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
    await renderApp(`/loans/${LOAN}`);

    await screen.findByRole('table', { name: 'Échéancier par année' });
    expect(yearButton(2027)).toHaveAttribute('aria-expanded', 'true');
    expect(yearButton(2026)).toHaveAttribute('aria-expanded', 'false');
    expect(within(table()).getByText('prochaine échéance').closest('tr')).toHaveTextContent('15 janvier 2027');
  });

  it('marks the figures’ next payment, a payment due today counting as paid', async () => {
    vi.setSystemTime(new Date(2026, 9, 15, 12));
    serve(loanDetail({ figures: { ...loanDetail().figures, nextPaymentOn: '2026-11-15' } }));
    await renderApp(`/loans/${LOAN}`);

    await screen.findByRole('table', { name: 'Échéancier par année' });
    const marked = within(table()).getByText('prochaine échéance').closest('tr')!;
    expect(marked).toHaveTextContent('15 novembre 2026');
    expect(within(marked).getAllByRole('cell')[0]).toHaveTextContent('13');
  });

  it('folds every year and marks nothing for an ended loan', async () => {
    vi.setSystemTime(new Date(2028, 2, 1, 12));
    serve(loanDetail({ ended: true, figures: { ...loanDetail().figures, nextPaymentOn: null, nextPaymentTotal: null } }));
    await renderApp(`/loans/${LOAN}`);

    await screen.findByRole('table', { name: 'Échéancier par année' });
    expect(within(table()).getAllByRole('row')).toHaveLength(1 + 3);
    expect(within(table()).queryByText('prochaine échéance')).not.toBeInTheDocument();
  });

  it('renders only the open year’s months of a 600-payment schedule', async () => {
    const years = Array.from({ length: 50 }, (_, i) => year({ year: 2026 + i }));
    serve(loanDetail(), schedule({ rows: rows('2026-01-15', 600), years }));
    const { user } = await renderApp(`/loans/${LOAN}`);

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

  it('tells a reader without write access the page is read-only, without settings', async () => {
    serve(loanDetail({ canWrite: false, visibility: 'shared' }));
    const { container } = await renderApp(`/loans/${LOAN}`);

    expect(await screen.findByText('Vous pouvez consulter ce crédit, pas le modifier.')).toBeInTheDocument();
    expect(screen.getByText('Partagé')).toBeInTheDocument();
    expect(screen.queryByRole('form', { name: 'Réglages' })).not.toBeInTheDocument();
    expect(screen.queryByRole('textbox')).not.toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Modifier le contrat' })).not.toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('says an unknown or unreadable loan is not found', async () => {
    server.use(http.get('*/api/v1/realestate/loans/:id', () => HttpResponse.json({ status: 404, code: null, title: 'Not found' }, { status: 404 })));
    await renderApp(`/loans/${LOAN}`);

    expect(await screen.findByText('Cet élément est introuvable.')).toBeInTheDocument();
  });
});
