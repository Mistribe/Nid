import { ChevronDownIcon, SlidersHorizontalIcon, SearchIcon, XIcon } from 'lucide-react';
import { useEffect, useId, useRef, useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import type { OperationsSearch } from '@/app/router';
import { ComboboxField } from '@/components/form/ComboboxField';
import { DateField } from '@/components/form/DateField';
import { Segmented } from '@/components/form/Segmented';
import { SelectField, type Option } from '@/components/form/SelectField';
import { formatLocalDate } from '@/components/LocalDate';
import { FilterField } from '@/components/page/FilterRow';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { DropdownMenu, DropdownMenuCheckboxItem, DropdownMenuContent, DropdownMenuTrigger } from '@/components/ui/dropdown-menu';
import { Input } from '@/components/ui/input';
import type { OperationPage } from '@/api/banking';
import '@/locales/banks';

// #437: « counted » is what a figure of Dépenses counts, on the Foyer / Moi accounts (OperationsPage sends the scope with it).
const STATES = ['unlinked', 'linked', 'internal', 'ignored', 'counted'] as const;
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

/**
 * The filters of the « Opérations » tab: the URL holds them, the server applies them and offers the options (facets). One line keeps the
 * search and the direction at hand; « Filtres » unfolds the rest, and each filter set shows as a chip that clears it.
 */
export function OperationFilters({ facets, search, onChange }: {
  facets: OperationPage['facets'] | undefined; search: OperationsSearch; onChange: (change: Partial<OperationsSearch>) => void;
}) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const panel = useId();
  // Half-typed dates and amounts stay in the text: only a whole one (or none) goes to the URL; the search and the amounts wait for a pause.
  const day = (key: 'from' | 'to') => (v: string) => (v === '' || ISO.test(v) ? (onChange({ [key]: v || undefined }), v) : null);
  const sum = (key: 'min' | 'max') => (v: string) => {
    // Plain non-negative decimals only: the URL and the server read no sign and no exponent.
    const n = Number(v);
    if (v !== '' && !/^\d{1,12}(\.\d{1,6})?$/.test(v)) return null;
    onChange({ [key]: v === '' ? undefined : n });
    return v === '' ? '' : String(n);
  };
  const [q, setQ] = useUrlText(search.q ?? '', (v) => (onChange({ q: v || undefined }), v), DEBOUNCE);
  const [from, setFrom] = useUrlText(search.from ?? '', day('from'));
  const [to, setTo] = useUrlText(search.to ?? '', day('to'));
  const [min, setMin] = useUrlText(search.min === undefined ? '' : String(search.min), sum('min'), DEBOUNCE);
  const [max, setMax] = useUrlText(search.max === undefined ? '' : String(search.max), sum('max'), DEBOUNCE);
  const options = (all: string, list: { id: string; name: string }[] | undefined): Option[] => [{ value: '', label: all }, ...(list ?? []).map((o) => ({ value: o.id, label: o.name }))];
  // Each control labelled above it, in the folded panel.
  const field = (label: string, control: (labelId: string, controlId: string) => ReactNode, labelFor = false) => (
    <FilterField label={label} labelFor={labelFor} className="flex min-w-0 flex-col items-stretch gap-1.5">{control}</FilterField>
  );
  // A select whose facet offers a single choice filters nothing: it shows only while it holds a value.
  const select = (label: string, value: string, opts: Option[], on: (v: string) => void) => (opts.length > 2 || value ? field(label, (labelId, controlId) => (
    <SelectField id={controlId} aria-labelledby={labelId} value={value} onValueChange={on} options={opts} />
  )) : null);
  const date = (label: string, value: string, set: (v: string) => void) => field(label, (labelId, controlId) => (
    <DateField id={controlId} aria-labelledby={labelId} value={value} onChange={set} />
  ), true);
  const amount = (label: string, value: string, set: (v: string) => void) => field(label, (labelId, controlId) => (
    <Input id={controlId} aria-labelledby={labelId} type="number" min="0" step="any" inputMode="decimal" value={value} onChange={(e) => set(e.target.value)} />
  ), true);
  const category = search.uncategorised ? NONE : search.category?.[0] ?? '';
  const states: Option[] = [{ value: '', label: t('banks.operations.filter.state_all') }, ...STATES.map((s) => ({ value: s, label: t(`banks.operations.${s}`) }))];
  const name = (list: { id: string; name: string }[] | undefined, id: string | undefined) => list?.find((o) => o.id === id)?.name;
  // Several accounts ticked in a menu, as on the overview; one ticked reads its name.
  const picked = search.account ?? [];
  const pickedText = picked.length === 1 ? name(facets?.accounts, picked[0]) ?? '…' : t('banks.operations.filter.account_picked', { count: picked.length });
  const toggle = (id: string) => { const next = picked.includes(id) ? picked.filter((p) => p !== id) : [...picked, id]; onChange({ account: next.length > 0 ? next : undefined }); };
  const accounts = (facets?.accounts.length ?? 0) > 1 || picked.length > 0 ? field(t('banks.operations.filter.account'), (labelId, controlId) => (
    <DropdownMenu modal={false}>
      <DropdownMenuTrigger asChild>
        <Button id={controlId} type="button" variant="outline" aria-labelledby={`${labelId} ${controlId}`} className="justify-between font-normal">
          {picked.length > 0 ? pickedText : t('banks.operations.filter.account_all')}
          <ChevronDownIcon aria-hidden="true" />
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="start">
        {(facets?.accounts ?? []).map((a) => (
          <DropdownMenuCheckboxItem key={a.id} checked={picked.includes(a.id)} onCheckedChange={() => toggle(a.id)} onSelect={(e) => e.preventDefault()}>{a.name}</DropdownMenuCheckboxItem>
        ))}
      </DropdownMenuContent>
    </DropdownMenu>
  )) : null;

  // The filters set, as chips: what each says, and what clears it.
  const chip = (on: unknown, label: string | undefined, clear: Partial<OperationsSearch>) => (on ? [{ label: label ?? '', clear }] : []);
  const chips = [
    ...chip(picked.length, `${t('banks.operations.filter.account')} : ${pickedText}`, { account: undefined }),
    ...chip(search.owner?.length, `${t('banks.operations.filter.owner')} : ${name(facets?.owners, search.owner?.[0]) ?? '…'}`, { owner: undefined }),
    ...chip(search.uncategorised, t('banks.operations.filter.category_none'), { uncategorised: undefined }),
    ...chip(search.category?.length, `${t('banks.operations.filter.category')} : ${name(facets?.categories, search.category?.[0]) ?? '…'}`, { category: undefined }),
    ...chip(search.state, search.state ? t(`banks.operations.${search.state}`) : undefined, { state: undefined }),
    ...chip(search.from, search.from ? t('banks.operations.filter.chip_from', { date: formatLocalDate(search.from, 'short') }) : undefined, { from: undefined }),
    ...chip(search.to, search.to ? t('banks.operations.filter.chip_to', { date: formatLocalDate(search.to, 'short') }) : undefined, { to: undefined }),
    ...chip(search.min !== undefined, t('banks.operations.filter.chip_min', { amount: search.min }), { min: undefined }),
    ...chip(search.max !== undefined, t('banks.operations.filter.chip_max', { amount: search.max }), { max: undefined }),
  ];
  const clearAll = Object.assign({}, ...chips.map((c) => c.clear)) as Partial<OperationsSearch>;

  return (
    <div role="group" aria-label={t('common.filters')} className="space-y-3">
      <div className="flex flex-wrap items-center gap-2">
        <div className="relative min-w-48 flex-1 sm:max-w-sm">
          <SearchIcon aria-hidden="true" className="pointer-events-none absolute top-1/2 left-3 size-4 -translate-y-1/2 text-muted-foreground" />
          <Input type="search" aria-label={t('banks.operations.filter.search')} placeholder={t('banks.operations.filter.search_placeholder')} className="pl-9"
            value={q} onChange={(e) => setQ(e.target.value)} />
        </div>
        <Segmented aria-label={t('banks.operations.filter.direction')} value={search.direction ?? ''} onValueChange={(v) => onChange({ direction: (v || undefined) as OperationsSearch['direction'] })}
          options={[{ value: '', label: t('banks.operations.filter.direction_all') }, { value: 'out', label: t('banks.operations.filter.direction_out') }, { value: 'in', label: t('banks.operations.filter.direction_in') }]} />
        <Button type="button" variant={open ? 'secondary' : 'outline'} aria-expanded={open} aria-controls={panel} onClick={() => setOpen(!open)}>
          <SlidersHorizontalIcon aria-hidden="true" />
          {t('common.filters')}
          {chips.length > 0 ? <Badge variant="secondary" className="tabular-nums">{chips.length}</Badge> : null}
        </Button>
      </div>
      {open ? (
        <div id={panel} className="grid gap-4 rounded-xl border bg-fill/40 p-4 sm:grid-cols-2 lg:grid-cols-3">
          {accounts}
          {select(t('banks.operations.filter.owner'), search.owner?.[0] ?? '', options(t('banks.operations.filter.owner_all'), facets?.owners), (v) => onChange({ owner: v ? [v] : undefined }))}
          {field(t('banks.operations.filter.category'), (labelId, controlId) => (
            <ComboboxField id={controlId} aria-labelledby={labelId} value={category} searchPlaceholder={t('banks.operations.filter.category_search')} empty={t('form.no_result')}
              options={[{ value: '', label: t('banks.operations.filter.category_all') }, { value: NONE, label: t('banks.operations.filter.category_none') }, ...options('', facets?.categories).slice(1)]}
              onValueChange={(v) => onChange({ category: v && v !== NONE ? [v] : undefined, uncategorised: v === NONE ? true : undefined })} />
          ))}
          {field(t('banks.operations.filter.state'), (labelId, controlId) => (
            <SelectField id={controlId} aria-labelledby={labelId} value={search.state ?? ''} onValueChange={(v) => onChange({ state: (v || undefined) as OperationsSearch['state'] })} options={states} />
          ))}
          <div className="grid grid-cols-2 gap-2">{date(t('banks.operations.filter.from'), from, setFrom)}{date(t('banks.operations.filter.to'), to, setTo)}</div>
          <div className="grid grid-cols-2 gap-2">{amount(t('banks.operations.filter.min'), min, setMin)}{amount(t('banks.operations.filter.max'), max, setMax)}</div>
        </div>
      ) : chips.length > 0 ? (
        <ul aria-label={t('banks.operations.filter.active')} className="flex flex-wrap items-center gap-2">
          {chips.map((c) => (
            <li key={c.label}>
              <span className="inline-flex items-center gap-1 rounded-full bg-fill py-0.5 pr-1 pl-3 text-sm">
                {c.label}
                <button type="button" aria-label={t('banks.operations.filter.remove', { label: c.label })} onClick={() => onChange(c.clear)}
                  className="inline-flex size-6 items-center justify-center rounded-full hover:bg-background focus-visible:ring-[3px] focus-visible:ring-ring/50 focus-visible:outline-none">
                  <XIcon aria-hidden="true" className="size-3.5" />
                </button>
              </span>
            </li>
          ))}
          {chips.length > 1 ? (
            <li><Button type="button" variant="link" size="sm" onClick={() => onChange(clearAll)}>{t('banks.operations.filter.clear')}</Button></li>
          ) : null}
        </ul>
      ) : null}
    </div>
  );
}
