import { useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { RETIREMENT } from '@/api/retirement';
import { SERIES_POINTS } from '@/api/series';
import { ProblemMessage } from '@/components/ProblemMessage';
import { fieldErrorsOf, messageFor } from '@/components/form/fieldErrors';
import { PROJECTION, PROJECTION_ASSUMPTIONS, PROJECTION_PROFILES, type AssumptionsPatch } from './api';

/**
 * One settings card's save: a PATCH of its own fields, a toast once saved, and its refusals kept to the card. `problem` shows a
 * refusal the card places nowhere (`placed`: the paths, or path prefixes, its fields show).
 */
export function useAssumptionsPatch(placed: readonly string[]) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const mutation = $api.useMutation('patch', PROJECTION_ASSUMPTIONS, {
    // One card's save waits for another's: each PATCH answers the whole document, read after the previous one.
    scope: { id: PROJECTION_ASSUMPTIONS },
    onSuccess: () => {
      for (const key of [PROJECTION_ASSUMPTIONS, PROJECTION_PROFILES, PROJECTION, RETIREMENT, SERIES_POINTS])
        void queryClient.invalidateQueries({ queryKey: ['get', key] });
      toast.success(t('settings.projection.assumptions_saved'));
    },
  });
  const fields = fieldErrorsOf(mutation.error);
  const errorOf = (path: string) => { const f = fields.find((x) => x.path === path); return f ? messageFor(t, f) : undefined; };
  const isPlaced = (path: string) => placed.some((p) => path === p || path.startsWith(`${p}.`));
  const problem = mutation.error && (fields.length === 0 || !fields.every((f) => isPlaced(f.path)))
    ? <ProblemMessage problem={present(toProblem(mutation.error))} /> : null;
  const save = (body: AssumptionsPatch, onError?: () => void) => mutation.mutate({ body }, { onError });
  return { save, pending: mutation.isPending, errorOf, problem };
}
