import { screen, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it, vi } from 'vitest';
import type { Rental2044, TaxEstimate } from '@/api/tax';
import { renderApp } from '@/test/renderApp';
import { expectNoAxeViolations } from '@/test/axe';
import { server } from '@/test/server';
import { toasts } from '@/test/toasts';
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

  describe('the 2042 in cards', () => {
    const boxes = [
      { code: '1AJ', person: ADULT, amount: '42000.00' }, { code: '2DC', person: null, amount: '300.00' },
      { code: '6NS', person: ADULT, amount: '1000.00' }, { code: '7GA', person: null, amount: '2500.00' },
    ];
    const serve2042 = (extra: { code: string; person: string | null; amount: string }[] = [], asked: string[] = []) => server.use(
      http.get('*/api/v1/core/persons', () => HttpResponse.json([{ id: ADULT, kind: 'adult', displayName: 'Alice', email: null }])),
      http.get('*/api/v1/tax', ({ request }) => {
        asked.push(new URL(request.url).searchParams.get('year')!);
        return HttpResponse.json({
          ...filled(), declaration: { boxes: [...boxes, ...extra], toEnter: [{ code: 'unsupported_income', item: 'p', name: 'Pension', amount: '1200.00' }] },
        });
      }),
    );
    const filledCard = async () => within(await screen.findByRole('region', { name: 'Cases remplies par Nid' }));

    it('lists each box with its code, label, declarant and amount, grouped by section of the form', async () => {
      const asked: string[] = [];
      serve2042([], asked);
      const { container, user } = await renderApp('/tax?tab=declarations');

      const card = await filledCard();
      expect((await card.findByText('1AJ')).closest('dl')).toBe(card.getByLabelText('Salaires', { selector: 'dl' }));
      const labels = card.getAllByText(/^(Salaires|Revenus de capitaux|Charges|Réductions et crédits)$/, { selector: 'h3' }).map((h) => h.textContent);
      expect(labels).toEqual(['Salaires', 'Revenus de capitaux', 'Charges', 'Réductions et crédits']);
      const salaries = within(card.getByLabelText('Salaires', { selector: 'dl' }));
      expect(salaries.getByText('1AJ')).toBeInTheDocument();
      expect(salaries.getByText('Alice')).toBeInTheDocument();
      expect(salaries.getByText('42 000,00 €')).toBeInTheDocument();
      const capital = within(card.getByLabelText('Revenus de capitaux', { selector: 'dl' }));
      expect(capital.getByText('2DC')).toBeInTheDocument();
      expect(capital.getByText('Foyer')).toBeInTheDocument();
      await user.hover(salaries.getByRole('button', { name: 'À propos : Salaires' }));
      expect((await screen.findAllByText(/cumul « net imposable »/)).length).toBeGreaterThan(0);
      expect(asked).toContain('2025');
      await expectNoAxeViolations(container);
    });

    it('puts an unknown box in « Autres cases », last', async () => {
      serve2042([{ code: '8TK', person: null, amount: '10.00' }]);
      await renderApp('/tax?tab=declarations');

      const card = await filledCard();
      await card.findAllByText('8TK');
      const dls = card.getAllByLabelText(/./, { selector: 'dl' });
      const last = dls[dls.length - 1]!;
      expect(last).toHaveAttribute('aria-label', 'Autres cases');
      expect(within(last).getAllByText('8TK').length).toBeGreaterThan(0);
    });

    it('exports the CSV and prints from the card’s actions', async () => {
      serve2042();
      const print = vi.spyOn(window, 'print').mockImplementation(() => undefined);
      const { user } = await renderApp('/tax?tab=declarations');

      const card = await filledCard();
      expect(await card.findByRole('button', { name: 'Exporter en CSV' })).toHaveClass('print:hidden');
      await user.click(card.getByRole('button', { name: 'Imprimer' }));
      expect(print).toHaveBeenCalled();
    });

    it('copies a box’s amount and confirms with a toast', async () => {
      serve2042();
      const { user } = await renderApp('/tax?tab=declarations');
      const writeText = vi.fn().mockResolvedValue(undefined);
      Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });

      await user.click(await screen.findByRole('button', { name: 'Copier la case 1AJ' }));
      expect(writeText).toHaveBeenCalledWith('42000');
      expect(await within(toasts()).findByText('Case 1AJ copiée : 42000')).toBeInTheDocument();
    });

    it('lists what to enter by hand in its own card', async () => {
      serve2042();
      await renderApp('/tax?tab=declarations');

      const toEnter = await screen.findByRole('region', { name: 'À saisir vous-même' });
      expect(within(toEnter).getByText(/Pension/)).toBeInTheDocument();
    });

    it('says there is nothing to fill when the server lists no box', async () => {
      server.use(http.get('*/api/v1/tax', () => HttpResponse.json({ ...filled(), declaration: { boxes: [], toEnter: [] } })));
      await renderApp('/tax?tab=declarations');

      expect(await screen.findByText('Aucune case à remplir')).toBeInTheDocument();
      expect(screen.queryByRole('button', { name: 'Exporter en CSV' })).not.toBeInTheDocument();
    });
  });
  describe('the 2044 in cards', () => {
    const url = '/tax?tab=declarations&form=2044';
    const serve2044 = (over: Partial<Rental2044> = {}, totals: Partial<Rental2044['totals']> = {}) => server.use(
      http.get('*/api/v1/tax', () => HttpResponse.json(rental())),
      http.get('*/api/v1/tax/2044', () => HttpResponse.json({ ...view2044(), ...over, totals: { ...view2044().totals, ...totals } })),
    );
    const notice = { code: 'rental_charges_removed', item: 'a', name: 'Studio', amount: '600.00' };

    it('words a deficit without a sign', async () => {
      serve2044({}, { result: '-500.00', globalDeficit: '500.00' });
      await renderApp(url);

      const figure = (await screen.findByText('Déficit', { selector: 'dt' })).nextElementSibling!;
      expect(figure).toHaveTextContent('500,00 €');
      expect(figure.textContent).not.toMatch(/-/);
      expect(figure.className).not.toMatch(/text-status-|text-destructive/);
    });

    it('shares the page’s notices card', async () => {
      serve2044({ notices: [notice] });
      const { container } = await renderApp(url);

      const region = await screen.findByRole('region', { name: 'À savoir' });
      expect(within(region).getByText('600,00 €')).toBeInTheDocument();
      expect(within(region).getByRole('link', { name: 'Studio' })).toHaveAttribute('href', '/properties/a');
      expect(container.querySelector('.border-status-warning\\/60')).toBeNull();
      expect(region.closest('.print\\:hidden')).not.toBeNull();
    });

    it('copies a property’s line and the household’s totals', async () => {
      serve2044({}, { result: '-500.00', globalDeficit: '500.00' });
      const { user } = await renderApp(url);
      const writeText = vi.fn().mockResolvedValue(undefined);
      Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });

      await user.click(await screen.findByRole('button', { name: 'Copier la ligne 211 (Studio)' }));
      expect(writeText).toHaveBeenLastCalledWith('6000');
      await user.click(screen.getByRole('button', { name: 'Copier la ligne 420 (Totaux du foyer)' }));
      expect(writeText).toHaveBeenLastCalledWith('-500');
    });

    it('shows skeletons while the 2044 loads', async () => {
      server.use(
        http.get('*/api/v1/tax', () => HttpResponse.json(rental())),
        http.get('*/api/v1/tax/2044', () => new Promise(() => {})),
      );
      const { container } = await renderApp(url);

      await screen.findByRole('radiogroup', { name: 'Formulaire' });
      expect(screen.getByRole('status', { name: 'Calcul de l’impôt' })).toBeInTheDocument();
      expect(container.querySelector('.shimmer:not([data-slot="skeleton"])')).toBeNull();
    });

    it('prints the 2044’s cards, not its notices', async () => {
      serve2044({ notices: [notice] });
      await renderApp(url);

      const studio = await screen.findByRole('region', { name: 'Studio' });
      expect(studio.closest('.print\\:hidden')).toBeNull();
      expect(screen.getByRole('heading', { level: 2, name: 'Déclaration 2044 — revenus 2025', hidden: true })).toHaveClass('print:block');
      expect((await screen.findByRole('region', { name: 'À savoir' })).closest('.print\\:hidden')).not.toBeNull();
    });
  });
});
