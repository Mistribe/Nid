import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { present, toProblem } from '@/api/errors';
import { Field } from '@/components/form/Field';
import { SelectField } from '@/components/form/SelectField';
import { FormDialog } from '@/components/FormDialog';
import { InfoTip } from '@/components/InfoTip';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { AMOUNT, POSITIVE_AMOUNT, toFieldAmount, toWireAmount } from '@/lib/amount';
import { todayIso } from '@/lib/today';
import { usePutNannyMonth, type NannyMonth } from './nannyApi';
import { monthTitle } from './statementText';
import '@/locales/childcare';

/** The months of `year` begun by today, newest first: the ones a month can be recorded for. */
export function monthsOf(year: number, today = todayIso()): string[] {
  const last = Number(today.slice(0, 4)) === year ? Number(today.slice(5, 7)) : Number(today.slice(0, 4)) > year ? 12 : 0;
  return Array.from({ length: last }, (_, i) => `${year}-${String(last - i).padStart(2, '0')}`);
}

/**
 * #94: a month as paid, the net salary and employer contributions, the CMG received and the hours if known. Recording a month
 * already recorded replaces it (the server keeps one per month); `month` given, the dialog corrects that one.
 */
export function NannyMonthDialog({ nannyId, year, month, onClose, onDone }: {
  nannyId: string; year: number; month?: NannyMonth; onClose: () => void; onDone: (message: string) => void;
}) {
  const { t } = useTranslation();
  const put = usePutNannyMonth();
  const options = monthsOf(year).map((m) => ({ value: m, label: monthTitle(m) }));
  const [values, setValues] = useState({
    month: month?.month ?? options[0]?.value ?? '',
    paid: month ? toFieldAmount(month.paid.amount) : '',
    cmg: month ? toFieldAmount(month.cmg.amount) : '',
    hours: month?.hours ? toFieldAmount(month.hours) : '',
  });
  const [errors, setErrors] = useState<{ month?: string; paid?: string; cmg?: string; hours?: string }>({});
  const set = (key: keyof typeof values) => (e: { target: { value: string } }) => setValues({ ...values, [key]: e.target.value });

  return (
    <FormDialog title={month ? t('childcare_nanny.month.edit_title', { month: monthTitle(month.month) }) : t('childcare_nanny.month.add_title')}
      onClose={onClose} onDone={onDone}>
      {({ titleId, done }) => (
        <form aria-labelledby={titleId} noValidate className="space-y-3" onSubmit={(e) => {
          e.preventDefault();
          e.stopPropagation();
          const next = {
            month: values.month ? undefined : t('childcare_nanny.month.month_required'),
            paid: POSITIVE_AMOUNT.test(values.paid.trim()) ? undefined : t('childcare_nanny.month.amount_format'),
            cmg: values.cmg.trim() === '' || AMOUNT.test(values.cmg.trim()) ? undefined : t('childcare_nanny.month.amount_format'),
            hours: values.hours.trim() === '' || POSITIVE_AMOUNT.test(values.hours.trim()) ? undefined : t('childcare_nanny.month.amount_format'),
          };
          setErrors(next);
          if (Object.values(next).some(Boolean)) return;
          put.mutate({
            params: { path: { id: nannyId, month: values.month } },
            body: {
              paid: toWireAmount(values.paid), cmg: values.cmg.trim() ? toWireAmount(values.cmg) : '0',
              hours: values.hours.trim() ? toWireAmount(values.hours) : null,
            },
          }, { onSuccess: () => done(t('childcare_nanny.month.saved', { month: monthTitle(values.month) })) });
        }}>
          {put.error ? <ProblemMessage problem={present(toProblem(put.error))} /> : null}
          {month ? null : (
            <Field label={t('childcare_nanny.month.month')} error={errors.month} hint={t('childcare_nanny.month.month_hint')}>
              {(c) => <SelectField {...c} value={values.month} onValueChange={(v) => setValues({ ...values, month: v })} options={options} />}
            </Field>
          )}
          <Field label={t('childcare_nanny.month.paid')} hint={t('childcare_nanny.month.paid_hint')} error={errors.paid}
            tip={<InfoTip label={t('childcare_nanny.month.paid')}>{t('childcare_nanny.month.paid_tip')}</InfoTip>}>
            {(c) => <Input inputMode="decimal" {...c} value={values.paid} onChange={set('paid')} />}
          </Field>
          <Field label={t('childcare_nanny.month.cmg')} hint={t('childcare_nanny.month.cmg_hint')} error={errors.cmg}
            tip={<InfoTip label={t('childcare_nanny.month.cmg')}>{t('childcare_nanny.month.cmg_tip')}</InfoTip>}>
            {(c) => <Input inputMode="decimal" {...c} value={values.cmg} onChange={set('cmg')} />}
          </Field>
          <Field label={t('childcare_nanny.month.hours')} hint={t('childcare_nanny.month.hours_hint')} error={errors.hours}>
            {(c) => <Input inputMode="decimal" {...c} value={values.hours} onChange={set('hours')} />}
          </Field>
          <div className="flex justify-end gap-2">
            <Button type="button" variant="outline" onClick={() => done()}>{t('childcare_nanny.cancel')}</Button>
            <Button type="submit" disabled={put.isPending}>{t('childcare_nanny.month.submit')}</Button>
          </div>
        </form>
      )}
    </FormDialog>
  );
}
