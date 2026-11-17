import { cleanup, screen, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { TaxEstimate } from '@/api/tax';
import { ADULT, estimate, result } from '../taxTestData';

const nbsp = (el: Element | null) => el?.textContent?.replace(/\s/g, ' ');
const load = (loaded: TaxEstimate = estimate()) => server.use(http.get('*/api/v1/tax', () => HttpResponse.json(loaded)));
const withResult = (over: Parameters<typeof result>[0]) => { const e = estimate(); e.result = result(over); return e; };
const group = (name: string) => screen.getByRole('region', { name });

describe('DetailTab', () => {
  beforeAll(() => { vi.setSystemTime(new Date('2026-06-15T12:00:00Z')); });
  afterEach(cleanup);

  it('opens on its question naming the total, then the short disclaimer', async () => {
    load();
    const { user } = await renderApp('/tax?tab=detail');

    expect(await screen.findByText(/Comment Nid arrive à 247,00\s€ d’impôt \?/)).toBeInTheDocument();
    expect(screen.getByText('Estimation indicative, barème des revenus 2025.')).toBeInTheDocument();
    expect(screen.queryByText(/Ce n’est pas un conseil fiscal/)).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'En savoir plus' }));
    expect(screen.getByText(/Ce n’est pas un conseil fiscal/)).toBeInTheDocument();
  });

  it('cards follow the reading order', async () => {
    load();
    await renderApp('/tax?tab=detail');

    const panel = await screen.findByRole('tabpanel', { name: 'Calcul' });
    await screen.findByRole('region', { name: 'Salaires et PER' });
    expect(within(panel).getAllByRole('region').map((r) => r.getAttribute('aria-labelledby')).map((id) => document.getElementById(id!)?.textContent)).toEqual([
      'Du revenu à l’impôt', 'Votre tranche d’imposition', 'Salaires et PER', 'Revenus imposables', 'Barème, réductions et crédits', 'Autres prélèvements', 'Ce qu’il reste',
    ]);
    expect(panel.querySelector('.animate-enter')?.lastElementChild?.textContent).toMatch(/^Non calculés : /);
  });

  it('toggles the steps between chart and table', async () => {
    load();
    const { user } = await renderApp('/tax?tab=detail');

    const card = await screen.findByRole('region', { name: 'Du revenu à l’impôt' });
    expect(card.querySelectorAll('.recharts-bar')).toHaveLength(2);
    await user.click(within(card).getByRole('button', { name: 'Afficher en tableau' }));
    expect(within(card).getByRole('table', { name: 'Étapes du revenu à l’impôt' })).toBeInTheDocument();
  });

  it('folds each group table with its total in the header', async () => {
    load();
    const { user } = await renderApp('/tax?tab=detail');

    await screen.findByRole('region', { name: 'Salaires et PER' });
    expect(nbsp(group('Revenus imposables'))).toContain('64 407,00 €');
    expect(within(group('Revenus imposables')).queryByRole('table')).not.toBeInTheDocument();
    expect(nbsp(group('Barème, réductions et crédits'))).toContain('247,00 €');
    expect(nbsp(group('Ce qu’il reste'))).toContain('68 753,00 €');

    const left = group('Ce qu’il reste');
    const open = within(left).getByRole('button', { name: 'Voir le détail' });
    await user.click(open);
    expect(open).toHaveAttribute('aria-expanded', 'true');
    expect(within(within(left).getByRole('table', { name: 'Ce qu’il reste' })).getByText(/Remboursé/)).toBeInTheDocument();

    const scale = group('Barème, réductions et crédits');
    await user.click(within(scale).getByRole('button', { name: 'Voir le détail' }));
    expect(within(scale).queryByRole('row', { name: /Réduction Pinel/ })).not.toBeInTheDocument();
    await user.click(within(scale).getByRole('button', { name: /Afficher les \d+ lignes à 0 €/ }));
    expect(within(scale).getByRole('row', { name: /Réduction Pinel/ })).toHaveTextContent('0,00 €');
  });

  it('shows each declarant’s figures and the tax terms in their card', async () => {
    load();
    const { user } = await renderApp('/tax?tab=detail');

    const card = await screen.findByRole('region', { name: 'Salaires et PER' });
    const people = within(card).getByRole('table', { name: 'Revenus par déclarant' });
    expect(within(people).getByRole('columnheader', { name: 'Alice' })).toBeInTheDocument();
    expect(within(people).getByRole('row', { name: /Abattement de 10 %/ })).toHaveTextContent('4 042,46 €');
    await user.click(within(group('Revenus imposables')).getByRole('button', { name: 'Voir le détail' }));
    expect(within(group('Revenus imposables')).getByText(/se compare à l’avis d’imposition de la même année de revenus/)).toBeInTheDocument();
    await user.click(within(group('Barème, réductions et crédits')).getByRole('button', { name: 'Voir le détail' }));
    const table = within(group('Barème, réductions et crédits')).getByRole('table');
    expect(within(table).getByRole('row', { name: /Crédit garde hors du domicile/ })).toHaveTextContent('3 000,00 €');
  });

  it('shows the PER ceiling and deduction once, the progress keeps them as its value text', async () => {
    const e = estimate();
    e.result = result({ declarants: [{ ...result().declarants[0]!, person: ADULT, per: { ceiling: '5000.00', source: 'notice', expiring: '0.00', contributions: '3000.00', deducted: '3000.00', excess: '0.00', remaining: '2000.00', minimum: '0.00', minimumEstimated: false } }] });
    load(e);
    await renderApp('/tax?tab=detail');

    const card = await screen.findByRole('region', { name: 'Salaires et PER' });
    expect(within(card).getByRole('progressbar', { name: 'PER de Alice' }).getAttribute('aria-valuetext')?.replace(/\s/g, ' ')).toBe('3 000,00 € déduits sur un plafond de 5 000,00 €');
    expect(within(card).queryByText(/déduits sur un plafond/)).not.toBeInTheDocument();
    expect(within(card).getByText(/2 000,00\s€ à verser avant le 31 décembre/)).toBeInTheDocument();
  });

  it('explains a term on hover and lists the realised gains and the losses carried forward', async () => {
    const e = withResult({ realisedGainsTaxable: '200.00', capitalLossesCarriedForward: '150.00', capitalScale: true, dividendAllowance: '1200.00' });
    load(e);
    const { user } = await renderApp('/tax?tab=detail');

    await screen.findByRole('region', { name: 'Salaires et PER' });
    await user.click(within(group('Revenus imposables')).getByRole('button', { name: 'Voir le détail' }));
    const rfr = within(group('Revenus imposables')).getByRole('button', { name: 'À propos : Revenu fiscal de référence' });
    await user.hover(rfr);
    expect(await screen.findByRole('tooltip')).toHaveTextContent(/en haut de la première page de votre avis/);
    expect(screen.getAllByRole('button', { name: 'À propos : Tranche marginale' })).toHaveLength(1);
    expect(within(group('Revenus imposables')).getByRole('row', { name: /Abattement de 40 % sur les dividendes/ })).toHaveTextContent('1 200,00 €');
    expect(within(group('Revenus imposables')).getByRole('row', { name: /Plus-values de cession de titres/ })).toHaveTextContent('200,00 €');
    expect(within(group('Revenus imposables')).getByRole('row', { name: /Moins-values à reporter/ })).toHaveTextContent('150,00 €');
  });

  it('leaves the choices to Pistes', async () => {
    load(withResult({ rentalRegimeGap: '-344.00' }));
    const { user } = await renderApp('/tax?tab=detail');

    await screen.findByRole('region', { name: 'Salaires et PER' });
    expect(screen.queryByText('Micro-foncier ou réel ?')).not.toBeInTheDocument();
    expect(screen.queryByText('Prélèvement forfaitaire ou barème ?')).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Voir les pistes' }));
    expect(screen.getByRole('tab', { name: 'Vue d’ensemble', selected: true })).toBeInTheDocument();
  });

  it('no Voir les pistes without a piste', async () => {
    load();
    await renderApp('/tax?tab=detail');

    await screen.findByRole('region', { name: 'Salaires et PER' });
    expect(screen.queryByRole('button', { name: 'Voir les pistes' })).not.toBeInTheDocument();
  });

  it('nothing negative, nothing « Infos »', async () => {
    load();
    const { container } = await renderApp('/tax?tab=detail');

    const panel = await screen.findByRole('tabpanel', { name: 'Calcul' });
    await screen.findByRole('region', { name: 'Salaires et PER' });
    expect(within(panel).queryByText(/négatif/)).not.toBeInTheDocument();
    expect(within(panel).queryByRole('columnheader', { name: 'Infos' })).not.toBeInTheDocument();
    expect(panel.querySelectorAll('section')).toHaveLength(0);
    await expectNoAxeViolations(container);
  });
});
