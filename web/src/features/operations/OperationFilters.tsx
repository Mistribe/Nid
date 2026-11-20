import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { OperationsSearch } from '@/app/router';
import { ComboboxField } from '@/components/form/ComboboxField';
import { DateField } from '@/components/form/DateField';
import { Segmented } from '@/components/form/Segmented';
import { SelectField, type Option } from '@/components/form/SelectField';
import { FilterField, FilterRow } from '@/components/page/FilterRow';
import { Input } from '@/components/ui/input';
import type { OperationPage } from '@/api/banking';
import '@/locales/banks';

const STATES = ['unlinked', 'linked', 'internal', 'ignored'] as const;
const NONE = 'none';
const ISO = /^\d{4}-\d{2}-\d{2}$/;

/** The filters of the « Opérations » page: the URL holds them, the server applies them and offers the options (facets). */
export function OperationFilters({ facets, search, onChange }: {
  facets: OperationPage['facets'] | undefined; search: OperationsSearch; onChange: (change: Partial<OperationsSearch>) => void;
}) {
  const { t } = useTranslation();
  // Half-typed dates and amounts stay here: only a whole one (or none) goes to the URL.
  const [from, setFrom] = useState(search.from ?? '');
  const [to, setTo] = useState(search.to ?? '');
  const [min, setMin] = useState(search.min === undefined ? '' : String(search.min));
  const [max, setMax] = useState(search.max === undefined ? '' : String(search.max));
  const options = (all: string, list: { id: string; name: string }[] | undefined): Option[] => [{ value: '', label: all }, ...(list ?? []).map((o) => ({ value: o.id, label: o.name }))];
  const select = (label: string, value: string, opts: Option[], on: (v: string) => void) => (
    <FilterField label={label} className="w-full sm:w-auto">
      {(labelId, controlId) => (
        <div className="min-w-0 flex-1 sm:w-52 sm:flex-none">
          <SelectField id={controlId} aria-labelledby={labelId} value={value} onValueChange={on} options={opts} />
        </div>
      )}
    </FilterField>
  );
  const date = (label: string, value: string, set: (v: string) => void, key: 'from' | 'to') => (
    <FilterField label={label} className="w-full sm:w-auto">
      {(labelId, controlId) => (
        <div className="min-w-0 flex-1 sm:w-44 sm:flex-none">
          <DateField id={controlId} aria-labelledby={labelId} value={value}
            onChange={(v) => { set(v); if (v === '' || ISO.test(v)) onChange({ [key]: v || undefined }); }} />
        </div>
      )}
    </FilterField>
  );
  const amount = (label: string, value: string, set: (v: string) => void, key: 'min' | 'max') => (
    <FilterField label={label} labelFor className="w-full sm:w-auto">
      {(labelId, controlId) => (
        <Input id={controlId} aria-labelledby={labelId} type="number" step="any" className="min-w-0 flex-1 sm:w-28 sm:flex-none" value={value}
          onChange={(e) => { set(e.target.value); const n = Number(e.target.value); if (e.target.value === '') onChange({ [key]: undefined }); else if (Number.isFinite(n)) onChange({ [key]: n }); }} />
      )}
    </FilterField>
  );
  const category = search.uncategorised ? NONE : search.category?.[0] ?? '';
  const states: Option[] = [{ value: '', label: t('banks.operations.filter.state_all') }, ...STATES.map((s) => ({ value: s, label: t(`banks.operations.${s}`) }))];
  return (
    <FilterRow label={t('common.filters')}>
      <FilterField label={t('banks.operations.filter.search')} labelFor className="w-full sm:w-auto">
        {(labelId, controlId) => <Input id={controlId} aria-labelledby={labelId} type="search" className="min-w-0 flex-1 sm:w-56 sm:flex-none"
          value={search.q ?? ''} onChange={(e) => onChange({ q: e.target.value || undefined })} />}
      </FilterField>
      {select(t('banks.operations.filter.account'), search.account?.[0] ?? '', options(t('banks.operations.filter.account_all'), facets?.accounts), (v) => onChange({ account: v ? [v] : undefined }))}
      {select(t('banks.operations.filter.owner'), search.owner?.[0] ?? '', options(t('banks.operations.filter.owner_all'), facets?.owners), (v) => onChange({ owner: v ? [v] : undefined }))}
      {date(t('banks.operations.filter.from'), from, setFrom, 'from')}
      {date(t('banks.operations.filter.to'), to, setTo, 'to')}
      <FilterField label={t('banks.operations.filter.category')} className="w-full sm:w-auto">
        {(labelId, controlId) => (
          <div className="min-w-0 flex-1 sm:w-52 sm:flex-none">
            <ComboboxField id={controlId} aria-labelledby={labelId} value={category} searchPlaceholder={t('banks.operations.filter.category_search')} empty={t('form.no_result')}
              options={[{ value: '', label: t('banks.operations.filter.category_all') }, { value: NONE, label: t('banks.operations.filter.category_none') }, ...options('', facets?.categories).slice(1)]}
              onValueChange={(v) => onChange({ category: v && v !== NONE ? [v] : undefined, uncategorised: v === NONE ? true : undefined })} />
          </div>
        )}
      </FilterField>
      {select(t('banks.operations.filter.state'), search.state ?? '', states, (v) => onChange({ state: (v || undefined) as OperationsSearch['state'] }))}
      <FilterField label={t('banks.operations.filter.direction')}>
        {(labelId) => (
          <Segmented aria-labelledby={labelId} value={search.direction ?? ''} onValueChange={(v) => onChange({ direction: (v || undefined) as OperationsSearch['direction'] })}
            options={[{ value: '', label: t('banks.operations.filter.direction_all') }, { value: 'out', label: t('banks.operations.filter.direction_out') }, { value: 'in', label: t('banks.operations.filter.direction_in') }]} />
        )}
      </FilterField>
      {amount(t('banks.operations.filter.min'), min, setMin, 'min')}
      {amount(t('banks.operations.filter.max'), max, setMax, 'max')}
    </FilterRow>
  );
}
