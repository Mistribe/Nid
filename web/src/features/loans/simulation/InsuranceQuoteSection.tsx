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
import { SelectField } from '@/components/form/SelectField';
import { Section } from '@/components/page/Section';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Table, TableBody, TableCell, TableFooter, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { ISO_DATE, toWireAmount } from '@/lib/amount';
import { useSubmitErrors } from '@/components/form/useSubmitErrors';
import { todayIso } from '@/lib/today';
import { INSURANCE_QUOTE, type LoanDetail, type LoanInsuranceComparison } from '../api';
import { INSURANCE_MODES, insuranceModeText, money, signedMoney } from '../format';
import { LOAN_AMOUNT } from '../new/loanForm';
import { isPercentRate, percentToFraction } from '../rate';
import { presentRefusal } from '../refusal';
import '@/locales/loans';
import '@/locales/forms';

const num = 'text-right tabular-nums';
const PATHS = {
  on: 'on', 'quotes[0].insurance': 'insurance', 'quotes[0].mode': 'mode', 'quotes[0].monthlyAmount': 'amount', 'quotes[0].annualRate': 'rate',
} as const;

const schema = (t: TFunction) => z.object({
  on: z.string().regex(ISO_DATE, t('edit.required')),
  insurance: z.string(),
  mode: z.enum(INSURANCE_MODES),
  amount: z.string(),
  rate: z.string(),
}).superRefine((v, ctx) => {
  if (v.mode === 'fixed_monthly' && !LOAN_AMOUNT.test(v.amount.trim()))
    ctx.addIssue({ code: 'custom', path: ['amount'], message: t('loan_form.amount_format') });
  if (v.mode !== 'fixed_monthly' && !isPercentRate(v.rate))
    ctx.addIssue({ code: 'custom', path: ['rate'], message: t('loan_form.rate_format') });
});
type Values = z.infer<ReturnType<typeof schema>>;

/**
 * Issue #404: a quote for one insured person's line, compared over the payments left with every line still due; read access
 * is enough. « Enregistrer », given to writers, opens the contract page with the line stopped on the date and the quote after
 * it: nothing is sent.
 */
