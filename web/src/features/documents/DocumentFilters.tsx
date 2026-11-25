import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { SelectField, type Option } from '@/components/form/SelectField';
import { FilterField, FilterRow } from '@/components/page/FilterRow';
import { SwitchField } from '@/components/page/SwitchField';
import { Input } from '@/components/ui/input';
import type { DocumentFacets } from '@/api/documents';
import { roleText } from './format';
import '@/locales/documents';

/** The URL's filters (issue #420); an empty value lets every document through. */
export type Filters = { type: string; year: string; item: string; q: string };

/** The filters these facets still offer: one whose option went away is cleared. The same object when none did. */
export function keepOffered(f: Filters, facets: DocumentFacets): Filters {
  const next: Filters = {
    ...f,
    type: facets.roles.includes(f.type) ? f.type : '',
    year: facets.years.some((y) => String(y) === f.year) ? f.year : '',
    item: facets.links.some((l) => l.targetId === f.item) ? f.item : '',
  };
  return next.type === f.type && next.year === f.year && next.item === f.item ? f : next;
}

const byLabel = (a: Option, b: Option) => a.label.localeCompare(b.label, 'fr');

/** Title search, type, year and linked element, offered by the server over the whole list; the archived ones on demand. */
export function DocumentFilters({ facets, filters, onChange, archived, onArchivedChange }: {
  facets: DocumentFacets | undefined; filters: Filters; onChange: (next: Filters) => void; archived: boolean; onArchivedChange: (on: boolean) => void;
}) {
  const { t } = useTranslation();
  const roles = (facets?.roles ?? []).map((role) => ({ value: role, label: roleText(t, role) })).sort(byLabel);
  const years = (facets?.years ?? []).map((y) => ({ value: String(y), label: String(y) }));
  const elements = (facets?.links ?? []).map((l) => ({ value: l.targetId, label: l.label })).sort(byLabel);
  const select = (key: 'type' | 'year' | 'item', label: string, all: string, options: Option[]) => (
    <FilterField label={label} className="w-full sm:w-auto">
      {(labelId, controlId) => (
        <div className="min-w-0 flex-1 sm:w-52 sm:flex-none">
          <SelectField id={controlId} aria-labelledby={labelId} value={filters[key]} onValueChange={(v) => onChange({ ...filters, [key]: v })}
            options={[{ value: '', label: all }, ...options]} />
        </div>
      )}
    </FilterField>
  );
  return (
    <FilterRow label={t('common.filters')}>
      <FilterField label={t('documents_page.search')} labelFor className="w-full sm:w-auto">
        {(labelId, controlId) => <SearchInput id={controlId} labelledBy={labelId} value={filters.q} onSearch={(q) => onChange({ ...filters, q })} />}
      </FilterField>
      {select('type', t('documents_page.filter_type'), t('documents_page.filter_type_all'), roles)}
      {select('year', t('documents_page.filter_year'), t('documents_page.filter_year_all'), years)}
      {select('item', t('documents_page.filter_link'), t('documents_page.filter_link_all'), elements)}
      <SwitchField label={t('documents_page.show_archived')} checked={archived} onCheckedChange={onArchivedChange} />
    </FilterRow>
  );
}

/** #169: how long the typing pauses before the search goes: the server may ask a worker for its meaning, not on every key. */
const SEARCH_PAUSE_MS = 300;

/** The search field: what is typed shows at once, and goes to the URL (so to the server) after a pause in the typing. */
export function SearchInput({ id, labelledBy, value, onSearch }: { id: string; labelledBy: string; value: string; onSearch: (q: string) => void }) {
  const [text, setText] = useState(value);
  // The URL changed from elsewhere (a link, a filter dropped): the field follows it.
  const [sent, setSent] = useState(value);
  if (value !== sent) { setSent(value); setText(value); }
  useEffect(() => {
    if (text === value) return undefined;
    const timer = window.setTimeout(() => onSearch(text), SEARCH_PAUSE_MS);
    return () => window.clearTimeout(timer);
  }, [text, value, onSearch]);
  return <Input id={id} aria-labelledby={labelledBy} type="search" className="min-w-0 flex-1 sm:w-56 sm:flex-none" value={text} onChange={(e) => setText(e.target.value)} />;
}
