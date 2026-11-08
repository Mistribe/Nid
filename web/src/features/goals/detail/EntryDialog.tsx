import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { present, toProblem } from '@/api/errors';
import { DateField } from '@/components/form/DateField';
import { Field } from '@/components/form/Field';
import { FormDialog } from '@/components/FormDialog';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { AMOUNT, ISO_DATE, toWireAmount } from '@/lib/amount';
import { todayIso } from '@/lib/today';
import { usePutProgress } from '@/api/goals';
import '@/locales/goals';

/** A dated reading of a free goal; a reading on a date already entered replaces it (the server keeps one per date). */
export function EntryDialog({ goalId, onClose, onDone }: { goalId: string; onClose: () => void; onDone: (message: string) => void }) {
  const { t } = useTranslation();
  const put = usePutProgress();
  const today = todayIso();
  const [on, setOn] = useState(today);
  const [amount, setAmount] = useState('');
  const [errors, setErrors] = useState<{ on?: string; amount?: string }>({});

  return (
    <FormDialog title={t('goals_detail.entries.add_title')} onClose={onClose} onDone={onDone}>
      {({ titleId, done }) => (
        <form aria-labelledby={titleId} noValidate className="space-y-3" onSubmit={(e) => {
          e.preventDefault();
          e.stopPropagation();
          const next = {
            on: !ISO_DATE.test(on) ? t('goals_detail.entries.date_required') : on > today ? t('goals_detail.entries.date_future') : undefined,
            amount: AMOUNT.test(amount.trim()) ? undefined : t('goals_detail.entries.amount_format'),
          };
          setErrors(next);
          if (next.on || next.amount) return;
          put.mutate({ params: { path: { id: goalId, on } }, body: { amount: toWireAmount(amount) } }, { onSuccess: () => done(t('goals_detail.entries.saved')) });
        }}>
          {put.error ? <ProblemMessage problem={present(toProblem(put.error))} /> : null}
          <Field label={t('goals_detail.entries.date')} hint={t('goals_detail.entries.hint')} error={errors.on}>
            {(c) => <DateField {...c} value={on} onChange={setOn} max={today} />}
          </Field>
          <Field label={t('goals_detail.entries.amount')} error={errors.amount}>
            {(c) => <Input inputMode="decimal" {...c} value={amount} onChange={(e) => setAmount(e.target.value)} />}
          </Field>
          <div className="flex justify-end gap-2">
            <Button type="button" variant="outline" onClick={() => done()}>{t('goals_detail.entries.cancel')}</Button>
            <Button type="submit" disabled={put.isPending}>{t('goals_detail.entries.submit')}</Button>
          </div>
        </form>
      )}
    </FormDialog>
  );
}
