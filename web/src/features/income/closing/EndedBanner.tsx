import { useQueryClient } from '@tanstack/react-query';
import { Archive } from 'lucide-react';
import type { Ref } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { formatLocalDate } from '@/components/LocalDate';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { presentConflict } from '@/components/form/fieldErrors';
import { Reload } from '@/components/form/Reload';
import { invalidateIncome, REOPEN_SOURCE, type IncomeSourceDetail } from '@/api/income';
import '@/locales/income';

/**
 * Spec §6: the note an ended source wears, with « Rouvrir » for whoever can write. The banner disappears once the
 * refetch shows the source open, so the page announces the success (`onReopened`).
 */
export function EndedBanner({ source, bannerRef, onReopened }: {
  source: IncomeSourceDetail; bannerRef?: Ref<HTMLParagraphElement>; onReopened: (message: string) => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const reopen = $api.useMutation('post', REOPEN_SOURCE, { onSuccess: () => { void invalidateIncome(queryClient); } });

  const submit = () => {
    reopen.mutate({ params: { path: { id: source.id } } }, { onSuccess: () => onReopened(t('income_detail.ending.reopened')) });
  };
  const problem = reopen.error ? presentConflict(t, reopen.error) : null;

  return (
    <div className="space-y-2">
      <p ref={bannerRef} tabIndex={-1} role="note" className="flex flex-wrap items-center gap-2 rounded-md border p-3">
        <Archive aria-hidden="true" className="size-4" />
        <span className="font-medium">{t('income_detail.ended_banner')}</span>
        {source.endedOn ? <span>{t('income_detail.ended_on', { date: formatLocalDate(source.endedOn) })}</span> : null}
        {source.canWrite ? (
          <Button type="button" variant="outline" size="sm" disabled={reopen.isPending} onClick={submit}>{t('income_detail.ending.reopen')}</Button>
        ) : null}
      </p>
      {problem ? (
        <>
          <ProblemMessage problem={problem} />
          {problem.kind === 'conflict' ? <Reload onReload={() => invalidateIncome(queryClient)} /> : null}
        </>
      ) : null}
    </div>
  );
}
