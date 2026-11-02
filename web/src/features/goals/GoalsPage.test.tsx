import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { GoalList } from './api';
import { list, summary } from './testData';

function serve(body: GoalList, seen: string[] = []) {
  server.use(http.get('*/api/v1/goals', ({ request }) => {
    seen.push(new URL(request.url).search);
    return HttpResponse.json(body);
  }));
}

async function rows(): Promise<HTMLElement[]> {
  const table = await screen.findByRole('table', { name: 'Liste des objectifs' });
  return within(table).getAllByRole('row').slice(1);
}

describe('GoalsPage', () => {
  beforeAll(async () => { await import('./GoalsPage'); }, 30_000);

  it('lists the goals in server order with status and progress', async () => {
    serve(list([
      summary({ id: 'b', name: 'Voyage', status: 'behind', progressPct: 40 }),
      summary({ id: 'a', name: 'Voiture', status: 'on_track', progressPct: 80 }),
    ]));
    const { container } = await renderApp('/goals');

    const items = await rows();
    expect(items).toHaveLength(2);
    expect(items[0]).toHaveTextContent('Voyage');
    expect(items[0]).toHaveTextContent('En retard');
    expect(items[0]).toHaveTextContent('40 %');
    expect(within(items[0]!).getByRole('progressbar')).toHaveAttribute('aria-valuenow', '40');
    expect(items[1]).toHaveTextContent('Voiture');
    expect(items[1]).toHaveTextContent('En bonne voie');
    expect(within(items[0]!).getByRole('link', { name: 'Voyage' })).toHaveAttribute('href', '/goals/b');
    expect(screen.getByRole('link', { name: 'Nouvel objectif' })).toHaveAttribute('href', '/goals/new');
    await expectNoAxeViolations(container);
  });

  it('shows dashes and the reason, not zero, when nothing is computed', async () => {
    serve(list([summary({ target: null, reached: null, progressPct: null, monthlyNeeded: null, status: null, flags: ['no_commitments'] })]));
    await renderApp('/goals');

    const [row] = await rows();
    expect(row).toHaveTextContent('Aucun engagement');
    expect(row).not.toHaveTextContent('0,00');
    expect(row).not.toHaveTextContent('NaN');
  });

  it('says a partial goal is partial', async () => {
    serve(list([summary({ partial: true })]));
    await renderApp('/goals');
    expect((await rows())[0]).toHaveTextContent('Partiel : une enveloppe liée ne vous est pas visible');
  });

  it('the « Clôturés » tab asks for closed goals and keeps only the closed rows', async () => {
    const seen: string[] = [];
    serve(list([summary({ id: 'a', name: 'Ouvert' }), summary({ id: 'b', name: 'Fini', closed: true })]), seen);
    const { user } = await renderApp('/goals');
    await rows();
    await user.click(screen.getByRole('tab', { name: 'Clôturés' }));

    await waitFor(() => expect(seen.some((s) => s.includes('closed=true'))).toBe(true));
    await waitFor(async () => {
      const items = await rows();
      expect(items).toHaveLength(1);
      expect(items[0]).toHaveTextContent('Fini');
    });
  });

  it('closes from the row menu after a confirmation, then refetches', async () => {
    let closed = false;
    const posts: string[] = [];
    server.use(
      http.get('*/api/v1/goals', () => HttpResponse.json(list(closed ? [] : [summary()]))),
      http.post('*/api/v1/goals/:id/close', ({ params }) => { posts.push(String(params.id)); closed = true; return HttpResponse.json({ id: params.id }); }),
    );
    const { user } = await renderApp('/goals');
    await rows();
    await user.click(screen.getAllByRole('button', { name: 'Actions pour Voyage' })[0]!);
    await user.click(await screen.findByRole('menuitem', { name: 'Clôturer' }));
    await user.click(await screen.findByRole('button', { name: 'Confirmer' }));

    await waitFor(() => expect(posts).toEqual([summary().id]));
    await screen.findByRole('heading', { name: 'Aucun objectif' });
  });

  it('offers no row actions on a goal the reader cannot write', async () => {
    serve(list([summary({ canWrite: false })]));
    await renderApp('/goals');
    await rows();
    expect(screen.queryByRole('button', { name: 'Actions pour Voyage' })).not.toBeInTheDocument();
  });

  it('says why a reopen is refused', async () => {
    serve(list([summary({ closed: true })]));
    server.use(http.post('*/api/v1/goals/:id/reopen', () => HttpResponse.json({ status: 409, code: 'goals.share_exceeded', title: 'Over.' }, { status: 409 })));
    const { user } = await renderApp('/goals?tab=closed');
    await rows();
    await user.click(screen.getAllByRole('button', { name: 'Actions pour Voyage' })[0]!);
    await user.click(await screen.findByRole('menuitem', { name: 'Rouvrir' }));
    expect((await screen.findAllByText('Ses enveloppes sont déjà affectées à plus de 100 % : baissez une part avant de rouvrir.')).length).toBeGreaterThan(0);
  });

  it('the mobile row shows the due date and the monthly amount needed', async () => {
    serve(list([summary()]));
    await renderApp('/goals');
    await rows();
    const [item] = within(screen.getByRole('list', { name: 'Liste des objectifs' })).getAllByRole('listitem');
    const [row] = await rows();
    const due = row!.querySelectorAll('td')[4]!.textContent;
    expect(item).toHaveTextContent(due);
    expect(item).toHaveTextContent('200,00 €');
  });

  it('invites to create the first goal', async () => {
    serve(list([]));
    await renderApp('/goals');
    expect(await screen.findByRole('heading', { name: 'Aucun objectif' })).toBeInTheDocument();
    expect(screen.getAllByRole('link', { name: 'Nouvel objectif' })).toHaveLength(2);
  });
});
