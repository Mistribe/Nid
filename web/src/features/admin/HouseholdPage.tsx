import { useQueryClient } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem, type Presentation } from '@/api/errors';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Field } from '@/components/form/Field';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { AdminFrame, AdminSkeleton } from './AdminFrame';

const HOUSEHOLD = '/api/v1/admin/household';

/** screens.md §12, Foyer: the adult cap and the reference currency. */
export function HouseholdPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
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
        <Field label={t('admin.household.max_adults')} hint={t('admin.household.max_adults_hint', { count: current.adultCount })}>
          {(c) => <Input {...c} name="maxAdults" type="number" required step={1} min={Math.max(1, current.adultCount)} defaultValue={current.maxAdults} />}
        </Field>
        <Field label={t('admin.household.currency')} hint={t('admin.household.currency_warning')}>
          {(c) => <Input {...c} name="referenceCurrency" required maxLength={3} defaultValue={current.referenceCurrency} className="uppercase" />}
        </Field>
        <Button type="submit" disabled={save.isPending}>{t('admin.household.save')}</Button>
      </form>
    </AdminFrame>
  );
}
