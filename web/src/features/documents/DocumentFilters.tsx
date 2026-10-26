import { useTranslation } from 'react-i18next';
import { Field } from '@/components/form/Field';
import { SelectField, type Option } from '@/components/form/SelectField';
import { SwitchField } from '@/components/page/SwitchField';
import type { DocumentSummary } from './api';
import { linkKey, roleText, shownDate } from './format';
import '@/locales/documents';

/** An empty value lets every document through. */
export type Filters = { role: string; year: string; link: string };
export const NO_FILTER: Filters = { role: '', year: '', link: '' };

/** Filtered in the browser on the list's answer (design §2.2): a document shows under each of its types and elements. */
export const applyFilters = (rows: DocumentSummary[], f: Filters) => rows.filter((d) =>
  (!f.role || d.links.some((l) => l.role === f.role))
  && (!f.year || shownDate(d).startsWith(f.year))
  && (!f.link || d.links.some((l) => linkKey(l) === f.link)));

/** The filters these rows still offer: one whose option went away is cleared. The same object when none did. */
export function keepOffered(f: Filters, rows: DocumentSummary[]): Filters {
  const links = rows.flatMap((d) => d.links);
  const next: Filters = {
    role: links.some((l) => l.role === f.role) ? f.role : '',
    year: rows.some((d) => shownDate(d).slice(0, 4) === f.year) ? f.year : '',
    link: links.some((l) => linkKey(l) === f.link) ? f.link : '',
  };
  return next.role === f.role && next.year === f.year && next.link === f.link ? f : next;
}

const byLabel = (a: Option, b: Option) => a.label.localeCompare(b.label, 'fr');

/** Type, year and linked element, offered from the documents listed; the archived ones on demand. */
export function DocumentFilters({ rows, filters, onChange, archived, onArchivedChange }: {
  rows: DocumentSummary[]; filters: Filters; onChange: (next: Filters) => void; archived: boolean; onArchivedChange: (on: boolean) => void;
}) {
  const { t } = useTranslation();
  const links = rows.flatMap((d) => d.links);
  const roles = [...new Set(links.map((l) => l.role))].map((role) => ({ value: role, label: roleText(t, role) })).sort(byLabel);
  const years = [...new Set(rows.map((d) => shownDate(d).slice(0, 4)))].sort().reverse().map((y) => ({ value: y, label: y }));
  const elements = [...new Map(links.map((l) => [linkKey(l), { value: linkKey(l), label: l.label }])).values()].sort(byLabel);
  const select = (key: keyof Filters, label: string, all: string, options: Option[]) => (
    <div className="w-full sm:w-52">
      <Field label={label}>
        {(control) => <SelectField {...control} value={filters[key]} onValueChange={(v) => onChange({ ...filters, [key]: v })}
          options={[{ value: '', label: all }, ...options]} />}
      </Field>
    </div>
  );
  return (
    <div className="flex flex-wrap items-end gap-3">
      {select('role', t('documents_page.filter_type'), t('documents_page.filter_type_all'), roles)}
      {select('year', t('documents_page.filter_year'), t('documents_page.filter_year_all'), years)}
      {select('link', t('documents_page.filter_link'), t('documents_page.filter_link_all'), elements)}
      <div className="pb-2"><SwitchField label={t('documents_page.show_archived')} checked={archived} onCheckedChange={onArchivedChange} /></div>
    </div>
  );
}
