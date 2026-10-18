import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { dashboard } from '@/features/dashboard/testData';
import { LOAN, loanDetail, schedule } from '@/features/loans/testData';
import { line, routine } from '@/features/routine/testData';
import { expectNoAxeViolations } from '@/test/axe';
import { aliceSession } from '@/test/fixtures';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { guideFor } from './guideData';

beforeAll(async () => {
  await import('./GuideIntro');
  await import('./SpotTour');
  await import('@/features/loans/LoanDetailPage');
  await import('@/features/loans/bank/BankScheduleSection');
  await import('@/features/loans/chart/ScheduleChart');
  await import('@/features/routine/RoutinePage');
}, 30_000);

afterEach(() => { vi.restoreAllMocks(); });

const fresh = () => ({ session: { ...aliceSession, preferences: { ...aliceSession.preferences, guidesSeen: {} } } });

function serveLoan() {
  server.use(
    http.get('*/api/v1/realestate/loans/:id', () => HttpResponse.json(loanDetail())),
    http.get('*/api/v1/realestate/loans/:id/schedule', () => HttpResponse.json(schedule())),
  );
}

/** Opens `path` with no guide seen, waits for `ready`, then goes to A's last screen. */
async function lastScreen(path: string, section: string, ready: () => Promise<unknown>) {
  const app = await renderApp(path, fresh());
  const intro = await screen.findByRole('dialog', { name: /premiers pas/ });
  await ready();
  for (let i = 1; i < guideFor(section)!.screens; i++) await app.user.click(within(intro).getByRole('button', { name: 'Suivant' }));
  return { ...app, intro };
}

async function startTour(path: string, section: string, ready: () => Promise<unknown>) {
  const app = await lastScreen(path, section, ready);
  await app.user.click(within(app.intro).getByRole('button', { name: 'Me montrer à l’écran' }));
  await waitFor(() => expect(screen.queryByRole('dialog', { name: /premiers pas/ })).not.toBeInTheDocument());
  return app;
}

const loanReady = () => screen.findByRole('region', { name: 'Tableau de la banque' });
const active = () => document.querySelectorAll('[data-guide-active]');
const target = (name: string) => document.querySelector(`[data-guide="${name}"]`);

/** Walks the tour to its end, returning each step's title and its active target. */
async function walk(user: Awaited<ReturnType<typeof renderApp>>['user']) {
  const steps: { title: string; target: string | null }[] = [];
  for (;;) {
    const card = await screen.findByRole('dialog');
    await waitFor(() => expect(active()).toHaveLength(1));
    steps.push({ title: within(card).getByRole('heading').textContent ?? '', target: active()[0]!.getAttribute('data-guide') });
    const next = within(card).queryByRole('button', { name: 'Suivant' });
    if (!next) break;
    await user.click(next);
  }
  return steps;
}

