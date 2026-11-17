import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw/http';
import { describe, expect, it, vi } from 'vitest';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { TaxResult } from '@/api/tax';
import { PerProgress, PerRows } from './PerSection';
import { DeclarantTable } from './ResultTable';
import { ADULT, estimate, result } from './taxTestData';

const declarantPer = (over: Partial<NonNullable<TaxResult['declarants'][number]['per']>> = {}) => ({
  ceiling: '5843.13', source: 'notice', expiring: '0.00', contributions: '3000.00', deducted: '3000.00', excess: '0.00', remaining: '2843.13', minimum: '0.00', minimumEstimated: false, ...over,
});
const withPer = (over: Partial<TaxResult> = {}, per = declarantPer()) => result({
  declarants: [{ ...result().declarants[0]!, person: ADULT, perDeducted: per.deducted, per }],
  per: { pooled: false, remaining: '2843.13', taxSaved: '900.00', roomInBracket: '10421.00', remainingTaxSaved: '900.00' },
  ...over,
});
const block = (r: TaxResult, names: string[], onProfile: () => void) => (
  <><DeclarantTable result={r}><PerRows result={r} names={names} /></DeclarantTable><PerProgress result={r} onProfile={onProfile} /></>
);
const show = (r: TaxResult, names = ['']) => render(block(r, names, () => undefined));
const cell = (row: RegExp) => screen.getByRole('row', { name: row });

