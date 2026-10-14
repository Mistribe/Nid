import { render, screen } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it } from 'vitest';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { EnvelopeChart } from './SplitCharts';
import { dashboard, envelope, person } from './testData';

// Lazy chunks load cold on the first render; warm them so no findBy races its 1 s default.
beforeAll(async () => { await import('./NetWorthCurve'); await import('./SplitCharts'); }, 30_000);

describe('split charts', () => {
  it('loads both charts on demand on the dashboard', async () => {
    server.use(http.get('*/api/v1/dashboard', () => HttpResponse.json(dashboard({ envelopeSplit: [envelope()], people: [person()] }))));
    await renderApp('/dashboard');
    expect(await screen.findByRole('figure', { name: 'Graphique de la valeur de chaque enveloppe' })).toBeInTheDocument();
    expect(await screen.findByRole('figure', { name: 'Graphique de la part de chaque personne' })).toBeInTheDocument();
  });

  it('sizes the envelope chart by its rows', () => {
    const rows = Array.from({ length: 12 }, (_, i) => envelope({ id: `00000000-0000-7000-8000-0000000001${String(i).padStart(2, '0')}`, name: `E${i}` }));
    render(<EnvelopeChart rows={rows} />);
    const figure = screen.getByRole('figure', { name: 'Graphique de la valeur de chaque enveloppe' });
    expect(figure.querySelector('[data-chart]')).toHaveStyle({ height: `${12 * 32 + 16}px` });
  });

  it('draws nothing when no envelope has a converted value', () => {
    render(<EnvelopeChart rows={[envelope({ converted: null, value: null })]} />);
    expect(screen.queryByRole('figure')).not.toBeInTheDocument();
  });
});
