import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it, vi } from 'vitest';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { TaxEstimate } from '@/api/tax';
import { estimate, result as baseResult } from '../taxTestData';

type Posted = { body: Record<string, unknown>; url: URL };
let answer = () => baseResult();
const serve = (loaded: TaxEstimate = estimate()) => {
  answer = () => baseResult();
  const posts: Posted[] = [];
  server.use(
    http.get('*/api/v1/tax', () => HttpResponse.json(loaded)),
    http.get('*/api/v1/tax/sale', () => HttpResponse.json({ currency: 'EUR', rulesYear: 2026, carriedForward: false, total: true, items: [], totals: { value: '0.00', incomeTax: '0.00', socialTax: '0.00', surtax: '0.00', net: '0.00' }, notes: [] })),
    http.post('*/api/v1/tax/simulate', async ({ request }) => {
      posts.push({ body: (await request.json()) as Record<string, unknown>, url: new URL(request.url) });
      return HttpResponse.json(answer());
    }),
  );
  return posts;
};
const withPer = (): TaxEstimate => {
  const loaded = estimate();
  loaded.result.declarants[0]!.per = { ceiling: '4500.00', remaining: '4500.00', source: 'notice', expiring: '0.00', contributions: '0.00', deducted: '0.00', excess: '0.00', minimum: '0.00', minimumEstimated: false };
  return loaded;
};