export function InsuranceQuoteSection({ loan }: { loan: LoanDetail }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const navigate = useNavigate();
  const today = todayIso();
  // An insurance has no id on the wire: its rank in the contract names it. Only one still due today can be replaced.
  const lines = loan.contract.insurances.map((ins, i) => ({ ins, i })).filter(({ ins }) => !ins.endsOn || ins.endsOn > today);
  const label = (i: number) => {
    const note = loan.contract.insurances[i]?.note;
    return note ? t('loan_insurance_quote.line_note', { n: i + 1, note }) : t('loan_insurance_quote.line', { n: i + 1 });
  };
  const form = useForm<Values>({
    resolver: zodResolver(schema(t)),
    defaultValues: { on: today, insurance: String(lines[0]?.i ?? 0), mode: 'rate_on_remaining', amount: '', rate: '' },
  });
  const errors = useSubmitErrors(form.setError, PATHS);
  const compare = $api.useMutation('post', INSURANCE_QUOTE);
  const [result, setResult] = useState<LoanInsuranceComparison | null>(null);
  const clear = () => { setResult(null); errors.clear(); };
  // eslint-disable-next-line react-hooks/exhaustive-deps -- only a new loan resets; `clear` is recreated every render
  useEffect(clear, [loan]);
  if (lines.length === 0) return null;

  const submit = form.handleSubmit((v) => {
    errors.clear();
    const fixed = v.mode === 'fixed_monthly';
    compare.mutate(
      {
        params: { path: { id: loan.id } },
        body: {
          on: v.on,
          quotes: [{ insurance: Number(v.insurance), mode: v.mode, ...(fixed ? { monthlyAmount: toWireAmount(v.amount) } : { annualRate: percentToFraction(v.rate)! }) }],
        },
      },
      { onSuccess: setResult, onError: (error) => errors.onError(error) },
    );
  });

  const e = form.formState.errors;
  const mode = form.watch('mode');
  const problem = errors.problem ? presentRefusal(t, compare.error) : null;
  const m = (amount: string) => money(loan.currency, amount);
  const dash = <span aria-hidden="true">{t('investments.dash')}</span>;

  return (
    <Section title={t('loan_insurance_quote.title')}>
      <p className="text-sm text-muted-foreground">{t('loan_insurance_quote.help')}</p>
      {problem ? <ProblemMessage problem={problem} /> : null}
      <form noValidate onSubmit={(ev) => void submit(ev)} className="grid items-start gap-3 sm:grid-cols-2 lg:grid-cols-4">
        <Controller control={form.control} name="on" render={({ field }) => (
          <Field label={t('loan_insurance_quote.on')} hint={t('loan_insurance_quote.on_help')} error={e.on?.message}>
            {(c) => <DateField {...c} value={field.value} onChange={(v) => { field.onChange(v); clear(); }} />}
          </Field>
        )} />
        {lines.length > 1 ? (
          <Controller control={form.control} name="insurance" render={({ field }) => (
            <Field label={t('loan_insurance_quote.insurance')} error={e.insurance?.message}>
              {(c) => <SelectField {...c} value={field.value} onValueChange={(v) => { field.onChange(v); clear(); }}
                options={lines.map(({ i }) => ({ value: String(i), label: label(i) }))} />}
            </Field>
          )} />
        ) : null}
        <Controller control={form.control} name="mode" render={({ field }) => (
          <Field label={t('loan_insurance_quote.mode')} error={e.mode?.message}>
            {(c) => <SelectField {...c} value={field.value} onValueChange={(v) => { field.onChange(v); clear(); }}
              options={INSURANCE_MODES.map((x) => ({ value: x, label: insuranceModeText(t, x) }))} />}
          </Field>
        )} />
        {mode === 'fixed_monthly' ? (
          <Field label={t('loan_insurance_quote.amount')} error={e.amount?.message}>
            {(c) => <Input inputMode="decimal" {...c} {...form.register('amount', { onChange: clear })} />}
          </Field>
        ) : (
          <Field label={t('loan_insurance_quote.rate')} error={e.rate?.message}>
            {(c) => <Input inputMode="decimal" {...c} {...form.register('rate', { onChange: clear })} />}
          </Field>
        )}
        <div><Button type="submit" disabled={compare.isPending}>{t('loan_insurance_quote.submit')}</Button></div>
      </form>
      {result ? (
        <div className="space-y-3">
          <Table aria-label={t('loan_insurance_quote.result')}>
            <TableHeader>
              <TableRow>
                <TableHead>{t('loan_insurance_quote.col_insured')}</TableHead>
                <TableHead className="text-right">{t('loan_insurance_quote.col_current')}</TableHead>
                <TableHead className="text-right">{t('loan_insurance_quote.col_quote')}</TableHead>
                <TableHead className="text-right">{t('loan_insurance_quote.col_saving')}</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {result.lines.map((l) => (
                <TableRow key={l.insurance}>
                  <TableHead scope="row">{label(l.insurance)}</TableHead>
                  <TableCell className={num}>{m(l.currentLeft)}</TableCell>
                  <TableCell className={num}>{l.quoteLeft ? m(l.quoteLeft) : dash}</TableCell>
                  <TableCell className={num}>{l.saving ? signedMoney(loan.currency, l.saving) : dash}</TableCell>
                </TableRow>
              ))}
            </TableBody>
            <TableFooter>
              <TableRow>
                <TableHead scope="row">{t('loan_insurance_quote.total')}</TableHead>
                <TableCell className={num}>{m(result.currentLeft)}</TableCell>
                <TableCell className={num}>{m(result.quotedLeft)}</TableCell>
                <TableCell className={num}>{signedMoney(loan.currency, result.saving)}</TableCell>
              </TableRow>
            </TableFooter>
          </Table>
          {loan.canWrite ? (
            <Button type="button" variant="outline"
              onClick={() => void navigate({ to: '/loans/$id/contract', params: { id: loan.id }, state: { loanContract: result.contract } })}>
              {t('loan_insurance_quote.record')}
            </Button>
          ) : null}
        </div>
      ) : null}
    </Section>
  );
}
