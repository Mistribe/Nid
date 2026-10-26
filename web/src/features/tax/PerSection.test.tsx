import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { describe, expect, it, vi } from 'vitest';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { TaxResult } from './api';
import { PerSection } from './PerSection';
import { ADULT, estimate, result } from './taxTestData';

const declarantPer = (over: Partial<NonNullable<TaxResult['declarants'][number]['per']>> = {}) => ({
  ceiling: '5843.13', source: 'notice', expiring: '0.00', contributions: '3000.00', deducted: '3000.00', excess: '0.00', remaining: '3220.80', ...over,
});
const withPer = (over: Partial<TaxResult> = {}, per = declarantPer()) => result({
  declarants: [{ ...result().declarants[0]!, person: ADULT, per }],
  per: { pooled: false, remaining: '3220.80', taxSaved: '900.00', roomInBracket: '10421.00' },
  ...over,
});
const show = (r: TaxResult, names = ['']) => render(<PerSection result={r} names={names} year={2026} rulesYear={2025} onProfile={() => undefined} />);

describe('PerSection', () => {
  it('shows the ceiling, the deduction and what is left to pay', () => {
    show(withPer());
    const alice = screen.getByRole('group', { name: 'Alice' });
    expect(within(alice).getByRole('row', { name: /Plafond \(avis\)/ })).toHaveTextContent('5 843,13 €');
    expect(within(alice).getByRole('row', { name: /Déduit/ })).toHaveTextContent('3 000,00 €');
    expect(within(alice).getByRole('row', { name: /Reste à verser avant le 31 décembre/ })).toHaveTextContent('3 220,80 €');
    expect(screen.getByText('Barème 2025')).toBeInTheDocument();
  });

  it('says what expires on December 31st', () => {
    show(withPer({}, declarantPer({ expiring: '1200.00' })));
    expect(screen.getByText('dont 1 200,00 € perdus au 31 décembre')).toBeInTheDocument();
  });

  it('warns when contributions go beyond the ceiling, with a way to the profile', async () => {
    const onProfile = vi.fn();
    render(<PerSection result={withPer({ warnings: ['per_over_ceiling'] }, declarantPer({ source: 'estimated', contributions: '8000.00', deducted: '5843.13', excess: '2156.87', remaining: '0.00' }))}
      names={['']} year={2026} rulesYear={2025} onProfile={onProfile} />);
    expect(screen.getByText('Alice : 2 156,87 € versés ne sont pas déduits, le plafond de 5 843,13 € est atteint.')).toBeInTheDocument();
    await userEvent.setup().click(screen.getByRole('button', { name: 'Saisissez le plafond de votre avis dans Profil fiscal.' }));
    expect(onProfile).toHaveBeenCalled();
  });

  it('offers the way to the profile only when the ceiling is estimated', () => {
    show(withPer({ warnings: ['per_over_ceiling'] }, declarantPer({ source: 'notice', excess: '100.00' })));
    expect(screen.getByText(/Alice : 100,00 € versés ne sont pas déduits, le plafond de 5 843,13 € est atteint\.$/)).toBeInTheDocument();
    expect(screen.queryByText('Saisissez le plafond de votre avis dans Profil fiscal.')).not.toBeInTheDocument();
  });

  it('offers the way to the profile when the ceiling is estimated and exceeded', () => {
    show(withPer({ warnings: ['per_over_ceiling'] }, declarantPer({ source: 'estimated', excess: '100.00' })));
    expect(screen.getByRole('button', { name: 'Saisissez le plafond de votre avis dans Profil fiscal.' })).toBeInTheDocument();
  });

  it('says when the ceiling is estimated', () => {
    show(withPer({ warnings: ['per_ceiling_estimated'] }, declarantPer({ source: 'estimated' })));
    expect(screen.getByText('Plafond de Alice estimé sur les revenus 2025 : saisissez celui de votre avis pour un chiffre exact.')).toBeInTheDocument();
    expect(screen.getByRole('row', { name: /Plafond \(estimé\)/ })).toBeInTheDocument();
  });

  it('shows the household line when pooled', () => {
    show(withPer({ per: { pooled: true, remaining: '5000.00', taxSaved: '900.00', roomInBracket: '10421.00' } }));
    expect(within(screen.getByRole('group', { name: 'Foyer (plafonds mutualisés)' })).getByRole('row', { name: /Reste à verser/ })).toHaveTextContent('5 000,00 €');
  });

  it('shows no household line when not pooled', () => {
    show(withPer());
    expect(screen.queryByText('Foyer (plafonds mutualisés)')).not.toBeInTheDocument();
  });

  it('names the envelopes the contributions come from', () => {
    show(withPer(), ['PER Avenir']);
    expect(screen.getByText('flux de PER Avenir')).toBeInTheDocument();
  });

  it('shows the tax saved and the room in the bracket', () => {
    show(withPer());
    expect(screen.getByText('Économie d’impôt')).toBeInTheDocument();
    expect(screen.getByText('900,00 €')).toBeInTheDocument();
    expect(screen.getByText('encore 10 421 € déductibles à 11 %')).toBeInTheDocument();
  });

  it('renders nothing without PER figures', () => {
    const { container } = show(result());
    expect(container).toBeEmptyDOMElement();
  });
});