describe('PerRows and PerProgress', () => {
  it('shows the ceiling, the deduction and what is left to pay', () => {
    show(withPer());
    expect(screen.getByRole('columnheader', { name: 'Alice' })).toBeInTheDocument();
    expect(cell(/Plafond \(avis\)/)).toHaveTextContent('5 843,13 €');
    expect(cell(/^Versements PER déduits/)).toHaveTextContent('3 000,00 €');
    expect(screen.queryByRole('row', { name: /^Déduit/ })).not.toBeInTheDocument();
    expect(screen.getByText('PER · Barème 2025')).toBeInTheDocument();
  });

  it('shows the deduction against the ceiling', () => {
    show(withPer());
    expect(screen.getByRole('progressbar', { name: 'PER de Alice' }).getAttribute('aria-valuetext')?.replace(/\s/g, ' ')).toBe('3 000,00 € déduits sur un plafond de 5 843,13 €');
    expect(screen.getByText('2 843,13 € à verser avant le 31 décembre')).toBeInTheDocument();
    expect(screen.queryByRole('row', { name: /Reste à verser/ })).not.toBeInTheDocument();
  });

  it('a ceiling of zero draws an empty progress', () => {
    show(withPer({}, declarantPer({ ceiling: '0.00', deducted: '0.00', contributions: '0.00', remaining: '0.00' })));
    expect(screen.getByRole('progressbar', { name: 'PER de Alice' })).toHaveAttribute('aria-valuenow', '0');
  });

  it('a deduction above the ceiling fills the progress at 100', () => {
    show(withPer({}, declarantPer({ ceiling: '1000.00', deducted: '3000.00' })));
    expect(screen.getByRole('progressbar', { name: 'PER de Alice' })).toHaveAttribute('aria-valuenow', '100');
  });

  it('no declarant with PER draws no progress at all', () => {
    const r = withPer();
    r.declarants = [{ ...r.declarants[0]!, per: null }];
    show(r);
    expect(screen.queryByRole('progressbar')).not.toBeInTheDocument();
  });

  it('a notice ceiling has no link to the profile', () => {
    show(withPer({ warnings: ['per_ceiling_estimated'] }));
    expect(screen.queryByRole('button', { name: 'Saisir celui de l’avis' })).not.toBeInTheDocument();
  });

  it('over the ceiling says it is reached', () => {
    show(withPer({ warnings: ['per_over_ceiling'] }, declarantPer({ excess: '500.00' })));
    expect(screen.getByText('Plafond atteint : le surplus n’est pas déduit.')).toBeInTheDocument();
  });

  it('an estimated ceiling links to the profile', async () => {
    const onProfile = vi.fn();
    render(block(withPer({ warnings: ['per_ceiling_estimated'] }, declarantPer({ source: 'estimated' })), [''], onProfile));
    await userEvent.setup().click(screen.getByRole('button', { name: 'Saisir celui de l’avis' }));
    expect(onProfile).toHaveBeenCalledTimes(1);
    expect(screen.getByText('Plafond estimé.')).toBeInTheDocument();
  });

  it('no progress for a declarant without PER', () => {
    const r = withPer();
    r.declarants = [r.declarants[0]!, { ...r.declarants[0]!, person: 'b', name: 'Bob', per: null }];
    show(r, ['', '']);
    expect(screen.getAllByRole('progressbar')).toHaveLength(1);
  });

  it('says what expires on December 31st and the minimum that keeps it', () => {
    show(withPer({}, declarantPer({ expiring: '1200.00', minimum: '1700.00' })));
    expect(screen.getByText('dont 1 200,00 € perdus sinon')).toBeInTheDocument();
    expect(cell(/Minimum à verser avant le 31 décembre/)).toHaveTextContent(/^Minimum à verser avant le 31 décembre1 700,00 €$/);
  });

  it('drops the loss once the minimum is paid', () => {
    show(withPer({}, declarantPer({ expiring: '1200.00' })));
    expect(screen.queryByText(/perdus sinon/)).not.toBeInTheDocument();
    expect(cell(/Minimum à verser/)).toHaveTextContent(/^Minimum à verser avant le 31 décembre0,00 €$/);
  });

  it('tells when the year’s own ceiling in the minimum is estimated', () => {
    show(withPer({}, declarantPer({ expiring: '1200.00', minimum: '1700.00', minimumEstimated: true })));
    expect(cell(/Minimum à verser/)).toHaveTextContent('plafond de l’année estimé');
  });

  it('shows no minimum when nothing expires', () => {
    show(withPer());
    expect(screen.queryByRole('row', { name: /Minimum à verser/ })).not.toBeInTheDocument();
  });

  it('names the envelopes the contributions come from', () => {
    show(withPer(), ['PER Avenir']);
    expect(screen.getByText('flux de PER Avenir')).toBeInTheDocument();
  });

  it('tells each ceiling’s source in its cell when the declarants’ differ', () => {
    const r = withPer();
    r.declarants = [r.declarants[0]!, { ...r.declarants[0]!, person: 'b', name: 'Bob', per: declarantPer({ source: 'estimated' }) }];
    show(r, ['', '']);
    const row = cell(/^Plafond/);
    expect(within(row).getByText('avis')).toBeInTheDocument();
    expect(within(row).getByText('estimé')).toBeInTheDocument();
  });

  it('shows a dash for a declarant without PER figures', () => {
    const r = withPer();
    r.declarants = [r.declarants[0]!, { ...r.declarants[0]!, person: 'b', name: 'Bob', per: null }];
    show(r, ['', '']);
    expect(cell(/^Versements(?! PER)/)).toHaveTextContent('—');
  });
});

