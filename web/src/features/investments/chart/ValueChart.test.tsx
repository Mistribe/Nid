import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { detail, eur, PEA } from '../testData';
import { timeTicks } from './ValueChart';

const point = (asOf: string, value: string, net: string, paidIn = false) => ({ asOf, value: eur(value), netContributions: eur(net), paidIn });

function serve(points: ReturnType<typeof point>[]) {
  server.use(
    http.get('*/api/v1/investments/envelopes/:id', () => HttpResponse.json(detail())),
    http.get('*/api/v1/investments/envelopes/:id/series', () => HttpResponse.json({ id: PEA, points })),
  );
}

describe('ValueChart', () => {
  // The lazy route chunk loaded cold inside the first test races its timeouts under load.
  beforeAll(async () => { await import('../EnvelopeDetailPage'); }, 30_000);

  it('draws the curve with a legend, and shows the same points as a table', async () => {
    serve([point('2024-12-31', '20000.0000', '20000.0000', true), point('2025-09-30', '25000.0000', '22000.0000')]);
    const { user, container } = await renderApp(`/investments/${PEA}`);

    const section = await screen.findByRole('region', { name: 'Évolution' });
    // The section exists as soon as the detail has loaded; the series arrives a request later.
    await within(section).findByRole('button', { name: 'Afficher en tableau' });
    expect(within(section).getByRole('application', { name: 'Valeur et versements nets de l’enveloppe' })).toBeInTheDocument();
    expect(within(section).getByText('Valeur')).toBeInTheDocument();
    expect(within(section).getByText('Versements nets')).toBeInTheDocument();
    await user.click(within(section).getByRole('button', { name: 'Afficher en tableau' }));
    const rows = within(within(section).getByRole('table', { name: 'Évolution de l’enveloppe' })).getAllByRole('row').slice(1);
    expect(rows.map((r) => r.textContent)).toEqual([
      expect.stringMatching(/31 décembre 2024.*20\s000,00\s€versé, pas encore valorisé.*20\s000,00\s€/),
      expect.stringMatching(/30 septembre 2025.*25\s000,00\s€.*22\s000,00\s€/),
    ]);
    await user.click(within(section).getByRole('button', { name: 'Afficher le graphique' }));
    await expectNoAxeViolations(container);
  });

  it('says the curve needs two values', async () => {
    serve([point('2025-09-30', '25000.0000', '22000.0000')]);
    const { container } = await renderApp(`/investments/${PEA}`);

    expect(await screen.findByText('La courbe apparaît à partir de deux valeurs.')).toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('draws negative and equal values, and lists them exactly', async () => {
    serve([point('2025-07-31', '-120.5000', '0'), point('2025-08-31', '-120.5000', '0')]);
    const { user, container } = await renderApp(`/investments/${PEA}`);

    const section = await screen.findByRole('region', { name: 'Évolution' });
    await user.click(await within(section).findByRole('button', { name: 'Afficher en tableau' }));
    expect(within(section).getAllByText(/-120,50\s€/)).toHaveLength(2);
    await expectNoAxeViolations(container);
  });

  it('asks for the series again after a value is recorded', async () => {
    let seriesCalls = 0;
    serve([point('2025-07-31', '100.0000', '0'), point('2025-08-31', '120.0000', '0')]);
    server.use(
      http.get('*/api/v1/investments/envelopes/:id/series', () => {
        seriesCalls += 1;
        return HttpResponse.json({ id: PEA, points: [] });
      }),
      http.post('*/api/v1/investments/envelopes/:id/valuations', () => HttpResponse.json({ id: 'new-value' }, { status: 201 })),
    );
    const { user } = await renderApp(`/investments/${PEA}`);

    await user.click(await screen.findByRole('button', { name: 'Saisir une valeur' }));
    const form = await screen.findByRole('dialog', { name: 'Nouvelle valeur' });
    await waitFor(() => expect(seriesCalls).toBe(1));
    await user.click(within(form).getByRole('button', { name: 'Enregistrer la valeur' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    await waitFor(() => expect(seriesCalls).toBe(2));
  });
});

describe('timeTicks', () => {
  const at = (iso: string) => Date.parse(iso);
  const read = (from: string, to: string) => {
    const { ticks, format } = timeTicks(at(from), at(to));
    return ticks?.map(format);
  };

  it('ticks each January over several years, every few years past eight (#381)', () => {
    expect(read('2020-10-08', '2025-09-30')).toEqual(['2021', '2022', '2023', '2024', '2025']);
    expect(read('2004-03-01', '2025-09-30')).toEqual(['2007', '2010', '2013', '2016', '2019', '2022', '2025']);
  });

  it('ticks month starts, evenly, under two years', () => {
    expect(read('2025-01-15', '2025-06-30')).toEqual(['févr. 2025', 'mars 2025', 'avr. 2025', 'mai 2025', 'juin 2025']);
    expect(read('2024-10-31', '2025-09-30')).toEqual(['nov. 2024', 'janv. 2025', 'mars 2025', 'mai 2025', 'juil. 2025', 'sept. 2025']);
  });

  it('leaves a span with no month start to the data’s own dates', () => {
    const { ticks, format } = timeTicks(at('2025-07-02'), at('2025-07-31'));
    expect(ticks).toBeUndefined();
    expect(format(at('2025-07-31'))).toBe('31 juil. 2025');
  });
});