describe('SimulationTab', () => {
  beforeAll(async () => { await import('../TaxPage'); }, 30_000);

  it('prefills the amounts in French format and sends the wire format', async () => {
    const posts = serve();
    const { user } = await renderApp('/tax?tab=simulation');
    const gross = await screen.findByRole('textbox', { name: 'Salaire brut annuel Alice' });
    expect(gross).toHaveValue('50000');
    await user.clear(gross);
    await user.type(gross, '60 000,50');
    await user.click(screen.getByRole('button', { name: 'Calculer' }));
    await screen.findByRole('region', { name: 'Avant / après' });
    expect(posts[0]!.body).toMatchObject({ declarants: [{ salaryGross: '60000.50', status: 'non_executive' }], childcareOutsideHome: '6000' });
  });

  it('opens on its question, the activity open and the other groups folded', async () => {
    serve();
    await renderApp('/tax?tab=simulation');
    const scenarios = await screen.findByRole('group', { name: 'Scénarios' });
    expect(screen.getByText(/Et si… \?/).compareDocumentPosition(scenarios) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(screen.getByRole('group', { name: 'Revenus d’activité' })).toBeInTheDocument();
    expect(screen.queryByRole('group', { name: 'Dépenses de l’année' })).toBeNull();
    expect(screen.getByRole('button', { name: 'Dépenses de l’année' })).toHaveAttribute('aria-expanded', 'false');
  });

  it('keeps a group holding an error open and sends nothing', async () => {
    const posts = serve();
    const { user } = await renderApp('/tax?tab=simulation');
    const trigger = await screen.findByRole('button', { name: 'Dépenses de l’année' });
    await user.click(trigger);
    const gift = await screen.findByRole('textbox', { name: 'Dons' });
    await user.type(gift, 'x');
    await user.click(trigger);
    expect(screen.getByRole('group', { name: 'Dépenses de l’année' })).toBeInTheDocument();
    expect(screen.getByRole('textbox', { name: 'Dons' })).toHaveAttribute('aria-invalid', 'true');
    await user.click(screen.getByRole('button', { name: 'Calculer' }));
    expect(posts).toHaveLength(0);
  });

  it('« Un enfant de moins de 6 ans de plus » adds a dependent under 6 and folds the rest', async () => {
    serve();
    const { user } = await renderApp('/tax?tab=simulation');
    const chip = await screen.findByRole('button', { name: 'Un enfant de moins de 6 ans de plus' });
    await user.click(chip);
    expect(chip).toHaveAttribute('aria-pressed', 'true');
    expect(screen.getByRole('textbox', { name: 'Enfants à charge' })).toHaveValue('3');
    expect(screen.getByRole('textbox', { name: 'Dont moins de 6 ans' })).toHaveValue('3');
    expect(screen.queryByRole('group', { name: 'Revenus d’activité' })).toBeNull();
    await user.click(chip);
    expect(screen.getByRole('textbox', { name: 'Enfants à charge' })).toHaveValue('2');
    expect(chip).toHaveAttribute('aria-pressed', 'false');
  });

  it('« Verser sur le PER » fills the contributions up to the server’s ceiling', async () => {
    serve(withPer());
    const { user } = await renderApp('/tax?tab=simulation');
    await user.click(await screen.findByRole('button', { name: 'Verser sur le PER' }));
    expect(screen.getByRole('textbox', { name: 'Versements PER Alice' })).toHaveValue('4500');
  });

  it('offers « Passer au réel » only for rents under the micro-foncier', async () => {
    serve();
    const first = await renderApp('/tax?tab=simulation');
    await screen.findByRole('group', { name: 'Scénarios' });
    expect(screen.queryByRole('button', { name: 'Passer au réel' })).toBeNull();
    first.unmount();

    const loaded = estimate();
    loaded.inputs.rentalGross = '7200.00';
    serve(loaded);
    const { user } = await renderApp('/tax?tab=simulation');
    await user.click(await screen.findByRole('button', { name: 'Passer au réel' }));
    expect(screen.getByRole('radio', { name: 'réel', checked: true })).toBeInTheDocument();
  });

  it('explains the rental regime choice', async () => {
    serve();
    const { user } = await renderApp('/tax?tab=simulation');
    await user.click(await screen.findByRole('button', { name: 'Location' }));
    const choice = screen.getByRole('radiogroup', { name: 'Régime des revenus fonciers' });
    expect(choice).toHaveAccessibleDescription(/Micro-foncier si les loyers/);
    await user.click(screen.getByRole('radio', { name: 'réel' }));
    expect(choice).toHaveAccessibleDescription(/Loyers moins intérêts/);
  });

  it('shows one column and no pooling for one declarant', async () => {
    serve();
    await renderApp('/tax?tab=simulation');
    const table = await screen.findByRole('table', { name: 'Revenus d’activité' });
    expect(within(table).getAllByRole('columnheader')).toHaveLength(2);
    expect(screen.queryByRole('checkbox', { name: /Mutualiser/ })).toBeNull();
  });

  it('shows a column and the pooling for a second declarant', async () => {
    const loaded = estimate();
    loaded.inputs.declarants.push({ ...loaded.inputs.declarants[0]!, name: 'Bob' });
    serve(loaded);
    const { user } = await renderApp('/tax?tab=simulation');
    await user.click(await screen.findByRole('button', { name: 'Épargne retraite (PER)' }));
    expect(screen.getByRole('textbox', { name: 'Versements PER Bob' })).toBeInTheDocument();
    expect(screen.getByRole('checkbox', { name: /Mutualiser/ })).toBeInTheDocument();
  });

  it('asks the server to compare with the page’s scope', async () => {
    const posts = serve();
    const { user } = await renderApp('/tax?tab=simulation&scope=me');
    await user.click(await screen.findByRole('button', { name: 'Calculer' }));
    await screen.findByRole('region', { name: 'Avant / après' });
    expect(posts[0]!.url.searchParams.get('scope')).toBe('me');
  });

  describe('the result', () => {
    const raised = () => baseResult({
      incomeTax: '1500.00', totalTax: '1500.00', netAfterTaxMonthly: '5600.00',
      withholding: { ...baseResult().withholding, balance: '1200.00' },
      differences: { totalTax: '1253.00', netAfterTaxMonthly: '-129.42', balance: '4173.39' },
    });
    const simulate = async () => {
      const { user } = await renderApp('/tax?tab=simulation');
      await user.click(await screen.findByRole('button', { name: 'Calculer' }));
      return { user, region: await screen.findByRole('region', { name: 'Avant / après' }) };
    };
    const item = (region: HTMLElement, label: string) => within(region).getByText(label).closest('div')!;

    it('shows the before and after above the form, with the server’s differences', async () => {
      serve(); answer = raised;
      const scroll = vi.spyOn(Element.prototype, 'scrollIntoView');
      const { region } = await simulate();
      expect(region.compareDocumentPosition(screen.getByRole('form', { name: 'Simulation' })) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
      const tax = item(region, 'Impôt total');
      expect(tax).toHaveTextContent('1 500,00 €');
      expect(tax).toHaveTextContent('avant 247,00 €');
      const rise = within(tax).getByText('+1 253,00 €');
      expect(rise.previousElementSibling).toHaveClass('text-status-critical');
      const left = item(region, 'Reste par mois');
      expect(left).toHaveTextContent('-129,42 €');
      expect(within(left).getByText('-129,42 €').previousElementSibling).toHaveClass('text-status-critical');
      expect(region).toHaveTextContent('avant 2 973,39 € remboursés');
      expect(scroll).toHaveBeenCalled();
    });

    it('folds the two-column table', async () => {
      serve(); answer = raised;
      const { user, region } = await simulate();
      expect(screen.queryByRole('table', { name: 'Estimation et simulation' })).toBeNull();
      await user.click(within(region).getByRole('button', { name: 'Voir le détail du calcul' }));
      const row = within(await screen.findByRole('table', { name: 'Estimation et simulation' })).getByRole('row', { name: /^Impôt sur le revenu/ });
      expect(row).toHaveTextContent('247,00 €1 500,00 €');
    });

    it('shows the before and after without arrows when the server gives none', async () => {
      serve(); answer = () => ({ ...raised(), differences: null });
      const { region } = await simulate();
      expect(region).toHaveTextContent('avant 247,00 €');
      expect(within(region).queryByText(/^\+/)).toBeNull();
    });

    it('announces the result in an always-mounted live region', async () => {
      serve(); answer = raised;
      const { user } = await renderApp('/tax?tab=simulation');
      const status = await screen.findByRole('status');
      expect(status).toBeEmptyDOMElement();
      await user.click(screen.getByRole('button', { name: 'Calculer' }));
      await screen.findByRole('region', { name: 'Avant / après' });
      expect(screen.getByRole('status')).toBe(status);
      expect(status).toHaveTextContent('Simulation : impôt total 1 500,00 €, 5 600,00 € de reste par mois.');
    });

    it('takes the result away when the year changes', async () => {
      serve(); answer = raised;
      const { user } = await simulate();
      await user.click(screen.getByRole('radio', { name: '2025' }));
      await waitFor(() => expect(screen.queryByRole('region', { name: 'Avant / après' })).toBeNull());
    });

    it('takes the result away when the scope changes', async () => {
      serve(); answer = raised;
      const { user } = await simulate();
      await user.click(screen.getByRole('radio', { name: 'Moi' }));
      await waitFor(() => expect(screen.queryByRole('region', { name: 'Avant / après' })).toBeNull());
    });

    it('shows a failed simulation as a problem, keeps the draft and no result', async () => {
      serve();
      server.use(http.post('*/api/v1/tax/simulate', () => HttpResponse.json({ status: 500, code: 'ERROR' }, { status: 500 })));
      const { user } = await renderApp('/tax?tab=simulation');
      const gross = await screen.findByRole('textbox', { name: 'Salaire brut annuel Alice' });
      await user.clear(gross);
      await user.type(gross, '61000');
      await user.click(screen.getByRole('button', { name: 'Calculer' }));
      expect(await screen.findByRole('alert')).toBeInTheDocument();
      expect(screen.getByRole('textbox', { name: 'Salaire brut annuel Alice' })).toHaveValue('61000');
      expect(screen.queryByRole('region', { name: 'Avant / après' })).toBeNull();
    });

    it('takes the result away once a field changes', async () => {
      serve(); answer = raised;
      const { user } = await simulate();
      await user.type(screen.getByRole('textbox', { name: 'Salaire brut annuel Alice' }), '1');
      expect(screen.queryByRole('region', { name: 'Avant / après' })).toBeNull();
    });
  });

  it('explains the tax terms beside their fields', async () => {
    serve();
    const { user } = await renderApp('/tax?tab=simulation');
    expect(await screen.findByRole('button', { name: 'À propos : Statut' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'À propos : Autres revenus imposables (chômage)' })).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Épargne retraite (PER)' }));
    expect(screen.getByRole('button', { name: 'À propos : Versements PER' })).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Placements' }));
    expect(screen.getByRole('button', { name: 'À propos : Imposer les placements au barème (case 2OP)' })).toBeInTheDocument();
  });
});
