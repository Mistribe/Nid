import { useQueryClient } from '@tanstack/react-query';
import { useId, useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem, type Presentation } from '@/api/errors';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { AdminFrame, AdminSkeleton } from './AdminFrame';

const HOUSEHOLD = '/api/v1/admin/household';

/** screens.md §12, Foyer: the adult cap and the reference currency. */
export function HouseholdPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const hintId = useId();
  const warningId = useId();
  const [problem, setProblem] = useState<Presentation | null>(null);
  const [saved, setSaved] = useState(false);
  const household = $api.useQuery('get', HOUSEHOLD);
  const save = $api.useMutation('patch', HOUSEHOLD, {
    onSuccess: () => {
      setProblem(null);
      setSaved(true);
      void queryClient.invalidateQueries({ queryKey: ['get', HOUSEHOLD] });
    },
    onError: (error) => {
      setSaved(false);
      setProblem(present(toProblem(error)));
    },
  });

  const title = t('admin.household.title');
  if (household.error) return <AdminFrame title={title}><ProblemMessage problem={present(toProblem(household.error))} /></AdminFrame>;
  if (!household.data) return <AdminFrame title={title}><AdminSkeleton /></AdminFrame>;
  const current = household.data;

  const submit = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    const fields = new FormData(event.currentTarget);
    const maxAdults = Number(fields.get('maxAdults'));
    const referenceCurrency = ((fields.get('referenceCurrency') as string | null) ?? '').trim().toUpperCase();
    const body: { maxAdults?: number; referenceCurrency?: string } = {};
    if (maxAdults !== current.maxAdults) body.maxAdults = maxAdults;
    if (referenceCurrency !== current.referenceCurrency) body.referenceCurrency = referenceCurrency;
    if (Object.keys(body).length === 0) return;
    setSaved(false);
    save.mutate({ body });
  };

  return (
    <AdminFrame title={title}>
      {problem ? <ProblemMessage problem={problem} /> : null}
      {saved ? <p role="status">{t('admin.household.saved')}</p> : null}
      <form onSubmit={submit} className="max-w-md space-y-4">
        <div className="space-y-1">
          <label className="block text-sm" htmlFor="maxAdults">{t('admin.household.max_adults')}</label>
          <input
            id="maxAdults" name="maxAdults" type="number" required step={1} min={Math.max(1, current.adultCount)}
            defaultValue={current.maxAdults} aria-describedby={hintId} className="w-full rounded-md border px-3 py-2"
          />
          <span id={hintId} className="block text-sm text-muted-foreground">{t('admin.household.max_adults_hint', { count: current.adultCount })}</span>
        </div>
        <div className="space-y-1">
          <label className="block text-sm" htmlFor="referenceCurrency">{t('admin.household.currency')}</label>
          <input
            id="referenceCurrency" name="referenceCurrency" required maxLength={3} defaultValue={current.referenceCurrency}
            aria-describedby={warningId} className="w-full rounded-md border px-3 py-2 uppercase"
          />
          <span id={warningId} className="block text-sm text-muted-foreground">{t('admin.household.currency_warning')}</span>
        </div>
        <Button type="submit" disabled={save.isPending}>{t('admin.household.save')}</Button>
      </form>
    </AdminFrame>
  );
}
