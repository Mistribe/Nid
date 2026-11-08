import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { components } from '@/api/schema';
import { expectNoAxeViolations } from '@/test/axe';
import { adminCapabilities, allCapabilities } from '@/test/fixtures';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { toasts } from '@/test/toasts';
import { formatInstant } from '@/lib/format';

type Run = components['schemas']['CatalogSyncRunView'];
type Report = components['schemas']['CatalogReport'];

const PINNED = 'df0146a8f3532f5c790aa54a34d4871e96b62a95';

const emptyReport: Report = {
  providersAdded: [], providersUpdated: [], providersUnchanged: [], providersArchived: [], adoptedReplaced: [],
  adoptedRemaining: [], conflicts: [], labelsAdded: [], labelsUpdated: [], labelsCreated: [], duplicates: [], refused: [], includesRefused: [],
  icons: { fetched: [], unchanged: 0, refused: [] },
};

const succeeded: Run = {
  id: 'run-2', startedAt: '2026-09-24T07:00:00+00:00', finishedAt: '2026-09-24T07:00:05+00:00', status: 'succeeded', commit: PINNED,
  failureCode: null, failureMessage: null,
  report: {
    ...emptyReport,
    providersAdded: ['netflix', 'spotify'], providersUnchanged: ['nordvpn'], adoptedReplaced: ['battlenet'],
    adoptedRemaining: ['wow'], labelsCreated: ['home'], duplicates: [{ file: 'labels', key: 'hosting' }],
    refused: [{ file: 'providers', index: 3, key: 'Netflix', reason: 'invalid_key' }],
    icons: {
      fetched: ['netflix'], unchanged: 14,
      refused: [{ key: 'nounou-top', reason: 'unsupported_format' }, { key: 'ovh-cloud', reason: 'http_status' }],
    },
  },
};

const failed: Run = {
  id: 'run-1', startedAt: '2026-09-23T07:00:00+00:00', finishedAt: '2026-09-23T07:00:01+00:00', status: 'failed', commit: 'aaaaaaa0000',
  failureCode: 'subscriptions.catalog_source_unavailable', failureMessage: 'raw.githubusercontent.com answered HTTP 503 for labels.json.',
  report: null,
};

/** Each GET answers the next list, the last one for ever after. */
function serveRuns(...answers: Run[][]) {
  let call = 0;
  server.use(http.get('*/api/v1/subscriptions/catalog/runs', () => {
    const runs = answers[Math.min(call++, answers.length - 1)] ?? [];
    return HttpResponse.json({ pinnedCommit: PINNED, runs });
  }));
}

const render = (path = '/admin/catalog') => renderApp(path, { capabilities: adminCapabilities });
const lastCard = () => screen.findByRole('region', { name: 'Dernière synchronisation' });
const syncButton = () => screen.getByRole('button', { name: 'Synchroniser maintenant' });

