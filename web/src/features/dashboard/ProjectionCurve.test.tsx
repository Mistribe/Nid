import { readFileSync } from 'node:fs';
import { screen, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { Projection } from '@/api/dashboard';
import { dashboard, emptyProjection, projection } from './testData';

const ALICE = '00000000-0000-7000-8000-0000000000a1';
const BOB = '00000000-0000-7000-8000-0000000000a2';
const LEA = '00000000-0000-7000-8000-0000000000d1';
// Every person has a birth date; the server lists Bob's retirement only (Alice retired, Léa a dependent).
const persons = [
  { id: ALICE, kind: 'member', displayName: 'Alice', birthDate: '1955-01-01', retirementAge: 64, retirementMonths: 0 },
  { id: BOB, kind: 'member', displayName: 'Bob', birthDate: '1984-05-20', retirementAge: 62, retirementMonths: 0 },
  { id: LEA, kind: 'dependent', displayName: 'Léa', birthDate: '2015-06-01', retirementAge: 64, retirementMonths: 0 },
];
const retirementHorizons = [{ person: BOB, name: 'Bob', on: '2047-03-31' }];

function serve(body: Projection, seen: string[] = []) {
  server.use(
    http.get('*/api/v1/dashboard', () => HttpResponse.json(dashboard())),
    http.get('*/api/v1/core/persons', () => HttpResponse.json(persons)),
    http.get('*/api/v1/projection', ({ request }) => { seen.push(new URL(request.url).search); return HttpResponse.json(body); }),
  );
}

const card = () => screen.findByRole('region', { name: 'Projection du patrimoine' });

// Lazy chunks load cold on the first render; warm them so no findBy races its 1 s default.
beforeAll(async () => { await import('./ProjectionCurve'); }, 30_000);

describe('net worth projection card', () => {
  it('requests ten years by default, in the scope', async () => {
    const seen: string[] = [];
    serve(projection(), seen);
    await renderApp('/dashboard?scope=me');

    const region = await card();
    expect(await within(region).findByRole('radio', { name: '10 ans' })).toBeChecked();
    expect(seen[0]).toContain('scope=me');
    expect(seen[0]).not.toContain('horizon=');
  });

  it('links to the retirement page', async () => {
    serve(projection());
    await renderApp('/dashboard');
    expect(await within(await card()).findByRole('link', { name: 'Retraite' })).toHaveAttribute('href', '/retirement');
  });

  it('keeps the horizon in the URL', async () => {
    const seen: string[] = [];
    serve(projection(), seen);
    const { router, user } = await renderApp('/dashboard');

    await user.click(await within(await card()).findByRole('radio', { name: '20 ans' }));
    expect(router.state.location.search).toEqual({ projection: '20y' });
    await expect.poll(() => seen.some((s) => s.includes('horizon=20y'))).toBe(true);
  });

  it('offers only the retirements the server lists', async () => {
    const seen: string[] = [];
    serve(projection({ retirementHorizons }), seen);
    const { router, user } = await renderApp('/dashboard');

    const region = await card();
    expect(await within(region).findByRole('radio', { name: 'Retraite de Bob' })).toBeInTheDocument();
    expect(within(region).queryByRole('radio', { name: 'Retraite de Alice' })).not.toBeInTheDocument();
    expect(within(region).queryByRole('radio', { name: 'Retraite de Léa' })).not.toBeInTheDocument();
    await user.click(within(region).getByRole('radio', { name: 'Retraite de Bob' }));
    expect(router.state.location.search).toEqual({ projection: `retire:${BOB}` });
    await expect.poll(() => seen.some((s) => s.includes(`horizon=retire%3A${BOB}`))).toBe(true);
    expect(within(region).getByRole('radio', { name: 'Retraite de Bob' })).toBeChecked();
  });

  it('switches to the current value in the URL and shows the nominal figures in every column', async () => {
    serve(projection());
    const { router, user } = await renderApp('/dashboard?real=0');

    const region = await card();
    await user.click(await within(region).findByRole('button', { name: 'Afficher en tableau' }));
    const nominal = within(within(region).getByRole('table')).getAllByRole('row')[1];
    for (const figure of ['11 000,00 €', '4 500,00 €', '8 300,00 €', '-1 800,00 €']) expect(nominal).toHaveTextContent(figure);
    expect(nominal).not.toHaveTextContent('4 400,00 €');
    expect(within(region).getByRole('radio', { name: 'Valeur courante' })).toBeChecked();
    await user.click(within(region).getByRole('radio', { name: 'Valeur d’aujourd’hui' }));
    expect(router.state.location.search).toEqual({});
    const real = within(within(region).getByRole('table')).getAllByRole('row')[1];
    for (const figure of ['10 800,00 €', '4 400,00 €', '8 150,00 €', '-1 750,00 €']) expect(real).toHaveTextContent(figure);
    expect(real).not.toHaveTextContent('4 500,00 €');
  });

  it('shows the year ends and the last month in the table', async () => {
    serve(projection());
    const { container, user } = await renderApp('/dashboard');

    const region = await card();
    await user.click(await within(region).findByRole('button', { name: 'Afficher en tableau' }));
    const rows = within(within(region).getByRole('table', { name: 'Projection du patrimoine en tableau' })).getAllByRole('row');
    expect(rows).toHaveLength(4);
    expect(rows[1]).toHaveTextContent('2026');
    expect(rows[1]).toHaveTextContent('10 800,00 €');
    expect(rows[1]).toHaveTextContent('4 400,00 €');
    expect(rows[1]).toHaveTextContent('8 150,00 €');
    expect(rows[1]).toHaveTextContent('-1 750,00 €');
    expect(rows[2]).toHaveTextContent('2027');
    expect(rows[3]).toHaveTextContent('mars 2028');
    expect(rows[3]).toHaveTextContent('17 000,00 €');
    await expectNoAxeViolations(container);
  });

  it('asks for the ranges on « Fourchette » and shows the bands, the probability and the assumptions', async () => { // ranges spec §6
    const seen: string[] = [];
    const plain = projection();
    const band = (low: string, median: string, high: string) => ({ low, median, high, lowReal: low, medianReal: median, highReal: high });
    const ranges = {
      runs: 1000, success: '87',
      points: plain.points.map((p, i) => ({ on: p.on, ...(i === 1 ? band('9500.00', '10600.00', '11900.00') : band(p.real, p.real, p.real)) })),
    };
    server.use(
      http.get('*/api/v1/dashboard', () => HttpResponse.json(dashboard())),
      http.get('*/api/v1/core/persons', () => HttpResponse.json(persons)),
      http.get('*/api/v1/projection', ({ request }) => {
        const search = new URL(request.url).search;
        seen.push(search);
        return HttpResponse.json(search.includes('ranges=true') ? { ...plain, ranges } : plain);
      }),
    );
    const { router, user, container } = await renderApp('/dashboard');

    const region = await card();
    await user.click(await within(region).findByRole('button', { name: 'Fourchette' }));
    expect(router.state.location.search).toEqual({ ranges: 1 });
    expect(await within(region).findByText('L’épargne disponible tient jusqu’à l’horizon dans 87 % des 1 000 tirages.')).toBeInTheDocument();
    expect(seen.at(-1)).toContain('ranges=true');
    expect(within(region).getByText(/volatilité annuelle : Actions 16 %\./)).toBeInTheDocument();
    expect(within(region).getByRole('list', { name: 'Légende' })).toHaveTextContent('10ᵉ à 90ᵉ centile');

    await user.click(within(region).getByRole('button', { name: 'Afficher en tableau' }));
    const rows = within(within(region).getByRole('table', { name: 'Projection du patrimoine en tableau' })).getAllByRole('row');
    expect(rows[0]).toHaveTextContent('10ᵉ centile');
    expect(rows[1]).toHaveTextContent('9 500,00 €');
    expect(rows[1]).toHaveTextContent('11 900,00 €');
    await expectNoAxeViolations(container);
  });

  it('names the assumptions with a link to the settings', async () => {
    serve(projection());
    await renderApp('/dashboard');
    const region = await card();
    expect(await within(region).findByText(/actions 6,0 %, inflation 2,0 %, épargne 500,00 €\/mois/)).toBeInTheDocument();
    expect(within(region).getByRole('link', { name: 'Modifier les hypothèses' })).toHaveAttribute('href', '/settings/projection');
    expect(within(region).getByText('Chiffres indicatifs : rendements supposés, pas d’impôt futur ni de pension.')).toBeInTheDocument();
  });

  it('lists the milestones with their server label and date', async () => {
    serve(projection({ milestones: [
      { on: '2027-12-31', kind: 'loan_end', label: 'Prêt résidence', item: null },
      { on: '2035-06-30', kind: 'financial_independence', label: 'financial_independence', item: null },
    ] }));
    await renderApp('/dashboard');
    const item = await within(await card()).findByText(/Prêt résidence/);
    expect(item).toHaveTextContent('31/12/2027');
    expect(within(await card()).getByText(/Indépendance financière/)).toHaveTextContent('30/06/2035');
  });

  it('is hidden for an empty household', async () => {
    serve(emptyProjection());
    await renderApp('/dashboard');
    await screen.findByRole('button', { name: /Patrimoine net/ });
    await expect.poll(() => screen.queryByRole('region', { name: 'Projection du patrimoine' })).toBeNull();
  });

  it('shows the warnings', async () => {
    serve(projection({ warnings: [
      { code: 'income_end_extended', detail: 'Mon CDD' },
      { code: 'savings_exhausted', detail: '2027-06' },
      { code: 'fx_missing', detail: 'GBP' },
    ] }));
    await renderApp('/dashboard');
    const region = await card();
    expect(await within(region).findByText('Le revenu « Mon CDD » est supposé continuer après sa fin.')).toBeInTheDocument();
    expect(within(region).getByText('L’épargne disponible serait épuisée en juin 2027.')).toBeInTheDocument();
    expect(within(region).getByText(/GBP/)).toBeInTheDocument();
  });

  it('says why when the server refuses', async () => {
    server.use(
      http.get('*/api/v1/dashboard', () => HttpResponse.json(dashboard())),
      http.get('*/api/v1/projection', () => HttpResponse.json({ status: 422, title: 'No birth date', code: 'projection.no_birth_date' }, { status: 422 })),
    );
    await renderApp('/dashboard');
    expect(await within(await card()).findByRole('alert')).toBeInTheDocument();
  });

  it('has no axe violations', async () => {
    serve(projection({ milestones: [{ on: '2027-12-31', kind: 'loan_end', label: 'Prêt résidence', item: null }] }));
    const { container } = await renderApp('/dashboard');
    await within(await card()).findByText(/Prêt résidence/);
    await expectNoAxeViolations(container);
  });

  it('is loaded lazily: the home page does not import the card statically', () => {
    const page = readFileSync('src/features/dashboard/DashboardPage.tsx', 'utf8');
    expect(page).toMatch(/lazy\(\(\) => import\('\.\/ProjectionCurve'\)/);
    expect(page).not.toMatch(/^import .* from '\.\/ProjectionCurve'/m);
  });
});
