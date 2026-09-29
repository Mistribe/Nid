import { screen, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { EnvelopeList } from './api';
import { eur, figures, list, summary, unknownFigures } from './testData';

function serve(body: EnvelopeList, seen: string[] = []) {
  server.use(http.get('*/api/v1/investments/envelopes', ({ request }) => {
    seen.push(new URL(request.url).search);
    return HttpResponse.json(body);
  }));
}

async function rows(): Promise<HTMLElement[]> {
  const ul = await screen.findByRole('list', { name: 'Liste des enveloppes' });
  return within(ul).getAllByRole('listitem');
}

describe('EnvelopesPage', () => {
  it('shows each envelope with its value, date and gain, before tax', async () => {
    serve(list([summary()]));
    const { container } = await renderApp('/investments');

    const row = (await rows())[0]!;
    expect(within(row).getByRole('link', { name: 'PEA' })).toHaveAttribute('href', expect.stringContaining('/investments/'));
    expect(row).toHaveTextContent('PEA');
    expect(row).toHaveTextContent('25 000,00 €');
    expect(row).toHaveTextContent('30 septembre 2025');
    expect(row).toHaveTextContent('+3 000,00 €');
    expect(row).toHaveTextContent('+13,6 %');
    expect(within(row).getByText('en hausse')).toBeInTheDocument();
    expect(screen.getByText('Gains avant fiscalité.')).toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('says a loss in words, not only in colour', async () => {
    serve(list([summary({ figures: figures({ gain: eur('-500.0000'), gainPct: -2.3 }) })]));
    await renderApp('/investments');

    const row = (await rows())[0]!;
    expect(row).toHaveTextContent('-500,00 €');
    expect(within(row).getByText('en baisse')).toBeInTheDocument();
  });

  it('never shows an unknown value as zero, and says it only once (not again for the gain)', async () => {
    serve(list([summary({ figures: unknownFigures() })]));
    await renderApp('/investments');

    const row = (await rows())[0]!;
    expect(within(row).getAllByText('Aucune valeur pour l’instant')).toHaveLength(1);
    expect(row).not.toHaveTextContent('0,00 €');
  });

  it('shows an undefined percent as a dash, with the explanation for a screen reader alone', async () => {
    serve(list([summary({ figures: figures({ netContributions: eur('0.0000'), gain: eur('1000.0000'), gainPct: null, missingOpening: true }) })]));
    await renderApp('/investments');

    const row = (await rows())[0]!;
    expect(row).toHaveTextContent('—');
    expect(within(row).getByText('Pourcentage non calculable : versements nets nuls ou négatifs')).toBeInTheDocument();
  });

  it('flags a stale value with its age', async () => {
    serve(list([summary({ figures: figures({ staleDays: 62 }) })]));
    await renderApp('/investments');

    const row = (await rows())[0]!;
    expect(within(row).getByText('Valeur de plus de 62 jours')).toBeInTheDocument();
  });

  it('invites to add the first envelope', async () => {
    serve(list([]));
    const { container } = await renderApp('/investments');

    expect(await screen.findByRole('heading', { name: 'Aucune enveloppe pour l’instant' })).toBeInTheDocument();
    expect(screen.getAllByRole('link', { name: 'Ajouter une enveloppe' })[0]).toHaveAttribute('href', '/investments/new');
    await expectNoAxeViolations(container);
  });

  it('passes the Moi scope to the API', async () => {
    const seen: string[] = [];
    serve(list([]), seen);
    await renderApp('/investments?scope=me');

    await screen.findByRole('heading', { name: 'Aucune enveloppe pour l’instant' });
    expect(seen).toContain('?scope=me');
  });

  it('shows a failed load as a problem', async () => {
    server.use(http.get('*/api/v1/investments/envelopes', () => HttpResponse.json({ status: 500, code: 'ERROR' }, { status: 500 })));
    await renderApp('/investments');

    expect(await screen.findByRole('alert', {}, { timeout: 8000 })).toBeInTheDocument();
  });

  it('appears in the navigation only when the module is enabled', async () => {
    serve(list([]));
    await renderApp('/investments');
    expect(screen.getAllByRole('link', { name: 'Placements' }).length).toBeGreaterThan(0);
  });
});
