import { zodResolver } from '@hookform/resolvers/zod';
import { useNavigate } from '@tanstack/react-router';
import type { TFunction } from 'i18next';
import { useEffect, useState } from 'react';
import { Controller, useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { z } from 'zod';
import { useApi } from '@/api/ApiProvider';
import { DateField } from '@/components/form/DateField';
import { Field } from '@/components/form/Field';
import { formatLocalDate } from '@/components/LocalDate';
import { Section } from '@/components/page/Section';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { ISO_DATE, toWireAmount } from '../../subscriptions/edits/amount';
import { useSubmitErrors } from '../../subscriptions/edits/useSubmitErrors';
import { todayIso } from '../../subscriptions/today';
import { RENEGOTIATION, type LoanDetail, type LoanRenegotiation, type RenegotiationSide } from '../api';
import { money, signedMoney } from '../format';
import { LOAN_AMOUNT } from '../new/loanForm';
import { isPercentRate, percentToFraction } from '../rate';
import { presentRefusal } from '../refusal';
import '@/locales/loans';
import '@/locales/forms';

const num = 'text-right tabular-nums';
const PATHS = { on: 'on', annualRate: 'rate', payments: 'payments', bankFees: 'bankFees', guaranteeFees: 'guaranteeFees', penalty: 'penalty' } as const;
const optionalAmount = (t: TFunction) => z.string().refine((v) => v.trim() === '' || LOAN_AMOUNT.test(v.trim()), t('loan_form.amount_format'));

const schema = (t: TFunction) => z.object({
  on: z.string().regex(ISO_DATE, t('edit.required')),
  rate: z.string().refine(isPercentRate, t('loan_form.rate_format')),
  payments: z.string().refine((v) => v.trim() === '' || (/^\d{1,3}$/.test(v.trim()) && Number(v) >= 1 && Number(v) <= 600), t('loan_renegotiation.payments_format')),
  bankFees: optionalAmount(t),
  guaranteeFees: optionalAmount(t),
  penalty: optionalAmount(t),
});
type Values = z.infer<ReturnType<typeof schema>>;

const optional = (v: string) => (v.trim() ? toWireAmount(v) : undefined);

/**
 * Issue #403: the loan renegotiated at a new rate, side by side with the loan as it runs, every figure the server's; read
 * access is enough. « Enregistrer », given to writers, opens the contract page with the change prepared: nothing is sent.
 */
export function RenegotiationSection({ loan }: { loan: LoanDetail }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const navigate = useNavigate();
  const form = useForm<Values>({
    resolver: zodResolver(schema(t)),
    defaultValues: { on: todayIso(), rate: '', payments: '', bankFees: '', guaranteeFees: '', penalty: '' },
  });
  const errors = useSubmitErrors(form.setError, PATHS);
  const simulate = $api.useMutation('post', RENEGOTIATION);
  const [result, setResult] = useState<LoanRenegotiation | null>(null);
  const clear = () => { setResult(null); errors.clear(); };
  // eslint-disable-next-line react-hooks/exhaustive-deps -- only a new loan resets; `clear` is recreated every render
  useEffect(clear, [loan]);

  const submit = form.handleSubmit((v) => {
    errors.clear();
    simulate.mutate(
      {
        params: { path: { id: loan.id } },
        body: {
          on: v.on, annualRate: percentToFraction(v.rate)!, ...(v.payments.trim() ? { payments: Number(v.payments) } : {}),
          bankFees: optional(v.bankFees), guaranteeFees: optional(v.guaranteeFees), penalty: optional(v.penalty),
        },
      },
      { onSuccess: setResult, onError: (error) => errors.onError(error) },
    );
  });

  const e = form.formState.errors;
  const problem = errors.problem ? presentRefusal(t, simulate.error) : null;
  const m = (amount: string) => money(loan.currency, amount);
  const lines: [string, (s: RenegotiationSide) => string][] = [
    [t('loan_renegotiation.payment'), (s) => m(s.payment)],
    [t('loan_renegotiation.end'), (s) => (s.endDate ? formatLocalDate(s.endDate) : '')],
    [t('loan_renegotiation.payments_left'), (s) => String(s.paymentsLeft)],
    [t('loan_renegotiation.interest_left'), (s) => m(s.interestLeft)],
    [t('loan_renegotiation.insurance_left'), (s) => m(s.insuranceLeft)],
    [t('loan_renegotiation.fees'), (s) => m(s.fees)],
    [t('loan_renegotiation.total_cost'), (s) => m(s.totalCost)],
  ];
  const register = (name: keyof Values) => form.register(name, { onChange: clear });

  return (
    <Section title={t('loan_renegotiation.title')}>
      <p className="text-sm text-muted-foreground">{t('loan_renegotiation.help')}</p>
      {problem ? <ProblemMessage problem={problem} /> : null}
      <form noValidate onSubmit={(ev) => void submit(ev)} className="grid items-start gap-3 sm:grid-cols-2 lg:grid-cols-3">
        <Controller control={form.control} name="on" render={({ field }) => (
          <Field label={t('loan_renegotiation.on')} hint={t('loan_renegotiation.on_help')} error={e.on?.message}>
            {(c) => <DateField {...c} value={field.value} onChange={(v) => { field.onChange(v); clear(); }} />}
          </Field>
        )} />
        <Field label={t('loan_renegotiation.rate')} error={e.rate?.message}>
          {(c) => <Input inputMode="decimal" {...c} {...register('rate')} />}
        </Field>
        <Field label={t('loan_renegotiation.payments')} hint={t('loan_renegotiation.payments_help')} error={e.payments?.message}>
          {(c) => <Input inputMode="numeric" {...c} {...register('payments')} />}
        </Field>
        <Field label={t('loan_renegotiation.bank_fees')} error={e.bankFees?.message}>
          {(c) => <Input inputMode="decimal" {...c} {...register('bankFees')} />}
        </Field>
        <Field label={t('loan_renegotiation.guarantee_fees')} error={e.guaranteeFees?.message}>
          {(c) => <Input inputMode="decimal" {...c} {...register('guaranteeFees')} />}
        </Field>
        <Field label={t('loan_renegotiation.penalty')} hint={t('loan_repayments.penalty_help')} error={e.penalty?.message}>
          {(c) => <Input inputMode="decimal" {...c} {...register('penalty')} />}
        </Field>
        <div><Button type="submit" disabled={simulate.isPending}>{t('loan_renegotiation.submit')}</Button></div>
      </form>
      {result ? (
        <div className="space-y-3">
          <Table aria-label={t('loan_renegotiation.result')}>
            <TableHeader>
              <TableRow>
                <TableHead>{t('loan_simulation.line')}</TableHead>
                <TableHead className="text-right">{t('loan_renegotiation.current')}</TableHead>
                <TableHead className="text-right">{t('loan_renegotiation.renegotiated')}</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {lines.map(([label, cell]) => (
                <TableRow key={label}>
                  <TableHead scope="row">{label}</TableHead>
                  <TableCell className={num}>{cell(result.current)}</TableCell>
                  <TableCell className={num}>{cell(result.renegotiated)}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
          <p>{t('loan_renegotiation.saving', { amount: signedMoney(loan.currency, result.saving) })}</p>
          <p>{result.breakEvenOn
            ? t('loan_renegotiation.break_even', { date: formatLocalDate(result.breakEvenOn) })
            : t('loan_renegotiation.never')}</p>
          {loan.canWrite ? (
            <Button type="button" variant="outline"
              onClick={() => void navigate({ to: '/loans/$id/contract', params: { id: loan.id }, state: { loanContract: result.contract } })}>
              {t('loan_renegotiation.record')}
            </Button>
          ) : null}
        </div>
      ) : null}
    </Section>
  );
}
