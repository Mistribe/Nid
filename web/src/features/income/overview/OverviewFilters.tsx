import { useNavigate, useSearch } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import type { IncomeSearch, RootSearch } from '@/app/router';
import { Field } from '@/components/form/Field';
import { Segmented } from '@/components/form/Segmented';
import { SelectField } from '@/components/form/SelectField';
import { PERSONS } from '@/features/subscriptions/api';
import { kindText } from '../format';
import { useOverview } from './useOverview';

/** Period, person and kind: each one in the URL (the defaults stay out of it), each reload keeps the others. */
export function OverviewFilters() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const search: RootSearch & IncomeSearch = useSearch({ strict: false });
  const navigate = useNavigate();
  const { data } = useOverview();
  const persons = $api.useQuery('get', PERSONS).data?.filter((p) => p.kind === 'adult') ?? [];

  const set = (patch: Partial<IncomeSearch>) =>
    void navigate({ from: '/income', to: '/income', replace: true, search: (prev: RootSearch & IncomeSearch) => ({ ...prev, source: undefined, ...patch }) });

  const kinds = [...new Set([...(data?.kinds ?? []), ...(search.kind ? [search.kind] : [])])];
  const kindLabel = (k: string) => (k === 'rent' ? t('income.overview.kind_rent') : kindText(t, k));

  return (
    <div className="flex flex-wrap items-end gap-4">
      <Segmented aria-label={t('income.overview.period_label')} value={search.period ?? 'year'}
        onValueChange={(v) => set({ period: v === 'year' ? undefined : (v as '12m' | '3y') })}
        options={[
          { value: '12m', label: t('income.overview.period_12m') },
          { value: 'year', label: t('income.overview.period_year') },
          { value: '3y', label: t('income.overview.period_3y') },
        ]} />
      <Segmented aria-label={t('income.overview.person_label')} value={search.person ?? ''}
        onValueChange={(v) => set({ person: v || undefined })}
        options={[{ value: '', label: t('income.overview.person_all') }, ...persons.map((p) => ({ value: p.id, label: p.displayName }))]} />
      <div className="w-48">
        <Field label={t('income.overview.kind_label')}>
          {(control) => (
            <SelectField {...control} value={search.kind ?? ''} onValueChange={(v) => set({ kind: v || undefined })}
              options={[{ value: '', label: t('income.overview.kind_all') }, ...kinds.map((k) => ({ value: k, label: kindLabel(k) }))]} />
          )}
        </Field>
      </div>
    </div>
  );
}
