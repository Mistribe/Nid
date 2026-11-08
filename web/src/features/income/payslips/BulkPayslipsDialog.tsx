import { useQueries, useQueryClient } from '@tanstack/react-query';
import { useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem, type Problem } from '@/api/errors';
import { Field } from '@/components/form/Field';
import { ComboboxField } from '@/components/form/ComboboxField';
import { ModalDialog } from '@/components/ModalDialog';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { invalidateDocuments, uploadDocument } from '../../documents/api';
import { refusal, UploadPicker, UploadZone } from '../../documents/DocumentUpload';
import { todayIso } from '@/lib/today';
import { EXPECTED, type IncomeSourceDetail } from '../api';
import { monthLabel } from '@/lib/format';
import { sinceWindows } from '../expected/months';
import { attachTarget, monthKeyOf, monthOfFileName, payslipTitle, type coverage, type MonthKey } from './payslip';
import '@/locales/documents';
import '@/locales/income';

// `month` is '' until one is chosen.
type Row = { key: number; file: File; month: MonthKey; guessed: MonthKey | null; skipped: boolean; state?: 'done' | Problem };

/**
 * Spec §6: drop a batch of payslips, check the month each file name gave (or choose one, or set the file aside), then send
 * them one at a time like « Joindre ». The documents reload at the end of sending (and on close), so the history shows what was sent.
 */
export function BulkPayslipsDialog({ source, payslips, onClose }: {
  source: IncomeSourceDetail; payslips: ReturnType<typeof coverage>; onClose: () => void;
}) {
  const { t } = useTranslation();
  const { $api, fetchClient } = useApi();
  const queryClient = useQueryClient();
  const today = todayIso();
  const start = source.phases.map((p) => p.startsOn).sort()[0] ?? today;
  const answers = useQueries({
    queries: sinceWindows(start, today).map((query) => $api.queryOptions('get', EXPECTED, { params: { path: { id: source.id }, query } })),
  });
  // Every window or none: a month missing from a window still loading would read « Aucun versement ce mois-là ».
  const ready = answers.every((a) => a.data);
  const failed = answers.find((a) => a.error)?.error;
  const paid = new Map((ready ? answers.flatMap((a) => a.data?.months ?? []) : []).filter((m) => m.occurrences.length > 0).map((m) => [monthKeyOf(m.year, m.month), m]));
  const options = [...paid.entries()].sort(([a], [b]) => b.localeCompare(a)).map(([value, m]) => ({ value, label: monthLabel(m.year, m.month) }));
  const [rows, setRows] = useState<Row[]>([]);
  const [sending, setSending] = useState(false);
  const next = useRef(0);

  const update = (key: number, change: Partial<Row>) => setRows((list) => list.map((r) => (r.key === key ? { ...r, ...change } : r)));
  const add = (files: File[]) => setRows((list) => [...list, ...files.map((file) => {
    const guessed = monthOfFileName(file.name);
    return { key: next.current++, file, guessed, month: guessed && paid.has(guessed) ? guessed : '', skipped: false };
  })]);
  const pending = rows.filter((r) => !r.skipped && r.state === undefined);
  const chosen = rows.filter((r) => !r.skipped).map((r) => r.month);
  const send = async () => {
    setSending(true);
    for (const row of pending) {
      const target = attachTarget(paid.get(row.month)?.occurrences ?? [], today);
      try {
        if (!target) throw new Error(`no payment in ${row.month}`); // never: the months offered end with this one
        await uploadDocument(fetchClient, row.file, {
          targetKind: 'income.phase', targetId: target.phaseId, role: 'payslip', date: target.date, title: payslipTitle(t, row.month),
        });
        update(row.key, { state: 'done' });
      } catch (error) {
        update(row.key, { state: toProblem(error) });
      }
    }
    await invalidateDocuments(queryClient); // spec §6.4: reload at the end of sending
    setSending(false);
  };
  const close = () => { void invalidateDocuments(queryClient); onClose(); }; // also when a file was added without sending

  return (
    <ModalDialog title={t('income_detail.payslips.bulk_title')} onClose={close}>
      {() => (
        <div className="space-y-3">
          {failed ? <ProblemMessage problem={present(toProblem(failed))} /> : null}
          {ready ? (
            <>
              <UploadZone onFiles={add} hint={t('income_detail.payslips.bulk_hint')} />
              <UploadPicker onFiles={add} />
            </>
          ) : null}
          {rows.length > 0 ? (
            <ul className="space-y-3">
              {rows.map((row) => {
                const already = row.month !== '' && (payslips.byMonth.has(row.month) || chosen.filter((m) => m === row.month).length > 1);
                const hint = row.skipped ? undefined
                  : already ? t('income_detail.payslips.bulk_already')
                    : row.month === '' && row.guessed ? t('income_detail.payslips.bulk_no_month') : undefined;
                const settled = row.state !== undefined;
                return (
                  <li key={row.key} className="flex items-end gap-2">
                    <div className="min-w-0 flex-1">
                      <Field label={t('income_detail.payslips.bulk_month', { name: row.file.name })} hint={hint}
                        error={typeof row.state === 'object' ? t(`documents_page.upload.${refusal(row.state)}`, { name: row.file.name }) : undefined}>
                        {(c) => <ComboboxField {...c} value={row.month} onValueChange={(month) => update(row.key, { month })} options={options}
                          placeholder={t('income_detail.payslips.bulk_none')} searchPlaceholder={t('form.search')} empty={t('form.no_result')}
                          disabled={row.skipped || settled || sending} />}
                      </Field>
                    </div>
                    {row.state === 'done' ? <span className="pb-2 text-status-good-text">{t('income_detail.payslips.bulk_done')}</span> : (
                      <Button type="button" variant="outline" aria-pressed={row.skipped} disabled={settled || sending}
                        onClick={() => update(row.key, { skipped: !row.skipped })}>{t('income_detail.payslips.bulk_skip')}</Button>
                    )}
                  </li>
                );
              })}
            </ul>
          ) : null}
          <div className="flex justify-end gap-2">
            <Button type="button" variant="outline" disabled={sending} onClick={close}>{t('common.close')}</Button>
            <Button type="button" disabled={!ready || sending || pending.length === 0 || pending.some((r) => r.month === '')}
              onClick={() => void send()}>{t('income_detail.payslips.bulk_send')}</Button>
          </div>
        </div>
      )}
    </ModalDialog>
  );
}
