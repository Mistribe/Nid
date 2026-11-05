import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { present, toProblem } from '@/api/errors';
import { Section } from '@/components/page/Section';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { todayIso } from '../subscriptions/today';
import { useDismissExpected, useExpectedDocuments, type ExpectedDocument } from './api';
import { ItemUploadDialog } from './ItemUploadDialog';
import '@/locales/documents';

/** A yearly document is counted in the year of its date: the upload is dated in that year, today at the latest. */
const dateFor = (period: number, today: string) => (period === 0 ? '' : `${period}-12-31` < today ? `${period}-12-31` : today);

const keyOf = (e: ExpectedDocument) => `${e.targetKind}:${e.targetId}:${e.role}:${e.period}`;

/**
 * Issue #414: « Documents attendus », the documents the server's rules still ask for: « Déposer » on the right item with the type
 * (and the year) preset, « Pas disponible » to stop asking. On an item's tab (`itemId`), its own; on the documents page, every one,
 * each named by its item. Nothing when none is missing.
 */
export function ExpectedDocuments({ itemId }: { itemId?: string }) {
  const { t } = useTranslation();
  const answer = useExpectedDocuments(itemId);
  const dismiss = useDismissExpected();
  const [uploading, setUploading] = useState<ExpectedDocument | null>(null);
  const items = answer.data?.items ?? [];
  if (items.length === 0) return null;
  const what = (e: ExpectedDocument) => t(`documents_expected.subject.${e.subject}`, { year: e.period, defaultValue: e.role });
  return (
    <Section title={t('documents_expected.title')}>
      {dismiss.error ? <ProblemMessage problem={present(toProblem(dismiss.error))} /> : null}
      <ul className="divide-y rounded-md border text-sm">
        {items.map((e) => (
          <li key={keyOf(e)} className="flex flex-wrap items-center gap-2 px-3 py-2">
            <div className="min-w-0 flex-1">
              <div className="font-medium">{what(e)}</div>
              {itemId ? null : <div className="text-xs text-muted-foreground">{e.label}</div>}
            </div>
            <Button type="button" variant="outline" size="sm" aria-label={t('documents_expected.upload', { what: what(e) })} onClick={() => setUploading(e)}>
              {t('documents_item.upload')}
            </Button>
            <Button type="button" variant="ghost" size="sm" disabled={dismiss.isPending} aria-label={t('documents_expected.dismiss_label', { what: what(e) })}
              onClick={() => dismiss.mutate({ body: { targetKind: e.targetKind, targetId: e.targetId, role: e.role, period: e.period } },
                { onSuccess: () => toast.success(t('documents_expected.dismissed')) })}>
              {t('documents_expected.dismiss')}
            </Button>
          </li>
        ))}
      </ul>
      {uploading ? (
        <ItemUploadDialog itemId={uploading.targetId} targetKind={uploading.targetKind} defaultRole={uploading.role}
          defaultDate={dateFor(uploading.period, todayIso())} onClose={() => setUploading(null)}
          onDone={(message) => { setUploading(null); toast.success(message); }} />
      ) : null}
    </Section>
  );
}