describe('Administration › Catalogue', () => {
  afterEach(() => { vi.useRealTimers(); });

  it('sends a non-admin to the forbidden page', async () => {
    await renderApp('/admin/catalog', { capabilities: allCapabilities });
    expect(await screen.findByRole('heading', { level: 1, name: 'Accès réservé' })).toBeInTheDocument();
  });

  it('is a section of the admin navigation, marked current', async () => {
    serveRuns([]);
    await render();
    const sections = screen.getByRole('navigation', { name: "Sections de l'administration" });
    expect(within(sections).getByRole('link', { name: 'Catalogue' })).toHaveAttribute('aria-current', 'page');
    expect(screen.getByRole('heading', { level: 1, name: 'Catalogue' })).toBeInTheDocument();
  });

  it('shows the last sync with its status in words, its time, its commit and its counts', async () => {
    serveRuns([succeeded, failed]);
    await render();

    const last = await lastCard();
    expect(within(last).getByText('Réussie')).toBeInTheDocument();
    expect(within(last).getByText(formatInstant(succeeded.startedAt))).toBeInTheDocument();
    expect(within(last).getByText('Version synchronisée : df0146a')).toBeInTheDocument();
    expect(within(last).getByText('Ajoutés').parentElement).toHaveTextContent('Ajoutés2');
    expect(within(last).getByText('Inchangés').parentElement).toHaveTextContent('Inchangés1');
    expect(within(last).getByText('Adoptés remplacés').parentElement).toHaveTextContent('Adoptés remplacés1');
    expect(screen.getByText('Version épinglée : df0146a')).toBeInTheDocument();
  });

  it('lists the report by category, each refused entry with its reason', async () => {
    serveRuns([succeeded]);
    const { user } = await render();

    const last = await lastCard();
    expect(within(last).queryByText('wow')).not.toBeInTheDocument(); // a category is folded until opened
    await user.click(within(last).getByRole('button', { name: 'Fournisseurs adoptés restants (1)' }));
    await user.click(within(last).getByRole('button', { name: 'Libellés créés (1)' }));
    await user.click(within(last).getByRole('button', { name: /^Clés en double/ }));
    await user.click(within(last).getByRole('button', { name: /^Entrées refusées/ }));
    expect(within(last).getByText('wow')).toBeInTheDocument();
    expect(within(last).getByText('Libellés créés (1)')).toBeInTheDocument();
    expect(within(last).getByText('labels : hosting')).toBeInTheDocument();
    expect(within(last).getByText('providers n° 3 (Netflix) : clé invalide')).toBeInTheDocument();
    expect(within(last).queryByText(/Conflits/)).not.toBeInTheDocument(); // an empty category is not shown
  });

  it('lists what was ignored in includes, each entry with its reason', async () => {
    serveRuns([{
      ...succeeded,
      report: {
        ...emptyReport,
        includesRefused: [
          { key: 'apple-one', included: 'ghost', reason: 'unknown_include' },
          { key: 'apple-one', included: null, reason: 'invalid_include' },
        ],
      },
    }]);
    const { user } = await render();

    const last = await lastCard();
    await user.click(within(last).getByRole('button', { name: 'Inclusions ignorées (2)' }));
    expect(within(last).getByText('apple-one inclut ghost : fournisseur absent du catalogue')).toBeInTheDocument();
    expect(within(last).getByText('apple-one inclut — : clé invalide')).toBeInTheDocument();
  });

  it('lists the icons fetched and refused, each with its reason', async () => {
    serveRuns([succeeded]);
    const { user } = await render();

    const last = await lastCard();
    await user.click(within(last).getByRole('button', { name: 'Icônes (récupérées : 1, inchangées : 14, refusées : 2)' }));
    expect(within(last).getByText('netflix')).toBeInTheDocument();
    expect(within(last).getByText('nounou-top : format non pris en charge (SVG compris)')).toBeInTheDocument();
    expect(within(last).getByText("ovh-cloud : réponse en erreur de l'hôte")).toBeInTheDocument();
  });

  it('shows no icon category when every icon was already there', async () => {
    serveRuns([{ ...succeeded, report: { ...emptyReport, icons: { fetched: [], unchanged: 17, refused: [] } } }]);
    await render();

    const last = await lastCard();
    expect(within(last).queryByText(/Icônes/)).not.toBeInTheDocument();
  });

  it('shows the failure code and message of a failed sync', async () => {
    serveRuns([failed]);
    await render();

    const last = await lastCard();
    expect(within(last).getByText('Échouée')).toBeInTheDocument();
    expect(within(last).getByText('subscriptions.catalog_source_unavailable')).toBeInTheDocument();
    expect(within(last).getByText('raw.githubusercontent.com answered HTTP 503 for labels.json.')).toBeInTheDocument();
  });

  it('says there is no sync yet', async () => {
    serveRuns([]);
    await render();
    expect(await screen.findByRole('heading', { name: "Aucune synchronisation pour l'instant" })).toBeInTheDocument();
  });

  it('keeps the ten latest runs in the history', async () => {
    serveRuns(Array.from({ length: 12 }, (_, i) => ({ ...failed, id: `run-${i}` })));
    await render();

    const history = await screen.findByRole('region', { name: 'Historique' });
    expect(within(history).getAllByRole('listitem')).toHaveLength(10);
  });

  it('asks for a confirmation, starts a sync, then refreshes every 3 seconds until the new run ends', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const running: Run = { ...succeeded, id: 'run-3', startedAt: '2026-09-24T08:00:00+00:00', status: 'running', finishedAt: null, report: null };
    const done: Run = { ...succeeded, id: 'run-3', startedAt: '2026-09-24T08:00:00+00:00' };
    serveRuns([failed], [failed], [running, failed], [done, failed]);
    let calls = 0;
    let csrf: string | null = null;
    server.use(http.post('*/api/v1/subscriptions/catalog/sync', ({ request }) => {
      calls += 1;
      csrf = request.headers.get('X-Nid-Csrf');
      return HttpResponse.json({ requested: true }, { status: 202 });
    }));
    const { user } = await render();
    await lastCard();

    await user.click(syncButton());
    expect(calls).toBe(0);
    await user.click(await screen.findByRole('button', { name: 'Confirmer' }));
    await waitFor(() => expect(toasts()).toHaveTextContent('Synchronisation demandée'));
    expect(csrf).toBe('csrf-alice');
    await waitFor(() => expect(syncButton()).toBeDisabled());

    await vi.advanceTimersByTimeAsync(3_000);
    expect(await within(await lastCard()).findByText('En cours')).toBeInTheDocument();
    expect(syncButton()).toBeDisabled();

    await vi.advanceTimersByTimeAsync(3_000);
    expect(await within(await lastCard()).findByText('Réussie')).toBeInTheDocument();
    await waitFor(() => expect(syncButton()).toBeEnabled());
  });

  it('stops waiting for the requested run after 30 seconds and stops refreshing', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    let gets = 0;
    server.use(http.get('*/api/v1/subscriptions/catalog/runs', () => {
      gets += 1;
      return HttpResponse.json({ pinnedCommit: PINNED, runs: [failed] }); // the job never creates its run
    }));
    server.use(http.post('*/api/v1/subscriptions/catalog/sync', () => HttpResponse.json({ requested: true }, { status: 202 })));
    const { user } = await render();
    await lastCard();

    await user.click(syncButton());
    await user.click(await screen.findByRole('button', { name: 'Confirmer' }));
    await waitFor(() => expect(syncButton()).toBeDisabled());

    await vi.advanceTimersByTimeAsync(27_000);
    expect(syncButton()).toBeDisabled();

    await vi.advanceTimersByTimeAsync(3_000);
    await waitFor(() => expect(syncButton()).toBeEnabled());
    expect(within(await lastCard()).getByText('Échouée')).toBeInTheDocument();
    const settled = gets;
    await vi.advanceTimersByTimeAsync(9_000);
    expect(gets).toBe(settled);
  });

  it('says a sync is already running when the server answers 409', async () => {
    serveRuns([succeeded]);
    server.use(http.post('*/api/v1/subscriptions/catalog/sync', () =>
      HttpResponse.json({ status: 409, code: 'subscriptions.catalog_sync_running', title: 'A catalog sync is already running.' }, { status: 409 })));
    const { user } = await render();
    await lastCard();

    await user.click(syncButton());
    await user.click(await screen.findByRole('button', { name: 'Confirmer' }));

    await waitFor(() => expect(toasts()).toHaveTextContent('Une synchronisation est déjà en cours'));
    expect(syncButton()).toBeEnabled();
  });

  it('has no accessibility violation', async () => {
    serveRuns([succeeded, failed]);
    const { container } = await render();
    await lastCard();
    await expectNoAxeViolations(container);
  });
});
