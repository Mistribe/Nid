import { useQueryClient } from '@tanstack/react-query';
import { Archive } from 'lucide-react';
import type { Ref } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Reload } from '../../subscriptions/edits/Reload';
import { invalidateInvestments, REOPEN, type EnvelopeDetail } from '../api';
import { presentConflict } from '../entries/entries';

/**
 * 2B2b design: the note a closed envelope wears, with « Rouvrir » for whoever can write. The banner disappears once
 * the refetch shows the envelope open, so the success is announced by the page (`onReopened`), in a region that
 * outlives it; `onStart` lets the page empty that region first.
 */
export function ClosedBanner({ envelope, bannerRef, onStart, onReopened }: {
  envelope: EnvelopeDetail; bannerRef?: Ref<HTMLParagraphElement>; onStart: () => void; onReopened: (message: string) => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const reopen = $api.useMutation('post', REOPEN, { onSuccess: () => { void invalidateInvestments(queryClient); } });

  const submit = () => {
    onStart();
    reopen.mutate({ params: { path: { id: envelope.id } } }, { onSuccess: () => onReopened(t('envelope.closing.reopened')) });
  };
  const problem = reopen.error ? presentConflict(t, reopen.error) : null;

  return (
    <div className="space-y-2">
      <p ref={bannerRef} tabIndex={-1} role="note" className="flex flex-wrap items-center gap-2">
        <Archive aria-hidden="true" className="size-4" />
        {t('envelope.closing.banner')}
        {envelope.canWrite ? (
          <Button type="button" variant="outline" size="sm" disabled={reopen.isPending} onClick={submit}>{t('envelope.closing.reopen')}</Button>
        ) : null}
      </p>
      {problem ? (
        <>
          <ProblemMessage problem={problem} />
          {problem.kind === 'conflict' ? <Reload onReload={() => invalidateInvestments(queryClient)} /> : null}
        </>
      ) : null}
    </div>
  );
}
