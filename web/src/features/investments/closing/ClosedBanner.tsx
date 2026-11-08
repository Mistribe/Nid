import { useQueryClient } from '@tanstack/react-query';
import { Archive } from 'lucide-react';
import type { Ref } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Reload } from '@/components/form/Reload';
import { invalidateInvestments, REOPEN, type EnvelopeDetail } from '@/api/investments';
import { presentConflict } from '@/components/form/fieldErrors';
import '@/locales/investments';

/**
 * 2B2b design: the note a closed envelope wears, with « Rouvrir » for whoever can write. The banner disappears once
 * the refetch shows the envelope open, so the page toasts the success (`onReopened`).
 */
export function ClosedBanner({ envelope, bannerRef, onReopened }: {
  envelope: EnvelopeDetail; bannerRef?: Ref<HTMLParagraphElement>; onReopened: (message: string) => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const reopen = $api.useMutation('post', REOPEN, { onSuccess: () => { void invalidateInvestments(queryClient); } });

  const submit = () => {
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