describe('the on-screen tour', () => {
  it('offers the tour on a loan’s page and walks its sections', async () => {
    serveLoan();
    const { user } = await startTour(`/loans/${LOAN}`, '/loans', loanReady);

    const card = await screen.findByRole('dialog', { name: 'Aujourd’hui' });
    expect(within(card).getByText('1 sur 4')).toBeInTheDocument();
    expect(card).toHaveAccessibleDescription(/^1 sur 4 Capital restant dû/);
    expect(card).not.toHaveAttribute('aria-modal');
    await waitFor(() => expect(target('loans.figures')).toHaveAttribute('data-guide-active'));
    await waitFor(() => expect(card).toHaveFocus());

    await user.click(within(card).getByRole('button', { name: 'Suivant' }));
    expect(await screen.findByRole('dialog', { name: 'Simuler un remboursement anticipé' })).toHaveTextContent('2 sur 4');
    await waitFor(() => expect(target('loans.simulation')).toHaveAttribute('data-guide-active'));
    expect(target('loans.figures')).not.toHaveAttribute('data-guide-active');

    await user.click(within(card).getByRole('button', { name: 'Suivant' }));
    await user.click(within(card).getByRole('button', { name: 'Suivant' }));
    expect(within(card).getByText('4 sur 4')).toBeInTheDocument();
    const scroll = vi.spyOn(Element.prototype, 'scrollIntoView');
    await user.click(within(card).getByRole('button', { name: 'Terminer' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(active()).toHaveLength(0);
    const help = screen.getByRole('button', { name: 'Guide de cette page' });
    await waitFor(() => expect(help).toHaveFocus());
    // Back to « ? » by a scroll rather than focus()'s jump.
    expect(scroll.mock.contexts.at(-1)).toBe(help);
    expect(scroll).toHaveBeenLastCalledWith({ block: 'nearest', behavior: 'smooth' });
    scroll.mockRestore();
  });

  it('hides « Me montrer à l’écran » and gives the hint on an empty page', async () => {
    const { intro } = await lastScreen('/loans', '/loans', () => screen.findByRole('heading', { name: 'Crédits', level: 1 }));
    expect(within(intro).queryByRole('button', { name: 'Me montrer à l’écran' })).not.toBeInTheDocument();
    expect(intro).toHaveTextContent('Ouvrez un crédit puis touchez ? pour voir la visite de sa fiche.');
  });

  it('skips a target that is not in the page', async () => {
    server.use(http.get('*/api/v1/dashboard', () => HttpResponse.json(dashboard({ envelopes: 0 }))));
    const { user } = await startTour('/dashboard', '/dashboard', () => screen.findByRole('region', { name: 'Patrimoine' }));

    const steps = await walk(user);
    expect(steps.map((s) => s.target)).not.toContain('dashboard.curve');
    const present = guideFor('/dashboard')!.tour.filter((t) => target(`dashboard.${t}`));
    expect(steps.map((s) => s.target)).toEqual(present.map((t) => `dashboard.${t}`));
    expect(screen.getByRole('dialog')).toHaveTextContent(`${present.length} sur ${present.length}`);
  });

  it('scrolls without animation when motion is reduced', async () => {
    const scroll = vi.spyOn(Element.prototype, 'scrollIntoView');
    const matchMedia = window.matchMedia.bind(window);
    const reduce = vi.spyOn(window, 'matchMedia').mockImplementation((query) => ({ ...matchMedia(query), matches: query === '(prefers-reduced-motion: reduce)' }));
    serveLoan();
    const reduced = await startTour(`/loans/${LOAN}`, '/loans', loanReady);
    await screen.findByRole('dialog', { name: 'Aujourd’hui' });
    await waitFor(() => expect(scroll).toHaveBeenCalledWith({ block: 'start', behavior: 'auto' }));
    reduced.unmount();

    reduce.mockRestore();
    scroll.mockClear();
    serveLoan();
    await startTour(`/loans/${LOAN}`, '/loans', loanReady);
    await screen.findByRole('dialog', { name: 'Aujourd’hui' });
    await waitFor(() => expect(scroll).toHaveBeenCalledWith({ block: 'start', behavior: 'smooth' }));
  });

  it('ends on Escape and on a route change', async () => {
    serveLoan();
    const escaped = await startTour(`/loans/${LOAN}`, '/loans', loanReady);
    const card = await screen.findByRole('dialog', { name: 'Aujourd’hui' });
    await waitFor(() => expect(card).toHaveFocus());
    await escaped.user.keyboard('{Escape}');
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(active()).toHaveLength(0);
    escaped.unmount();

    // Spec §4.4: Escape ends the tour wherever the focus is, here on the page after a click on the ringed section.
    serveLoan();
    const outside = await startTour(`/loans/${LOAN}`, '/loans', loanReady);
    await screen.findByRole('dialog', { name: 'Aujourd’hui' });
    await waitFor(() => expect(active()).toHaveLength(1));
    await outside.user.click(active()[0]!);
    expect(screen.getByRole('dialog')).not.toHaveFocus();
    // An Escape a dialog opened from the page already handled (ModalDialog prevents its default) leaves the tour alone.
    const handled = new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true });
    handled.preventDefault();
    active()[0]!.dispatchEvent(handled);
    expect(screen.getByRole('dialog')).toBeInTheDocument();
    await outside.user.keyboard('{Escape}');
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    outside.unmount();

    serveLoan();
    const moved = await startTour(`/loans/${LOAN}`, '/loans', loanReady);
    await screen.findByRole('dialog', { name: 'Aujourd’hui' });
    await waitFor(() => expect(active()).toHaveLength(1));
    // Same section, another page: the tour ends all the same.
    await moved.router.navigate({ to: '/loans' });
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(active()).toHaveLength(0);
  });

  it('the routine’s tour', async () => {
    server.use(http.get('*/api/v1/investments/routine', () => HttpResponse.json(routine([line()]))));
    const { user } = await startTour('/routine', '/routine', async () => {
      await screen.findByRole('button', { name: 'Enregistrer les placements' });
      await screen.findByRole('region', { name: 'Biens' });
    });

    expect((await walk(user)).map((s) => s.target)).toEqual(['routine.due', 'routine.save', 'routine.estate']);
  });

  it('is accessible', async () => {
    serveLoan();
    const { container } = await startTour(`/loans/${LOAN}`, '/loans', loanReady);
    await screen.findByRole('dialog', { name: 'Aujourd’hui' });
    await expectNoAxeViolations(container);
  });
});
