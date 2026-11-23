import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { pick } from '@/test/controls';
import { adminCapabilities } from '@/test/fixtures';
import { expectHeaderActionRule, expectOnePrimary } from '@/test/headerActions';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { toasts } from '@/test/toasts';
import { formatInstant, formatInstantDate, formatInstantTime } from '@/lib/format';

const settings = { maxAdults: 2, referenceCurrency: 'EUR', adultCount: 2, pricesEnabled: false, irlEnabled: false };
const run = {
  id: '01926b9e-0000-7000-8000-0000000000f3', startedAt: '2026-10-29T03:30:00+00:00', finishedAt: '2026-10-29T03:31:00+00:00',
  trigger: 'schedule', outcome: 'succeeded', connections: 2, accounts: 5, balances: 9, proposals: 3, written: 4, skipped: 1, failureCode: null, rateLimited: 2,
};
const failed = { ...run, id: '01926b9e-0000-7000-8000-0000000000f4', trigger: 'manual', outcome: 'failed', failureCode: 'enablebanking.EXPIRED_SESSION' };
const nextRunAt = '2026-10-30T04:30:00+00:00';
/** Everything set up: an adult with their own application, a bank linked, the last night a success. */
const ready = { lastRun: run, nextRunAt, bankLinked: true, ownApplications: [{ personId: '01926b9e-0000-7000-8000-00000000000a', name: 'Alice' }] };

function serve(household: object, runs: unknown[] = [], total = runs.length, seen: string[] = [], diagnostics: object = ready) {
  const sent: unknown[] = [];
  server.use(
    http.get('*/api/v1/admin/banking/diagnostics', () => HttpResponse.json(diagnostics)),
    http.get('*/api/v1/admin/household', () => HttpResponse.json({ ...settings, ...household })),
    http.get('*/api/v1/admin/banking/runs', ({ request }) => {
      seen.push(request.url);
      return HttpResponse.json({ items: runs, total, page: 1 });
    }),
    http.patch('*/api/v1/admin/household', async ({ request }) => {
      const body = await request.json() as object;
      sent.push(body);
      return HttpResponse.json({ ...settings, ...household, ...body });
    }),
  );
  return sent;
}

const on = { bankingEnabled: true };
const figures = () => screen.getByRole('region', { name: 'État de la synchronisation bancaire' });
const setup = () => screen.queryByRole('region', { name: 'Mise en route' });

