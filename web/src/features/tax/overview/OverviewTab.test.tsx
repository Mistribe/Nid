import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { TaxEstimate } from '@/api/tax';
import { estimate, history, previous, result } from '../taxTestData';

const follows = (a: Element, b: Element) => expect(a.compareDocumentPosition(b) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
const PER = { pooled: false, remaining: '2400.00', taxSaved: '0.00', roomInBracket: '0.00', remainingTaxSaved: '720.00' };
const serve = (over: Partial<TaxEstimate> = {}) => {
  server.use(http.get('*/api/v1/tax', () => HttpResponse.json({ ...estimate(), previous: previous(), history: history(), ...over })));
};
const savings = (n: number): TaxEstimate['notices'] => Array.from({ length: n }, (_, i) => ({ code: 'savings_interest', item: `s${i + 1}`, name: i === 0 ? 'Compte à terme' : i === 1 ? 'Livret B' : `Compte ${i + 1}`, amount: '300.00' }));

describe('OverviewTab', () => {
  beforeAll(async () => { await import('../TaxPage'); }, 30_000);

  it('opens on the overview: the question, the hero, where the income goes, À savoir and Pistes, in that order', async () => {
    serve({ result: result({ per: PER }), notices: savings(1) });
    const { container } = await renderApp('/tax');

    expect(await screen.findByRole('tab', { name: 'Vue d’ensemble', selected: true })).toBeInTheDocument();
    const question = await screen.findByText('Combien d’impôt cette année, et que puis-je faire ?');
    const hero = screen.getByRole('region', { name: /^Impôt total/ });
    const split = await screen.findByRole('region', { name: 'Où vont vos revenus' });
    const notes = screen.getByRole('region', { name: 'À savoir' });
    const pistes = screen.getByRole('region', { name: 'Pistes' });
    follows(question, hero);
    follows(hero, split);
    follows(split, notes);
    follows(notes, pistes);
    await expectNoAxeViolations(container);
  });

  it('shows the hero’s figure and the history behind it', async () => {
    serve();
    await renderApp('/tax');

    const hero = await screen.findByRole('region', { name: 'Impôt total 2026 · estimation' });
    expect(hero).toHaveTextContent('247,00 €');
    expect(hero).toHaveTextContent('Estimation selon le barème 2025');
  });

  it('À savoir says a kind’s advice once and links each item', async () => {
    serve({ notices: savings(2) });
    await renderApp('/tax');

    const notes = await screen.findByRole('region', { name: 'À savoir' });
    expect(within(notes).getAllByText(/Comptes d’épargne non réglementés : intérêts imposables cette année/)).toHaveLength(1);
    expect(within(notes).getByRole('link', { name: /Compte à terme/ })).toHaveAttribute('href', '/investments/s1');
    expect(within(notes).getByRole('link', { name: /Livret B/ })).toHaveAttribute('href', '/investments/s2');
    expect(within(notes).getByRole('heading', { name: 'À savoir' }).parentElement!.querySelector('svg')).not.toBeNull();
  });

  it('À savoir links a PER without contributions to its envelope', async () => {
    serve({ notices: [{ code: 'per_without_contributions', item: 'e1', name: 'PER Avenir', amount: '0.00' }] });
    await renderApp('/tax');

    const notes = await screen.findByRole('region', { name: 'À savoir' });
    expect(within(notes).getByRole('link', { name: /PER Avenir/ })).toHaveAttribute('href', '/investments/e1');
    expect(notes).toHaveTextContent('Aucun versement PER enregistré cette année.');
  });

  it('À savoir tells a missing gross salary', async () => {
    serve({ notices: [{ code: 'missing_gross', item: 'i1', name: 'Prime', amount: '1000.00' }] });
    await renderApp('/tax');

    const notes = await screen.findByRole('region', { name: 'À savoir' });
    expect(within(notes).getByRole('link', { name: /Prime/ })).toHaveAttribute('href', '/income/i1');
    expect(notes).toHaveTextContent('Pas de brut renseigné : le net versé sert d’estimation.');
  });

  it('À savoir tells the sales it could not count', async () => {
    serve({ notices: [{ code: 'realised_gain_unknown', item: 'c', name: 'CTO', amount: '1000.00' }] });
    await renderApp('/tax');

    const notes = await screen.findByRole('region', { name: 'À savoir' });
    expect(within(notes).getByRole('link', { name: /CTO/ })).toHaveAttribute('href', '/investments/c');
    expect(notes).toHaveTextContent('Ventes sans nombre de parts ou sans prix de revient : leur plus-value n’est pas comptée.');
  });

  it('À savoir folds past five rows', async () => {
    serve({ notices: savings(7) });
    const { user } = await renderApp('/tax');

    const notes = await screen.findByRole('region', { name: 'À savoir' });
    expect(within(notes).getAllByRole('link')).toHaveLength(5);
    await user.click(within(notes).getByRole('button', { name: 'Afficher les 2 autres' }));
    expect(within(notes).getAllByRole('link')).toHaveLength(7);
  });

  it('the childcare summary link keeps its year', async () => {
    serve({ notices: [{ code: 'childcare_from_statements', item: 'c1', name: 'Garde de Léa', amount: '1728.81' }] });
    await renderApp('/tax');

    const notes = await screen.findByRole('region', { name: 'À savoir' });
    expect(within(notes).getByRole('link', { name: 'Récapitulatif 2026 à recopier' })).toHaveAttribute('href', expect.stringMatching(/^\/childcare\/c1\?tab=summary&year=.*2026/));
    expect(notes).toHaveTextContent(/Garde de Léa/);
    expect(notes).toHaveTextContent(/1.728,81.€/);
  });

  it('a piste reads its saving in words and opens its tab', async () => {
    serve({ result: result({ per: PER }) });
    const { user } = await renderApp('/tax');

    const pistes = await screen.findByRole('region', { name: 'Pistes' });
    const row = within(pistes).getByRole('link', { name: /Verser 2[\s..]400.* sur le PER avant le 31 décembre.*720.* d’impôt en moins/ });
    await user.click(row);
    expect(await screen.findByRole('tab', { name: 'Calcul', selected: true })).toBeInTheDocument();
  });

  it('a piste keeps the scope and the year', async () => {
    serve({ result: result({ per: PER }) });
    const { user, router } = await renderApp('/tax?scope=me&year=2025');

    const pistes = await screen.findByRole('region', { name: 'Pistes' });
    const link = within(pistes).getByRole('link', { name: /sur le PER/ });
    expect(link.getAttribute('href')).toMatch(/year=.*2025/);
    await user.click(link);
    await waitFor(() => expect(router.state.location.search).toMatchObject({ scope: 'me', tab: 'detail', year: '2025' }));
  });

  it('no Pistes card with nothing to suggest', async () => {
    serve();
    await renderApp('/tax');

    await screen.findByRole('region', { name: /^Impôt total/ });
    expect(screen.queryByRole('region', { name: 'Pistes' })).not.toBeInTheDocument();
  });

  it('an estimate without income shows the empty state', async () => {
    serve({ result: result({ netBeforeTax: '0.00' }) });
    await renderApp('/tax');

    expect(await screen.findByRole('heading', { name: 'Aucun revenu pour 2026' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Ajouter un revenu' })).toHaveAttribute('href', '/income/new');
    expect(screen.queryByRole('region', { name: /^Impôt total/ })).not.toBeInTheDocument();
  });
});
