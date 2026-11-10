import { screen, waitFor, within } from '@testing-library/react';
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

  it('opens the current arrangement at once', async () => {
    const seen: string[] = [];
    serve(list([summary({ id: 'a', name: 'Ancienne garde', closed: true, end: '2025-06-30' }), summary()]), seen);
    const { router } = await renderApp('/childcare');
    await waitFor(() => expect(router.state.location.pathname).toBe(`/childcare/${summary().id}`));
    expect(seen[0]).toContain('closed=true');
  });

  it('without a current one, lists the closed arrangements', async () => {
    serve(list([summary({ closed: true, end: '2025-06-30', toPrepare: ['2025-06'] })]));
    const { container } = await renderApp('/childcare');
    const [row] = await rows();
    expect(row).toHaveTextContent('Léa');
    expect(row).toHaveTextContent('Martine');
    expect(row).toHaveTextContent('626,37');
    expect(row).toHaveTextContent('1 mois');
    expect(within(row!).getByRole('link', { name: 'Garde de Léa' })).toHaveAttribute('href', `/childcare/${summary().id}`);
    expect(screen.getByRole('link', { name: 'Nouvelle garde' })).toHaveAttribute('href', '/childcare/new');
    await expectNoAxeViolations(container);
  });

  it('empty state links to creation', async () => {
    serve(list([]));
    const { container } = await renderApp('/childcare');
    expect(await screen.findByText('Aucune garde')).toBeInTheDocument();
    expect(screen.getAllByRole('link', { name: 'Nouvelle garde' }).length).toBeGreaterThan(0);
    expectOnePrimary(container);
  });
});