describe('Administration › Banque', () => {
  it('shows a ready sync: its state, the last and next runs, no checklist, and the journal with readable outcomes', async () => {
    serve(on, [failed, run]);
    const { container, user } = await renderApp('/admin/banking', { capabilities: adminCapabilities });

    const row = await screen.findByRole('row', { name: /Réussie/ });
    expect(row).toHaveTextContent(formatInstant(run.startedAt));
    expect(within(row).getAllByRole('cell').map((c) => c.textContent).slice(1)).toEqual(['Nuit', 'Réussie', '2', '5', '9', '3', '4', '1', '2', '']);
    const error = screen.getByRole('row', { name: /Échouée/ });
    expect(error).toHaveTextContent('Le consentement d’une banque a expiré');
    // The code sits in an InfoTip, not in the sentence.
    expect(error).not.toHaveTextContent('EXPIRED_SESSION');
    await user.hover(within(error).getByRole('button', { name: 'À propos : Échec' }));
    expect(await screen.findByRole('tooltip')).toHaveTextContent('Code technique : enablebanking.EXPIRED_SESSION');

    expect(figures()).toHaveTextContent('ÉtatPrête');
    expect(figures()).toHaveTextContent(`Dernière synchronisation${formatInstantDate(run.startedAt)}à ${formatInstantTime(run.startedAt)} · Réussie`);
    expect(figures()).toHaveTextContent(`Prochaine synchronisation${formatInstantDate(nextRunAt)}à ${formatInstantTime(nextRunAt)}, puis chaque nuit`);
    expect(setup()).toBeNull();
    expect(screen.getByRole('heading', { level: 1, name: 'Banque' })).toBeInTheDocument();
    expect(within(screen.getByRole('navigation', { name: 'Fil d’Ariane' })).getByRole('link', { name: 'Administration' })).toHaveAttribute('href', '/admin');
    expectHeaderActionRule(container);
    expectOnePrimary(container);
    await expectNoAxeViolations(container);
  });

  it('sends the household to « Mon application » while no adult has one: no installation id, checklist or test (#414)', async () => {
    serve(on, [], 0, [], { lastRun: null, nextRunAt, bankLinked: false, ownApplications: [] });
    const { container } = await renderApp('/admin/banking', { capabilities: adminCapabilities });

    const region = await screen.findByRole('region', { name: 'Mise en route' });
    expect(figures()).toHaveTextContent('ÉtatÀ configurer');
    expect(region).toHaveTextContent('Chaque adulte crée sa propre application Enable Banking');
    expect(within(region).getByRole('link', { name: 'Ouvrir Mon application' })).toHaveAttribute('href', '/settings/banks?tab=application');
    expect(within(region).queryByRole('listitem')).toBeNull();
    expect(screen.queryByLabelText('Identifiant de l’application')).toBeNull();
    expect(screen.queryByLabelText('Adresse de retour')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Tester la connexion' })).toBeNull();
    expect(screen.queryByRole('button', { name: /Transférer/ })).toBeNull();
    await expectNoAxeViolations(container);
  });

  it('is « À configurer » while no bank is linked', async () => {
    serve(on, [], 0, [], { ...ready, lastRun: null, bankLinked: false });
    await renderApp('/admin/banking', { capabilities: adminCapabilities });

    expect(await screen.findByRole('region', { name: 'Mise en route' })).toBeInTheDocument();
    expect(figures()).toHaveTextContent('ÉtatÀ configurer');
    expect(figures()).toHaveTextContent('Dernière synchronisationAucune');
  });

  it('is « En erreur » when the last run failed', async () => {
    serve(on, [failed], 1, [], { ...ready, lastRun: failed });
    await renderApp('/admin/banking', { capabilities: adminCapabilities });

    expect(await screen.findByRole('region', { name: 'État de la synchronisation bancaire' })).toHaveTextContent('ÉtatEn erreur');
    expect(figures()).toHaveTextContent(`Dernière synchronisation${formatInstantDate(failed.startedAt)}à ${formatInstantTime(failed.startedAt)} · Échouée`);
  });

  it('says when the first sync runs, and where to start one now', async () => {
    serve(on, [], 0, [], { ...ready, lastRun: null });
    await renderApp('/admin/banking', { capabilities: adminCapabilities });

    const journal = await screen.findByRole('region', { name: 'Journal des synchronisations' });
    expect(await within(journal).findByText(`La première aura lieu le ${formatInstant(nextRunAt)}, puis chaque nuit. « Synchroniser », dans Réglages › Banques, en lance une tout de suite.`))
      .toBeInTheDocument();
    expect(within(journal).getByRole('link', { name: 'Ouvrir Réglages › Banques' })).toHaveAttribute('href', '/settings/banks');
  });

  it('says no night is scheduled when this server runs no job', async () => {
    serve(on, [], 0, [], { ...ready, lastRun: null, nextRunAt: null });
    await renderApp('/admin/banking', { capabilities: adminCapabilities });

    expect(await screen.findByText(/Ce serveur ne lance pas de synchronisation de nuit/)).toBeInTheDocument();
    expect(figures()).toHaveTextContent('Prochaine synchronisationNon planifiée');
  });

  it('pages the log', async () => {
    const seen: string[] = [];
    serve(on, [run], 51, seen);
    const { user, router } = await renderApp('/admin/banking', { capabilities: adminCapabilities });

    const pages = await screen.findByRole('navigation', { name: 'Pages' });
    expect(pages).toHaveTextContent('Page 1 sur 2');
    await user.click(within(pages).getByRole('button', { name: 'Suivant' }));
    await waitFor(() => expect(router.state.location.search).toEqual({ page: 2 }));
    await waitFor(() => expect(seen.at(-1)).toContain('page=2'));
  });

  it('turns bank sync on from the switch', async () => {
    const sent = serve({ bankingEnabled: false });
    const { user } = await renderApp('/admin/banking', { capabilities: adminCapabilities });

    const toggle = await screen.findByRole('switch', { name: 'Synchronisation bancaire' });
    expect(toggle).not.toBeChecked();
    await user.click(toggle);

    await waitFor(() => expect(sent).toEqual([{ bankingEnabled: true }]));
    await waitFor(() => expect(toasts()).toHaveTextContent('Enregistré'));
  });

  it('keeps the operations for a chosen number of months, or without limit', async () => {
    const sent = serve({ bankingEnabled: true, bankRetentionMonths: 36 });
    const { user } = await renderApp('/admin/banking', { capabilities: adminCapabilities });

    expect(await screen.findByRole('combobox', { name: 'Conserver les opérations' })).toHaveTextContent('36 mois');
    await pick(user, 'Conserver les opérations', '24 mois');
    await waitFor(() => expect(sent).toEqual([{ bankRetentionMonths: 24 }]));
    await pick(user, 'Conserver les opérations', 'Sans limite');
    await waitFor(() => expect(sent).toEqual([{ bankRetentionMonths: 24 }, { clearBankRetention: true }]));
  });

  it('shows Nid’s estimate of the unattended reads and the bank’s latest refusal, as counts (#365)', async () => {
    const limitedAt = '2026-10-29T13:02:00+00:00';
    serve(on, [run], 1, [], { ...ready, reads: { unattended: 3, attended: 1, limit: 4, freesAt: null, limitedAt, limitedAttended: false, warn: true } });
    await renderApp('/admin/banking', { capabilities: adminCapabilities });

    await screen.findByRole('row', { name: /Réussie/ });
    expect(figures()).toHaveTextContent(`Lectures sans la personne (24 h)3 / 4estimation de Nid · dernière limite de la banque : ${formatInstant(limitedAt)}`);
    expect(screen.getByRole('columnheader', { name: 'Limites de la banque' })).toBeInTheDocument();
  });

  it('lists the adults who declared their own application, or says none', async () => {
    serve(on, [], 0, [], { ...ready, ownApplications: [{ personId: 'a', name: 'Alice' }, { personId: 'b', name: 'Bob' }] });
    await renderApp('/admin/banking', { capabilities: adminCapabilities });

    const own = await screen.findByRole('region', { name: 'Adultes avec leur propre application' });
    expect(own).toHaveTextContent('Alice');
    expect(own).toHaveTextContent('Bob');
  });

  it('says none when no adult declared an application', async () => {
    serve(on, [], 0, [], { ...ready, ownApplications: [] });
    await renderApp('/admin/banking', { capabilities: adminCapabilities });

    expect(await screen.findByRole('region', { name: 'Adultes avec leur propre application' })).toHaveTextContent('Aucun');
  });
});
