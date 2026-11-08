import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { present, toProblem } from '@/api/errors';
import { DateField } from '@/components/form/DateField';
import { Field } from '@/components/form/Field';
import { FormDialog } from '@/components/FormDialog';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { fieldErrorsOf } from '@/components/form/fieldErrors';
import { ISO_DATE } from '@/lib/amount';
import { todayIso } from '@/lib/today';
import { useSnooze } from './api';
import '@/locales/notifications';

const pad = (n: number) => String(n).padStart(2, '0');

/** `days` after the ISO date, as an ISO date: only a bound for the calendar, the server stays the authority. */
function shifted(iso: string, days: number): string {
  const [y, m, d] = iso.split('-').map(Number) as [number, number, number];
  const at = new Date(y, m - 1, d + days);
  return `${at.getFullYear()}-${pad(at.getMonth() + 1)}-${pad(at.getDate())}`;
}

/** « Jusqu'au… »: a date in (today, today + 365]; the server's refusal reads under the field. */
export function SnoozeDialog({ id, onClose, onDone }: { id: string; onClose: () => void; onDone: (message: string) => void }) {
  const { t } = useTranslation();
  const snooze = useSnooze();
  const today = todayIso();
  const [until, setUntil] = useState('');
  const [error, setError] = useState<string>();
  const problem = snooze.error ? toProblem(snooze.error) : null;
  const refused = fieldErrorsOf(snooze.error).find((f) => f.path === 'until');
  const shown = error ?? (refused ? (refused.code === 'notifications.invalid_snooze' ? t('notifications_page.snooze_dialog.invalid_snooze') : refused.message) : undefined);

  return (
    <FormDialog title={t('notifications_page.snooze_dialog.title')} onClose={onClose} onDone={onDone}>
      {({ titleId, done }) => (
        <form aria-labelledby={titleId} noValidate className="space-y-3" onSubmit={(e) => {
          e.preventDefault();
          e.stopPropagation();
          if (!ISO_DATE.test(until)) { setError(t('notifications_page.snooze_dialog.required')); return; }
          setError(undefined);
          snooze.reset();
          snooze.mutate({ params: { path: { id } }, body: { until } }, { onSuccess: () => done(t('notifications_page.snoozed_done')) });
        }}>
          {problem && !refused ? <ProblemMessage problem={present(problem)} /> : null}
          <Field label={t('notifications_page.snooze_dialog.date')} hint={t('notifications_page.snooze_dialog.hint')} error={shown}>
            {(c) => <DateField {...c} value={until} onChange={setUntil} min={shifted(today, 1)} max={shifted(today, 365)} />}
          </Field>
          <div className="flex justify-end gap-2">
            <Button type="button" variant="outline" onClick={() => done()}>{t('notifications_page.snooze_dialog.cancel')}</Button>
            <Button type="submit" disabled={snooze.isPending}>{t('notifications_page.snooze_dialog.submit')}</Button>
          </div>
        </form>
      )}
    </FormDialog>
  );
}