describe('TaxPage, PER', () => {
  it('shows the block on the Détail tab, the envelope names, and flags a PER without contributions', async () => {
    const loaded = estimate();
    loaded.result = withPer({ warnings: ['per_over_ceiling'] }, declarantPer({ excess: '100.00' }));
    loaded.inputs.declarants[0]!.perEnvelopes = ['PER Avenir'];
    loaded.notices = [{ code: 'per_without_contributions', item: 'e1', name: 'PER Avenir', amount: '0.00' }];
    server.use(http.get('*/api/v1/tax', () => HttpResponse.json(loaded)));
    await renderApp('/tax');

    expect(await screen.findByText('PER Avenir : aucun versement enregistré cette année.')).toBeInTheDocument();
    expect(screen.getByText('flux de PER Avenir')).toBeInTheDocument();
    expect(screen.getAllByText(/100,00 € versés ne sont pas déduits/)).toHaveLength(1);
    expect(screen.queryByText('per_over_ceiling')).not.toBeInTheDocument();
  });

  it('saves the notice ceiling, what is lost and the pooling in the profile', async () => {
    const sent: unknown[] = [];
    const loaded = estimate();
    loaded.adults = [{ id: ADULT, displayName: 'Alice' }, { id: 'b', displayName: 'Bob' }];
    server.use(
      http.get('*/api/v1/tax', () => HttpResponse.json(loaded)),
      http.put('*/api/v1/tax/profile/:year', async ({ request }) => { sent.push(await request.json()); return HttpResponse.json(loaded.profile); }),
    );
    const { user } = await renderApp('/tax?tab=profile');

    const alice = await screen.findByRole('group', { name: 'Épargne retraite de Alice' });
    expect(within(alice).getByText('Avis d’imposition, rubrique Plafond épargne retraite.')).toBeInTheDocument();
    await user.type(within(alice).getByLabelText('Plafond épargne retraite (avis)'), '5843,13');
    await user.type(within(alice).getByLabelText('dont perdu au 31 décembre'), '1200');
    await user.click(screen.getByRole('checkbox', { name: 'Mutualiser les plafonds des deux déclarants (case 6QR)' }));
    await user.click(screen.getByRole('button', { name: 'Enregistrer le profil' }));

    expect(await screen.findByText('Profil enregistré.')).toBeInTheDocument();
    expect(sent[0]).toMatchObject({ perCeilings: { [ADULT]: '5843.13' }, perCeilingsExpiring: { [ADULT]: '1200' }, perPooling: true });
  });

  it('sends the PER inputs of the simulation', async () => {
    const sent: unknown[] = [];
    server.use(
      http.get('*/api/v1/tax', () => HttpResponse.json(estimate())),
      http.post('*/api/v1/tax/simulate', async ({ request }) => { sent.push(await request.json()); return HttpResponse.json(result()); }),
    );
    const { user } = await renderApp('/tax?tab=simulation');

    await user.type(await screen.findByLabelText('Plafond épargne retraite (avis)'), '5000');
    await user.clear(screen.getByLabelText('Revenus professionnels nets de l’année précédente'));
    await user.type(screen.getByLabelText('Revenus professionnels nets de l’année précédente'), '40000');
    await user.click(screen.getByRole('button', { name: 'Calculer' }));
    await screen.findByRole('table', { name: 'Estimation et simulation' });
    expect(sent[0]).toMatchObject({ declarants: [{ perCeiling: '5000', previousYearIncome: '40000', perExpiring: '0.00' }], perPooling: false });
  });

  it('keeps a PER warning the block cannot show in the top notes, with the generic text', async () => {
    const loaded = estimate();
    loaded.result = withPer({ warnings: ['per_over_ceiling'] }, declarantPer({ excess: '0.00' }));
    server.use(http.get('*/api/v1/tax', () => HttpResponse.json(loaded)));
    await renderApp('/tax');

    expect(await screen.findByText('Des versements PER dépassent le plafond : voir le Profil fiscal pour saisir celui de votre avis.')).toBeInTheDocument();
  });

  it('keeps a PER warning whose declarant has no PER figures in the top notes', async () => {
    const loaded = estimate();
    loaded.result = result({ warnings: ['per_ceiling_estimated'] });
    server.use(http.get('*/api/v1/tax', () => HttpResponse.json(loaded)));
    await renderApp('/tax');

    expect(await screen.findByText('Un plafond PER est estimé : saisissez celui de votre avis pour un chiffre exact.')).toBeInTheDocument();
  });

  it('shows the over-ceiling warning in the top notes on the Profil tab', async () => {
    const loaded = estimate();
    loaded.result = withPer({ warnings: ['per_over_ceiling'] }, declarantPer({ excess: '100.00' }));
    server.use(http.get('*/api/v1/tax', () => HttpResponse.json(loaded)));
    await renderApp('/tax?tab=profile');

    expect(await screen.findByText(/Alice : 100,00 € versés ne sont pas déduits/)).toBeInTheDocument();
  });

  it('tells each declarant’s PER warning once in the top notes, with two declarants', async () => {
    const loaded = estimate();
    const [alice, bob] = [declarantPer({ excess: '100.00' }), declarantPer({ excess: '200.00', source: 'estimated' })];
    loaded.result = withPer({ warnings: ['per_over_ceiling', 'per_over_ceiling', 'per_ceiling_estimated', 'per_ceiling_estimated'] }, alice);
    loaded.result.declarants = [{ ...loaded.result.declarants[0]!, per: alice }, { ...loaded.result.declarants[0]!, name: 'Bob', person: 'b', per: bob }];
    server.use(http.get('*/api/v1/tax', () => HttpResponse.json(loaded)));
    const errors = vi.spyOn(console, 'error').mockImplementation(() => undefined);
    await renderApp('/tax?tab=profile');

    expect(await screen.findAllByText(/versés ne sont pas déduits/)).toHaveLength(2);
    expect(screen.getAllByText(/Plafond de Bob estimé/)).toHaveLength(1);
    expect(errors.mock.calls.flat().join(' ')).not.toContain('same key');
    errors.mockRestore();
  });

  it('removes a ceiling from the profile when its fields are cleared', async () => {
    const sent: unknown[] = [];
    const loaded = estimate();
    loaded.profile = { ...loaded.profile, perCeilings: { [ADULT]: '5843.13' }, perCeilingsExpiring: { [ADULT]: '1200.00' } };
    server.use(
      http.get('*/api/v1/tax', () => HttpResponse.json(loaded)),
      http.put('*/api/v1/tax/profile/:year', async ({ request }) => { sent.push(await request.json()); return HttpResponse.json(loaded.profile); }),
    );
    const { user } = await renderApp('/tax?tab=profile');

    const alice = await screen.findByRole('group', { name: 'Épargne retraite de Alice' });
    await user.clear(within(alice).getByLabelText('Plafond épargne retraite (avis)'));
    await user.clear(within(alice).getByLabelText('dont perdu au 31 décembre'));
    await user.click(screen.getByRole('button', { name: 'Enregistrer le profil' }));

    expect(await screen.findByText('Profil enregistré.')).toBeInTheDocument();
    const body = (sent[0] as { perCeilings: Record<string, string>; perCeilingsExpiring: Record<string, string> });
    expect(body.perCeilings).not.toHaveProperty(ADULT);
    expect(body.perCeilingsExpiring).not.toHaveProperty(ADULT);
  });
});
