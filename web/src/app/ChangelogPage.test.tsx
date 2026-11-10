import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { groupByMinor, type Version } from './ChangelogPage';

const v = (version: string, date: string, fr: string, en?: string): Version => ({ version, date, changes: [{ fr, ...(en ? { en } : {}) }] });
const LIST = [
  v('0.5.1', '2026-11-03', 'Correctif de la 0.5.'),
  v('0.5.0', '2026-11-01', 'Une page des nouveautés.'),
  v('0.4.0', '2026-10-20', 'Les squelettes.'),
  v('0.3.2', '2026-10-10', 'Deuxième correctif.'),
  v('0.3.1', '2026-10-05', 'Premier correctif.'),
  v('0.2.0', '2026-09-01', 'Ancienne version.'),
  v('0.1.0', '2026-08-01', 'Toute première version.'),
];
const serve = (list: Version[]) => server.use(
  http.get('*/changelog.json', () => HttpResponse.json(list)),
  http.get('*/api/v1/platform/version', () => HttpResponse.json({ version: '1.2.3' })),
);

afterEach(() => vi.unstubAllEnvs());

describe('groupByMinor', () => {
  it('heads each minor release with its x.y.0, else with its oldest version', () => {
    expect(groupByMinor(LIST).map((g) => [g.minor, g.headline.version, g.patches.map((p) => p.version)])).toEqual([
      ['0.5', '0.5.0', ['0.5.1']], ['0.4', '0.4.0', []], ['0.3', '0.3.1', ['0.3.2']], ['0.2', '0.2.0', []], ['0.1', '0.1.0', []],
    ]);
  });
});

describe('ChangelogPage', () => {
  it('shows both versions, the groups newest first, the running version marked, the older ones folded', async () => {
    vi.stubEnv('VITE_NID_VERSION', '0.5.1');
    serve(LIST);
    const { user } = await renderApp('/changelog');
    expect(await screen.findByRole('heading', { level: 2, name: /Version 0\.5\.0/ })).toBeInTheDocument();
    const figures = screen.getByText("Version de l'application").closest('dl')!;
    expect(figures).toHaveTextContent('0.5.1');
    await waitFor(() => expect(figures).toHaveTextContent('1.2.3'));
    expect(within(screen.getByRole('main')).getAllByRole('heading', { level: 2 }).map((h) => h.id).filter(Boolean)).toEqual(['0.5.0', '0.4.0', '0.3.1']);
    const patches = screen.getByRole('list', { name: 'Correctifs de la version 0.5' });
    expect(within(patches).getByRole('heading', { level: 3 })).toHaveTextContent('Version actuelle');
    expect(screen.getByText('1 novembre 2026')).toBeInTheDocument();
    expect(screen.queryByText('Ancienne version.')).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Versions précédentes (2 groupes)' }));
    expect(screen.getByText('Ancienne version.')).toBeInTheDocument();
    await expectNoAxeViolations(document.body);
  });

  it('opens the folded group of a linked version', async () => {
    serve(LIST);
    await renderApp('/changelog#0.1.0');
    expect(await screen.findByText('Toute première version.')).toBeInTheDocument();
    expect(document.getElementById('0.1.0')).toHaveTextContent('Version 0.1.0');
  });

  it('falls back to French when a change has no line in the app language', async () => {
    serve([v('0.5.0', '2026-11-01', 'Seulement en français.', 'Only in English.')]);
    await renderApp('/changelog');
    expect(await screen.findByText('Seulement en français.')).toBeInTheDocument();
  });

  it('says there is nothing to show without a changelog (a dev server)', async () => {
    server.use(http.get('*/changelog.json', () => new HttpResponse('<!doctype html>', { headers: { 'Content-Type': 'text/html' } })));
    await renderApp('/changelog');
    expect(await screen.findByText('Aucune nouveauté à afficher.')).toBeInTheDocument();
  });
});
