import { useEffect, useRef, useState } from 'react';
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
const DEBOUNCE = 250;
const ISO = /^\d{4}-\d{2}-\d{2}$/;

/**
 * A text control over a URL value: the text is typed locally (a half-typed date or number stays), `write` puts a whole value in the URL
 * (after `wait` ms of quiet) and answers its URL form, or null to write nothing. A URL value that did not come from `write` (back,
 * a link, a reset) replaces the text.
 */
function useUrlText(url: string, write: (text: string) => string | null, wait = 0) {
  const [text, setText] = useState(url);
  const written = useRef(url);
  const timer = useRef<ReturnType<typeof setTimeout>>(undefined);
  useEffect(() => { if (url !== written.current) { written.current = url; clearTimeout(timer.current); setText(url); } }, [url]);
  useEffect(() => () => clearTimeout(timer.current), []);
  const commit = (v: string) => { const w = write(v); if (w !== null) written.current = w; };
  const type = (v: string) => { setText(v); clearTimeout(timer.current); if (wait > 0) timer.current = setTimeout(() => commit(v), wait); else commit(v); };
  return [text, type] as const;
}

/** The filters of the « Opérations » page: the URL holds them, the server applies them and offers the options (facets). */
export function OperationFilters({ facets, search, onChange }: {
  facets: OperationPage['facets'] | undefined; search: OperationsSearch; onChange: (change: Partial<OperationsSearch>) => void;
}) {
  const { t } = useTranslation();
  // Half-typed dates and amounts stay in the text: only a whole one (or none) goes to the URL; the search and the amounts wait for a pause.
  const day = (key: 'from' | 'to') => (v: string) => (v === '' || ISO.test(v) ? (onChange({ [key]: v || undefined }), v) : null);
  const sum = (key: 'min' | 'max') => (v: string) => {
    const n = Number(v);
    if (v !== '' && !Number.isFinite(n)) return null;
    onChange({ [key]: v === '' ? undefined : n });
    return v === '' ? '' : String(n);
  };
  const [q, setQ] = useUrlText(search.q ?? '', (v) => (onChange({ q: v || undefined }), v), DEBOUNCE);
  const [from, setFrom] = useUrlText(search.from ?? '', day('from'));
  const [to, setTo] = useUrlText(search.to ?? '', day('to'));
  const [min, setMin] = useUrlText(search.min === undefined ? '' : String(search.min), sum('min'), DEBOUNCE);
  const [max, setMax] = useUrlText(search.max === undefined ? '' : String(search.max), sum('max'), DEBOUNCE);
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
  const date = (label: string, value: string, set: (v: string) => void) => (
    <FilterField label={label} className="w-full sm:w-auto">
      {(labelId, controlId) => (
        <div className="min-w-0 flex-1 sm:w-44 sm:flex-none">
          <DateField id={controlId} aria-labelledby={labelId} value={value} onChange={set} />
        </div>
      )}
    </FilterField>
  );
  const amount = (label: string, value: string, set: (v: string) => void) => (
    <FilterField label={label} labelFor className="w-full sm:w-auto">
      {(labelId, controlId) => (
        <Input id={controlId} aria-labelledby={labelId} type="number" step="any" className="min-w-0 flex-1 sm:w-28 sm:flex-none" value={value} onChange={(e) => set(e.target.value)} />
      )}
    </FilterField>
  );
  const category = search.uncategorised ? NONE : search.category?.[0] ?? '';
  const states: Option[] = [{ value: '', label: t('banks.operations.filter.state_all') }, ...STATES.map((s) => ({ value: s, label: t(`banks.operations.${s}`) }))];
  return (
    <FilterRow label={t('common.filters')}>
      <FilterField label={t('banks.operations.filter.search')} labelFor className="w-full sm:w-auto">
        {(labelId, controlId) => <Input id={controlId} aria-labelledby={labelId} type="search" className="min-w-0 flex-1 sm:w-56 sm:flex-none"
          value={q} onChange={(e) => setQ(e.target.value)} />}
      </FilterField>
      {select(t('banks.operations.filter.account'), search.account?.[0] ?? '', options(t('banks.operations.filter.account_all'), facets?.accounts), (v) => onChange({ account: v ? [v] : undefined }))}
      {select(t('banks.operations.filter.owner'), search.owner?.[0] ?? '', options(t('banks.operations.filter.owner_all'), facets?.owners), (v) => onChange({ owner: v ? [v] : undefined }))}
      {date(t('banks.operations.filter.from'), from, setFrom)}
      {date(t('banks.operations.filter.to'), to, setTo)}
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
      {amount(t('banks.operations.filter.min'), min, setMin)}
      {amount(t('banks.operations.filter.max'), max, setMax)}
    </FilterRow>
  );
}
