import { useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { useApi } from '@/api/ApiProvider';
import { CATEGORY_SUGGESTIONS, presentBankError, SUGGESTION_CALIBRATIONS, SUGGESTIONS_REFRESH } from '@/api/banking';
import { InfoTip } from '@/components/InfoTip';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import '@/locales/banks';

/**
 * Dépenses › Catégories, « Suggérer à nouveau » (#450): forgets every category the AI model guessed, so it is asked again at its next
 * pass. Only while a worker's model is calibrated: otherwise no guess is ever kept.
 */
export function SuggestAgain() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const setup = $api.useQuery('get', SUGGESTION_CALIBRATIONS);
  const refresh = $api.useMutation('post', SUGGESTIONS_REFRESH, {
    onSuccess: () => {
      toast.success(t('banks.suggestions.again_done'));
      void queryClient.invalidateQueries({ predicate: (q) => typeof q.queryKey[1] === 'string' && q.queryKey[1].startsWith(CATEGORY_SUGGESTIONS) });
    },
  });
  if (!setup.data?.profile || setup.data.implementation === 'nearest-neighbours') return null;
  return (
    <div className="space-y-2">
      <div className="flex flex-wrap items-center gap-2">
        <Button type="button" variant="outline" size="sm" disabled={refresh.isPending} onClick={() => refresh.mutate({})}>
          {t('banks.suggestions.again')}
        </Button>
        <InfoTip label={t('banks.suggestions.again')}>{t('banks.suggestions.again_tip')}</InfoTip>
      </div>
      {refresh.error ? <ProblemMessage problem={presentBankError(t, refresh.error)} /> : null}
    </div>
  );
}
