import { zodResolver } from '@hookform/resolvers/zod';
import type { TFunction } from 'i18next';
import { useEffect, useState } from 'react';
import { useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { z } from 'zod';
import { useApi } from '@/api/ApiProvider';
import { formatLocalDate } from '@/components/LocalDate';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Section } from '../../income/Section';
import { ISO_DATE, toWireAmount } from '../../subscriptions/edits/amount';
import { Field } from '../../subscriptions/edits/Field';
import { useSubmitErrors } from '../../subscriptions/edits/useSubmitErrors';
import { todayIso } from '../../subscriptions/today';
import { SIMULATIONS, type LoanDetail, type LoanOutcome, type LoanSimulation } from '../api';
import { EFFECTS, money, signedMoney } from '../format';
import { LOAN_AMOUNT } from '../new/newLoan';
import { presentRefusal } from '../refusal';
import type { RepaymentPrefill } from '../repayments/repayment';

const input = 'rounded-md border px-3 py-2';
const th = 'px-2 py-1 text-left font-medium';
const td = 'px-2 py-1';
const num = 'px-2 py-1 text-right tabular-nums';
const PATHS = { on: 'on', amount: 'amount', penalty: 'penalty' } as const;

const schema = (t: TFunction) => z.object({
  on: z.string().regex(ISO_DATE, t('edit.required')),
  amount: z.string().trim().regex(LOAN_AMOUNT, t('loan_form.amount_format')),
  penalty: z.string().refine((v) => v.trim() === '' || LOAN_AMOUNT.test(v.trim()), t('loan_form.amount_format')),
});
type SimulationValues = z.infer<ReturnType<typeof schema>>;

/**
 * 3B2 design §8: an early repayment simulated on the loan as stored (read access is enough), both effects compared side
 * by side, every figure the server's. `onRecord`, given to writers only, opens the repayments' add form with a column.
 */
export function SimulationSection({ loan, onRecord }: { loan: LoanDetail; onRecord?: (prefill: Omit<RepaymentPrefill, 'seq'>) => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const form = useForm<SimulationValues>({ resolver: zodResolver(schema(t)), defaultValues: { on: todayIso(), amount: '', penalty: '' } });
  const errors = useSubmitErrors(form.setError, PATHS);
  const simulate = $api.useMutation('post', SIMULATIONS);
  const [result, setResult] = useState<{ view: LoanSimulation; asked: SimulationValues } | null>(null);
  const [announcement, setAnnouncement] = useState('');
  // A write refetches the loan: a result computed before it no longer holds.
  useEffect(() => { setResult(null); }, [loan]);
  const clear = () => setResult(null);

  const submit = form.handleSubmit((values) => {
    errors.clear();
    setAnnouncement('');
    simulate.mutate(
      {
        params: { path: { id: loan.id } },
        // No effect: the server answers both outcomes.
        body: { on: values.on, amount: toWireAmount(values.amount), ...(values.penalty.trim() ? { penalty: toWireAmount(values.penalty) } : {}) },
      },
      {
        onSuccess: (view) => { setResult({ view, asked: values }); setAnnouncement(t('loan_simulation.announced')); },
        onError: (error) => errors.onError(error),
      },
    );
  });

  const e = form.formState.errors;
  const problem = errors.problem ? presentRefusal(t, simulate.error) : null;
  const m = (amount: string) => money(loan.currency, amount);
  const date = (iso?: string | null) => (iso ? formatLocalDate(iso) : '');
  const change = (before: string, after: string) => t('loan_simulation.change', { before, after });
  const lines: [string, (o: LoanOutcome) => string][] = [
    [t('loan_simulation.payment'), (o) => change(m(o.paymentBefore), m(o.paymentAfter))],
    [t('loan_simulation.end'), (o) => change(date(o.endBefore), date(o.endAfter))],
    [t('loan_simulation.payments'), (o) => change(String(o.paymentsBefore), String(o.paymentsAfter))],
    [t('loan_simulation.interest'), (o) => change(m(o.interestBefore), m(o.interestAfter))],
    [t('loan_simulation.interest_saved'), (o) => m(o.interestSaved)],
    [t('loan_simulation.net_saving'), (o) => signedMoney(loan.currency, o.netSaving)],
  ];

  return (
    <Section title={t('loan_simulation.title')} announcement={announcement}>
      <p className="text-sm text-muted-foreground">{t('loan_simulation.help')}</p>
      {problem ? <ProblemMessage problem={problem} /> : null}
      <form noValidate onSubmit={(ev) => void submit(ev)} className="flex flex-wrap items-end gap-3">
        <Field label={t('loan_simulation.on')} error={e.on?.message}>
          {(c) => <input type="date" {...c} {...form.register('on', { onChange: clear })} className={input} />}
        </Field>
        <Field label={t('loan_simulation.amount')} error={e.amount?.message}>
          {(c) => <input inputMode="decimal" {...c} {...form.register('amount', { onChange: clear })} className={`w-32 ${input}`} />}
        </Field>
        <Field label={t('loan_simulation.penalty')} hint={t('loan_repayments.penalty_help')} error={e.penalty?.message}>
          {(c) => <input inputMode="decimal" {...c} {...form.register('penalty', { onChange: clear })} className={`w-32 ${input}`} />}
        </Field>
        <Button type="submit" disabled={simulate.isPending}>{t('loan_simulation.submit')}</Button>
      </form>
      {result ? (
        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <caption className="text-left font-medium">{t('loan_simulation.result')}</caption>
            <thead>
              <tr>
                <th scope="col" className={th}>{t('loan_simulation.line')}</th>
                {EFFECTS.map((effect) => <th key={effect} scope="col" className={th}>{t(`loan_repayments.effect_option.${effect}`)}</th>)}
              </tr>
            </thead>
            <tbody>
              {lines.map(([label, cell]) => (
                <tr key={label} className="border-t">
                  <th scope="row" className={th}>{label}</th>
                  {EFFECTS.map((effect) => {
                    const outcome = result.view.outcomes.find((o) => o.effect === effect);
                    return <td key={effect} className={num}>{outcome ? cell(outcome) : null}</td>;
                  })}
                </tr>
              ))}
            </tbody>
            {onRecord ? (
              <tfoot>
                <tr className="border-t">
                  <td className={td} />
                  {EFFECTS.map((effect) => (
                    <td key={effect} className={td}>
                      <Button type="button" variant="outline" size="sm" aria-label={t(`loan_simulation.record_${effect}`)}
                        onClick={() => onRecord({ onDate: result.asked.on, amount: result.asked.amount, effect, penalty: result.asked.penalty })}>
                        {t('loan_simulation.record')}
                      </Button>
                    </td>
                  ))}
                </tr>
              </tfoot>
            ) : null}
          </table>
        </div>
      ) : null}
    </Section>
  );
}
