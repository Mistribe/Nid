import { screen, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { TaxEstimate } from '@/api/tax';
import { estimate, result } from '../taxTestData';

type Posted = { body: Record<string, unknown>; url: URL };
const serve = (loaded: TaxEstimate = estimate()) => {
  const posts: Posted[] = [];
  server.use(
    http.get('*/api/v1/tax', () => HttpResponse.json(loaded)),
    http.get('*/api/v1/tax/sale', () => HttpResponse.json({ currency: 'EUR', rulesYear: 2026, carriedForward: false, total: true, items: [], totals: { value: '0.00', incomeTax: '0.00', socialTax: '0.00', surtax: '0.00', net: '0.00' }, notes: [] })),
    http.post('*/api/v1/tax/simulate', async ({ request }) => {
      posts.push({ body: (await request.json()) as Record<string, unknown>, url: new URL(request.url) });
      return HttpResponse.json(result());
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
    await screen.findByRole('table', { name: 'Estimation et simulation' });
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
    await screen.findByRole('table', { name: 'Estimation et simulation' });
    expect(posts[0]!.url.searchParams.get('scope')).toBe('me');
  });
});