describe('TaxPage, PER', () => {
  it('shows the block on the Détail tab, the envelope names, and flags a PER without contributions', async () => {
    const loaded = estimate();
    loaded.result = withPer({ warnings: ['per_over_ceiling'] }, declarantPer({ excess: '100.00' }));
    loaded.inputs.declarants[0]!.perEnvelopes = ['PER Avenir'];
    loaded.notices = [{ code: 'per_without_contributions', item: 'e1', name: 'PER Avenir', amount: '0.00' }];
    server.use(http.get('*/api/v1/tax', () => HttpResponse.json(loaded)));
    await renderApp('/tax?tab=detail');

    expect(await screen.findByText('flux de PER Avenir')).toBeInTheDocument();
    expect(screen.getAllByText('Plafond atteint : le surplus n’est pas déduit.')).toHaveLength(1);
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

    const ceiling = await screen.findByRole('textbox', { name: 'Plafond épargne retraite (avis) Alice' });
    expect(ceiling).toHaveAccessibleDescription('Avis d’imposition, rubrique Plafond épargne retraite.');
    await user.type(ceiling, '5843,13');
    await user.type(screen.getByRole('textbox', { name: 'dont perdu au 31 décembre Alice' }), '1200');
    await user.click(screen.getByRole('checkbox', { name: 'Mutualiser les plafonds des deux déclarants' }));
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

    await user.click(await screen.findByRole('button', { name: 'Épargne retraite (PER)' }));
    await user.click(within(screen.getByRole('group', { name: 'Épargne retraite (PER)' })).getByRole('button', { name: 'Plus de champs' }));
    await user.type(screen.getByRole('textbox', { name: 'Plafond épargne retraite (avis) Alice' }), '5000');
    await user.clear(screen.getByRole('textbox', { name: 'Revenus professionnels nets de l’année précédente Alice' }));
    await user.type(screen.getByRole('textbox', { name: 'Revenus professionnels nets de l’année précédente Alice' }), '40000');
    await user.click(screen.getByRole('button', { name: 'Calculer' }));
    await screen.findByRole('table', { name: 'Estimation et simulation' });
    expect(sent[0]).toMatchObject({ declarants: [{ perCeiling: '5000', previousYearIncome: '40000', perExpiring: '0' }], perPooling: false });
  });

  it('keeps a PER warning the block cannot show in À savoir, with the generic text', async () => {
    const loaded = estimate();
    loaded.result = withPer({ warnings: ['per_over_ceiling'] }, declarantPer({ excess: '0.00' }));
    server.use(http.get('*/api/v1/tax', () => HttpResponse.json(loaded)));
    await renderApp('/tax');

    expect(await screen.findByText('Des versements PER dépassent le plafond : voir le Profil fiscal pour saisir celui de votre avis.')).toBeInTheDocument();
  });

  it('says an estimated ceiling as a piste, not in À savoir', async () => {
    const loaded = estimate();
    loaded.result = result({ warnings: ['per_ceiling_estimated'] });
    server.use(http.get('*/api/v1/tax', () => HttpResponse.json(loaded)));
    await renderApp('/tax');

    const pistes = await screen.findByRole('region', { name: 'Pistes' });
    expect(within(pistes).getByText('Saisir le plafond PER de votre avis')).toBeInTheDocument();
    expect(screen.queryByText(/Un plafond PER est estimé/)).not.toBeInTheDocument();
  });

  it('the block tells each warning once', async () => {
    const loaded = estimate();
    const [alice, bob] = [declarantPer({ excess: '100.00' }), declarantPer({ excess: '200.00', source: 'estimated' })];
    loaded.result = withPer({ warnings: ['per_over_ceiling', 'per_over_ceiling', 'per_ceiling_estimated', 'per_ceiling_estimated'] }, alice);
    loaded.result.declarants = [{ ...loaded.result.declarants[0]!, per: alice }, { ...loaded.result.declarants[0]!, name: 'Bob', person: 'b', per: bob }];
    server.use(http.get('*/api/v1/tax', () => HttpResponse.json(loaded)));
    const errors = vi.spyOn(console, 'error').mockImplementation(() => undefined);
    await renderApp('/tax?tab=detail');

    expect(await screen.findAllByText('Plafond atteint : le surplus n’est pas déduit.')).toHaveLength(2);
    expect(screen.getAllByRole('button', { name: 'Saisir celui de l’avis' })).toHaveLength(1);
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

    await user.clear(await screen.findByRole('textbox', { name: 'Plafond épargne retraite (avis) Alice' }));
    await user.clear(screen.getByRole('textbox', { name: 'dont perdu au 31 décembre Alice' }));
    await user.click(screen.getByRole('button', { name: 'Enregistrer le profil' }));

    expect(await screen.findByText('Profil enregistré.')).toBeInTheDocument();
    const body = (sent[0] as { perCeilings: Record<string, string>; perCeilingsExpiring: Record<string, string> });
    expect(body.perCeilings).not.toHaveProperty(ADULT);
    expect(body.perCeilingsExpiring).not.toHaveProperty(ADULT);
  });
});
