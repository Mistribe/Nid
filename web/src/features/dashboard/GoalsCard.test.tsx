import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { delay } from 'msw/utils/delay';
import { describe, expect, it } from 'vitest';
import { allCapabilities } from '@/test/fixtures';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { list, summary } from '../goals/testData';
import { dashboard } from './testData';

const names = ['Voyage', 'Toit', 'Voiture', 'Mariage', 'Piano'];

function serve(items = names.map((name, i) => summary({ id: `0192f0aa-0000-7000-8000-00000000a10${i}`, name }))) {
  server.use(
    http.get('*/api/v1/dashboard', () => HttpResponse.json(dashboard())),
    http.get('*/api/v1/goals', () => HttpResponse.json(list(items))),
  );
}

describe('GoalsCard', () => {
  it('shows at most four goals in server order, with a link to all of them', async () => {
    serve();
    await renderApp('/dashboard');
    const card = await screen.findByRole('region', { name: 'Objectifs' });
    const rows = within(card).getAllByRole('listitem');
    expect(rows.map((r) => r.querySelector('a')?.textContent)).toEqual([
      expect.stringMatching(/^Voyage/), expect.stringMatching(/^Toit/), expect.stringMatching(/^Voiture/), expect.stringMatching(/^Mariage/),
    ]);
    // The list is the grouped one, each row led by the goals module's tile and linking to its goal.
    expect(rows.map((r) => r.dataset.tile)).toEqual(['4', '4', '4', '4']);
    expect(within(rows[0] as HTMLElement).getByRole('link')).toHaveAttribute('href', '/goals/0192f0aa-0000-7000-8000-00000000a100');
    expect(within(rows[0] as HTMLElement).getByText('40 %', { exact: false })).toBeInTheDocument();
    expect(within(card).getAllByText('En retard')).toHaveLength(4);
    expect(within(card).getByRole('link', { name: 'Tous les objectifs' })).toHaveAttribute('href', '/goals');
  });

  it('is hidden without the goals module', async () => {
    serve();
    await renderApp('/dashboard', { capabilities: { ...allCapabilities, modules: allCapabilities.modules.filter((m) => m !== 'goals') } });
    await screen.findByRole('button', { name: /Patrimoine net/ });
    expect(screen.queryByRole('region', { name: 'Objectifs' })).not.toBeInTheDocument();
  });

  it('is hidden with no open goal', async () => {
    serve([]);
    await renderApp('/dashboard');
    await screen.findByRole('button', { name: /Patrimoine net/ });
    await waitFor(() => expect(screen.queryByRole('region', { name: 'Objectifs' })).not.toBeInTheDocument());
  });

  it('does not block the dashboard while it loads', async () => {
    server.use(
      http.get('*/api/v1/dashboard', () => HttpResponse.json(dashboard())),
      http.get('*/api/v1/goals', async () => { await delay('infinite'); return HttpResponse.json(list([])); }),
    );
    await renderApp('/dashboard');
    expect(await screen.findByRole('button', { name: /Patrimoine net/ })).toBeInTheDocument();
    expect(screen.queryByRole('region', { name: 'Objectifs' })).not.toBeInTheDocument();
  });
});
