import { screen, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { ChildcareList } from './api';
import { list, summary } from './testData';
import { expectOnePrimary } from '@/test/headerActions';

function serve(body: ChildcareList, seen: string[] = []) {
  server.use(http.get('*/api/v1/childcare', ({ request }) => {
    seen.push(new URL(request.url).search);
    return HttpResponse.json(body);
  }));
}

async function rows(): Promise<HTMLElement[]> {
  const table = await screen.findByRole('table', { name: 'Liste des gardes' });
  return within(table).getAllByRole('row').slice(1);
}

describe('ChildcarePage', () => {
  beforeAll(async () => { await import('./ChildcarePage'); }, 30_000);

  it('lists the arrangements with the server monthly pay', async () => {
    serve(list([summary()]));
    const { container } = await renderApp('/childcare');
    const [row] = await rows();
    expect(row).toHaveTextContent('Léa');
    expect(row).toHaveTextContent('Martine');
    expect(row).toHaveTextContent('626,37');
    expect(within(row!).getByRole('link', { name: 'Garde de Léa' })).toHaveAttribute('href', `/childcare/${summary().id}`);
    expect(screen.getByRole('link', { name: 'Nouvelle garde' })).toHaveAttribute('href', '/childcare/new');
    await expectNoAxeViolations(container);
  });

  it('counts the months to prepare', async () => {
    serve(list([summary({ toPrepare: ['2025-10', '2025-09'] }), summary({ id: 'b', name: 'Garde à jour' })]));
    await renderApp('/childcare');
    const [due, done] = await rows();
    expect(due).toHaveTextContent('2 mois');
    expect(done).toHaveTextContent('—');
  });

  it('keeps closed ones under their tab', async () => {
    const seen: string[] = [];
    serve(list([summary({ id: 'a', name: 'Ancienne garde', closed: true, end: '2025-06-30' }), summary({ id: 'b', name: 'Garde actuelle' })]), seen);
    await renderApp('/childcare?tab=closed');
    const items = await rows();
    expect(items).toHaveLength(1);
    expect(items[0]).toHaveTextContent('Ancienne garde');
    expect(seen.at(-1)).toContain('closed=true');
  });

  it('empty state links to creation', async () => {
    serve(list([]));
    const { container } = await renderApp('/childcare');
    expect(await screen.findByText('Aucune garde')).toBeInTheDocument();
    expect(screen.getAllByRole('link', { name: 'Nouvelle garde' }).length).toBeGreaterThan(0);
    expectOnePrimary(container);
  });
});
