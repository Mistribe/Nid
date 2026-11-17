import { screen, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import type { Rental2044, TaxEstimate } from '@/api/tax';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { ADULT, estimate } from '../taxTestData';

const filled = (): TaxEstimate => ({
  ...estimate(), year: 2025,
  declaration: { boxes: [{ code: '1AJ', person: ADULT, amount: '42000.00' }], toEnter: [] },
});
const rental = (): TaxEstimate => ({ ...filled(), rentalProperties: [{ id: 'a', name: 'Studio', acquiredIn: 2020 }] });
const view2044 = (): Rental2044 => ({
  year: 2025, regime: 'real', provisional: false,
  properties: [{ id: 'a', name: 'Studio', lines: [{ line: '211', amount: '6000.00' }] }],
  totals: {
    receipts: '6000.00', charges: '0.00', interest: '0.00', result: '6000.00', interestDeficit: '0.00', chargesDeficit: '0.00',
    globalDeficit: '0.00', carriedDeficit: '0.00', box4BA: '6000.00', box4BB: '0.00', box4BC: '0.00', box4BE: '0.00',
  },
  toCheck: [], notices: [],
});
const serve = (loaded: TaxEstimate, asked: string[] = []) => server.use(
  http.get('*/api/v1/tax', () => HttpResponse.json(loaded)),
  http.get('*/api/v1/tax/2044', () => { asked.push('2044'); return HttpResponse.json(view2044()); }),
);

describe('DeclarationsTab', () => {
  beforeAll(async () => { await import('../TaxPage'); }, 30_000);

  it('offers 2042 and 2044 to a household with a rental property, 2042 first and out of the URL', async () => {
    serve(rental());
    const { user, router } = await renderApp('/tax?tab=declarations');

    const group = await screen.findByRole('radiogroup', { name: 'Formulaire' });
    expect(within(group).getAllByRole('radio').map((r) => r.getAttribute('aria-label') ?? r.textContent)).toEqual(['2042', '2044']);
    expect(within(group).getByRole('radio', { name: '2042', checked: true })).toBeInTheDocument();
    await user.click(within(group).getByRole('radio', { name: '2044' }));
    expect(await screen.findByRole('region', { name: 'Studio' })).toBeInTheDocument();
    expect((router.state.location.search as { form?: number }).form).toBe(2044);
    expect(router.state.location.href).toContain('form=2044');
    expect(router.state.location.href).not.toContain('%22');
    await user.click(within(group).getByRole('radio', { name: '2042' }));
    expect((router.state.location.search as { form?: number }).form).toBeUndefined();
  });

  it('offers only the 2042 without rental property, even when the URL asks for the 2044', async () => {
    const asked: string[] = [];
    serve(filled(), asked);
    await renderApp('/tax?tab=declarations&form=2044');

    expect(await screen.findByText('Que recopier sur impots.gouv.fr ?')).toBeInTheDocument();
    expect(screen.queryByRole('radiogroup', { name: 'Formulaire' })).not.toBeInTheDocument();
    expect(screen.getByText('1AJ')).toBeInTheDocument();
    expect(asked).toEqual([]);
  });

  it('opens on its question', async () => {
    serve(rental());
    await renderApp('/tax?tab=declarations');

    const question = await screen.findByText('Que recopier sur impots.gouv.fr ?');
    const group = screen.getByRole('radiogroup', { name: 'Formulaire' });
    expect(question.compareDocumentPosition(group) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it('prints the selected form only, with its title', async () => {
    serve(rental());
    await renderApp('/tax?tab=declarations');

    const title = await screen.findByRole('heading', { level: 2, name: 'Déclaration 2042 — revenus 2025', hidden: true });
    expect(title).toHaveClass('print:block');
    const hidden = (el: HTMLElement) => el.closest('.print\\:hidden');
    expect(hidden(screen.getByRole('heading', { level: 1, name: 'Impôts' }))).not.toBeNull();
    expect(hidden(screen.getByRole('radiogroup', { name: 'Formulaire' }))).not.toBeNull();
    expect(hidden(screen.getByText('Que recopier sur impots.gouv.fr ?'))).not.toBeNull();
  });
});
