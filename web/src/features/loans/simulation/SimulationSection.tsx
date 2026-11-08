import { zodResolver } from '@hookform/resolvers/zod';
import type { TFunction } from 'i18next';
import { useEffect, useRef, useState } from 'react';
import { Controller, useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { z } from 'zod';
import { useApi } from '@/api/ApiProvider';
import { DateField } from '@/components/form/DateField';
import { formatLocalDate } from '@/components/LocalDate';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Table, TableBody, TableCell, TableFooter, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { Section } from '@/components/page/Section';
import { ISO_DATE, toWireAmount } from '@/lib/amount';
import { Field } from '@/components/form/Field';
import { useSubmitErrors } from '@/components/form/useSubmitErrors';
import { todayIso } from '@/lib/today';
import { SIMULATIONS, type LoanDetail, type LoanOutcome, type LoanSimulation } from '@/api/loans';
import { EFFECTS, money, signedMoney } from '../format';
import { LOAN_AMOUNT } from '../new/loanForm';
import { presentRefusal } from '../refusal';
import type { RepaymentPrefill } from '../repayments/repayment';
import '@/locales/loans';
import '@/locales/forms';

const num = 'text-right tabular-nums';
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
export function SimulationSection({ loan, onRecord, landing, onLanded }: {
  loan: LoanDetail; onRecord?: (prefill: Omit<RepaymentPrefill, 'seq'>) => void; landing?: boolean; onLanded?: () => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const form = useForm<SimulationValues>({ resolver: zodResolver(schema(t)), defaultValues: { on: todayIso(), amount: '', penalty: '' } });
  const errors = useSubmitErrors(form.setError, PATHS);
  const simulate = $api.useMutation('post', SIMULATIONS);
  const [result, setResult] = useState<{ view: LoanSimulation; asked: SimulationValues } | null>(null);
  // The header's « Simuler » lands here: the heading takes the focus, then the page forgets the request.
  const headingRef = useRef<HTMLHeadingElement>(null);
  useEffect(() => {
    if (landing) { headingRef.current?.focus(); onLanded?.(); }
  }, [landing]); // eslint-disable-line react-hooks/exhaustive-deps -- only a new request lands; `onLanded` is recreated every render
  // A write refetches the loan: a result computed before it no longer holds.
  const clear = () => { setResult(null); errors.clear(); };
  // eslint-disable-next-line react-hooks/exhaustive-deps -- only a new loan resets; `clear` is recreated every render
  useEffect(clear, [loan]);

  const submit = form.handleSubmit((values) => {
    errors.clear();
    simulate.mutate(
      {
        params: { path: { id: loan.id } },
        // No effect: the server answers both outcomes.
        body: { on: values.on, amount: toWireAmount(values.amount), ...(values.penalty.trim() ? { penalty: toWireAmount(values.penalty) } : {}) },
      },
      {
        onSuccess: (view) => { setResult({ view, asked: values }); toast.success(t('loan_simulation.announced')); },
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
    <Section title={t('loan_simulation.title')} headingRef={headingRef}>
      <p className="text-sm text-muted-foreground">{t('loan_simulation.help')}</p>
      {problem ? <ProblemMessage problem={problem} /> : null}
      <form noValidate onSubmit={(ev) => void submit(ev)} className="grid items-start gap-3 sm:grid-cols-2 lg:grid-cols-4">
        <Controller control={form.control} name="on" render={({ field }) => (
          <Field label={t('loan_simulation.on')} error={e.on?.message}>
            {(c) => <DateField {...c} value={field.value} onChange={(v) => { field.onChange(v); clear(); }} />}
          </Field>
        )} />
        <Field label={t('loan_simulation.amount')} error={e.amount?.message}>
          {(c) => <Input inputMode="decimal" {...c} {...form.register('amount', { onChange: clear })} />}
        </Field>
        <Field label={t('loan_simulation.penalty')} hint={t('loan_repayments.penalty_help')} error={e.penalty?.message}>
          {(c) => <Input inputMode="decimal" {...c} {...form.register('penalty', { onChange: clear })} />}
        </Field>
        <div className="sm:pt-6"><Button type="submit" disabled={simulate.isPending}>{t('loan_simulation.submit')}</Button></div>
      </form>
      {result ? (
        <Table aria-label={t('loan_simulation.result')}>
          <TableHeader>
            <TableRow>
              <TableHead>{t('loan_simulation.line')}</TableHead>
              {EFFECTS.map((effect) => <TableHead key={effect}>{t(`loan_repayments.effect_option.${effect}`)}</TableHead>)}
            </TableRow>
          </TableHeader>
          <TableBody>
            {lines.map(([label, cell]) => (
              <TableRow key={label}>
                <TableHead scope="row">{label}</TableHead>
                {EFFECTS.map((effect) => {
                  const outcome = result.view.outcomes.find((o) => o.effect === effect);
                  return <TableCell key={effect} className={num}>{outcome ? cell(outcome) : null}</TableCell>;
                })}
              </TableRow>
            ))}
          </TableBody>
          {onRecord ? (
            <TableFooter>
              <TableRow>
                <TableCell />
                {EFFECTS.map((effect) => (
                  <TableCell key={effect} className="text-right">
                    <Button type="button" variant="outline" size="sm" aria-label={t(`loan_simulation.record_${effect}`)}
                      onClick={() => onRecord({ onDate: result.asked.on, amount: result.asked.amount, effect, penalty: result.asked.penalty })}>
                      {t('loan_simulation.record')}
                    </Button>
                  </TableCell>
                ))}
              </TableRow>
            </TableFooter>
          ) : null}
        </Table>
      ) : null}
    </Section>
  );
}
