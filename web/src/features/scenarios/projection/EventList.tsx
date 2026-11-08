import { lazy, Suspense, useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { useApi } from '@/api/ApiProvider';
import { useSession } from '@/app/Providers';
import { formatCurrency } from '@/components/Money';
import { RowMenu } from '@/components/page/RowMenu';
import { SortableTable } from '@/components/page/SortableTable';
import { Button } from '@/components/ui/button';
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger } from '@/components/ui/dropdown-menu';
import { PERSONS } from '@/api/persons';
import { ESTATE_KINDS, EVENT_KINDS, isEventKind, type EventDate, type EventKind, type ProjectionEvent } from './api';
import type { EventFieldError } from './EventDialog';
import { usePickOptions } from './pickOptions';
import type strings from '@/locales/fr.scenarios.json';
import '@/locales/scenarios';

// The dialog and its fields load when an event is added or modified.
const EventDialog = lazy(() => import('./EventDialog').then((m) => ({ default: m.EventDialog })));
// The confirmation of a removal (`ConfirmAction`'s dialog), loaded on the first « Retirer ».
const ConfirmDialog = lazy(() => import('@/components/ConfirmDialog'));

/** yyyy-mm-dd as jj/mm/aaaa (a string swap, nothing computed). */
const dayFirst = (iso: string) => iso.split('-').reverse().join('/');

type EventKey = keyof typeof strings.scenarios.projection.event;
type Split = 'highest_rate' | 'smallest_balance' | 'chosen';
type Row = { index: number; event: ProjectionEvent; kind: EventKind };
type Editing = { kind: EventKind; index?: number };

/** The fields an event's dialog labels; a refusal on anything else (`kind`, the event itself) reads as its message alone. */
const FIELD_KEYS = new Set<EventKey>([
  'label', 'start_on', 'until_on', 'repayment_on', 'sale_on', 'person', 'age', 'until_person', 'until_age', 'monthly_amount', 'amount', 'split',
  'loan', 'effect', 'penalty', 'property', 'price', 'costs', 'scenario',
]);

/** The label of the field a refusal names inside an event of this kind (`amount`, `start.age`). */
function fieldKey(kind: EventKind, path: string): EventKey | null {
  const [root, part] = path.split('.');
  if (root === 'start' || root === 'until') {
    if (part === 'age') return root === 'until' ? 'until_age' : 'age';
    if (part === 'person') return root === 'until' ? 'until_person' : 'person';
    if (root === 'until') return 'until_on';
    return kind === 'early_repayment' ? 'repayment_on' : kind === 'property_sale' ? 'sale_on' : 'start_on';
  }
  const key = root === 'monthlyAmount' ? 'monthly_amount' : root;
  return FIELD_KEYS.has(key as EventKey) ? (key as EventKey) : null;
}

/**
 * Spec §8: a scenario's events in a dense table, each one's `⋯` modifying it (its `EventDialog`) or removing it; « Ajouter un
 * événement » asks for the kind first; a removal is confirmed first. The page owns the events: `onAdd`, `onEdit` and `onRemove` hand
 * each change back; `errors` are the server's refusals by event, shown under it and on its fields once its dialog opens. `readOnly`
 * lists them without any action; `busy` disables the actions while the page saves the last change, and `silent` leaves announcing
 * each change to the page (once it is saved).
 */
export function EventList({ events, errors, currency, scope, readOnly, busy, silent, onAdd, onEdit, onRemove }: {
  events: ProjectionEvent[]; errors?: Record<number, EventFieldError[]>; currency: string; scope: 'me' | 'household'; readOnly?: boolean;
  busy?: boolean; silent?: boolean;
  onAdd: (event: ProjectionEvent) => void; onEdit: (index: number, event: ProjectionEvent) => void; onRemove: (index: number) => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { modules } = useSession().capabilities;
  const kinds = EVENT_KINDS.filter((k) => modules.includes('realestate') || !ESTATE_KINDS.includes(k));
  const [editing, setEditing] = useState<Editing | null>(null);
  const [removing, setRemoving] = useState<Row | null>(null);
  const addButton = useRef<HTMLButtonElement>(null);
  // A change hands the focus to « Ajouter un événement »; disabled while the page saves it, the button takes it back once saved.
  const refocus = useRef(false);
  useEffect(() => {
    if (busy || !refocus.current) return;
    refocus.current = false;
    addButton.current?.focus();
  }, [busy]);
  const toAdd = () => { refocus.current = true; addButton.current?.focus(); };

  // The names of the targets, each list read only when an event names one.
  const loans = usePickOptions(events.some((e) => e.loan) ? 'loans' : null, scope);
  const properties = usePickOptions(events.some((e) => e.property) ? 'properties' : null, scope);
  const purchases = usePickOptions(events.some((e) => e.scenario) ? 'purchases' : null, scope);
  const persons = $api.useQuery('get', PERSONS, {}, { enabled: events.some((e) => e.start?.person || e.until?.person) });
  const nameOf = (options: { value: string; label: string }[], id: string | null | undefined) => options.find((o) => o.value === id)?.label ?? '';

  const rows: Row[] = events.flatMap((event, index) => (isEventKind(event.kind) ? [{ index, event, kind: event.kind }] : []));
  const kindText = (r: Row) => t(`scenarios.projection.kind.${r.kind}`);
  const menuName = (r: Row) => (r.event.label ? `${kindText(r)} ${r.event.label}` : kindText(r));
  const dateText = (d: EventDate | null | undefined) => (d?.person
    ? t('scenarios.projection.events.at_age', { age: d.age, name: persons.data?.find((x) => x.id === d.person)?.displayName ?? '' })
    : d?.on ? dayFirst(d.on) : '');
  const when = (r: Row) => (r.kind === 'property_purchase' ? t('scenarios.projection.events.on_purchase')
    : r.event.until ? t('scenarios.projection.events.until', { start: dateText(r.event.start), end: dateText(r.event.until) }) : dateText(r.event.start));
  const what = (r: Row) => {
    switch (r.kind) {
      case 'income_change': case 'savings_change': return r.event.label ?? '';
      case 'early_repayment': return r.event.split === 'chosen' ? nameOf(loans, r.event.loan) : t(`scenarios.projection.event.split_${r.event.split as Split}`);
      case 'property_sale': return nameOf(properties, r.event.property);
      case 'property_purchase': return nameOf(purchases, r.event.scenario);
    }
  };
  const figure = (r: Row) => {
    const e = r.event;
    if (r.kind === 'income_change' || r.kind === 'savings_change') return e.monthlyAmount ? t('scenarios.projection.events.monthly', { amount: formatCurrency(e.monthlyAmount, currency) }) : '';
    if (r.kind === 'early_repayment') return e.amount ? formatCurrency(e.amount, currency) : '';
    if (r.kind === 'property_sale') return e.price ? formatCurrency(e.price, currency) : t('scenarios.projection.events.sale_projected');
    return t('scenarios.dash');
  };
  const refusals = (r: Row) => (errors?.[r.index] ?? []).map((f) => {
    const key = fieldKey(r.kind, f.path);
    return (
      <span key={f.path} className="block text-sm text-destructive">
        {key ? t('scenarios.projection.events.error_line', { field: t(`scenarios.projection.event.${key}`), message: f.message }) : f.message}
      </span>
    );
  });
  const menu = (r: Row) => (readOnly ? null : (
    <RowMenu label={t('scenarios.projection.events.row_menu', { name: menuName(r) })} items={[
      { label: t('scenarios.projection.events.edit'), onSelect: () => setEditing({ kind: r.kind, index: r.index }) },
      { label: t('scenarios.projection.events.remove'), onSelect: () => setRemoving(r), destructive: true },
    ]} />
  ));
  const announce = (text: string) => { if (!silent) toast.success(text); };
  const remove = (r: Row) => { setRemoving(null); onRemove(r.index); announce(t('scenarios.projection.events.removed')); toAdd(); };
  const done = (text: string) => { setEditing(null); announce(text); toAdd(); };
  const edited = editing?.index === undefined ? undefined : events[editing.index];

  // While the page saves the last change, every action of the list (each `⋯`, « Ajouter ») waits: a disabled fieldset disables them all.
  return (
    <fieldset disabled={busy} className="m-0 min-w-0 space-y-3 border-0 p-0">
      {rows.length === 0 ? <p className="text-sm text-muted-foreground">{t('scenarios.projection.events.empty')}</p> : (
        <SortableTable<Row> caption={t('scenarios.projection.events.title')} rows={rows} rowKey={(r) => String(r.index)}
          columns={[
            { id: 'kind', header: t('scenarios.projection.events.col_kind'), cell: (r) => <span className="font-medium">{kindText(r)}</span> },
            { id: 'what', header: t('scenarios.projection.events.col_what'), cell: (r) => <>{what(r)}{refusals(r)}</> },
            { id: 'date', header: t('scenarios.projection.events.col_date'), cell: when },
            { id: 'amount', header: t('scenarios.projection.events.col_amount'), align: 'end', cell: (r) => <span className="tabular-nums">{figure(r)}</span> },
            ...(readOnly ? [] : [{ id: 'actions', header: t('scenarios.projection.events.col_actions'), align: 'end' as const, cell: menu }]),
          ]}
          mobile={(r) => (
            <div className="space-y-1">
              <div className="flex items-center justify-between gap-2"><span className="font-medium">{kindText(r)}</span>{menu(r)}</div>
              <div className="flex flex-wrap justify-between gap-2 text-sm"><span>{what(r)}</span><span>{when(r)}</span><span>{figure(r)}</span></div>
              {refusals(r)}
            </div>
          )} />
      )}
      {readOnly ? null : <DropdownMenu>
        <DropdownMenuTrigger asChild><Button ref={addButton} type="button" variant="outline">{t('scenarios.projection.events.add')}</Button></DropdownMenuTrigger>
        <DropdownMenuContent align="start">
          {kinds.map((k) => <DropdownMenuItem key={k} onSelect={() => setEditing({ kind: k })}>{t(`scenarios.projection.kind.${k}`)}</DropdownMenuItem>)}
        </DropdownMenuContent>
      </DropdownMenu>}
      {editing ? (
        <Suspense fallback={null}><EventDialog kind={editing.kind} event={edited} errors={editing.index === undefined ? undefined : errors?.[editing.index]} scope={scope}
          onClose={() => setEditing(null)} onDone={done}
          onSave={(event) => (editing.index === undefined ? onAdd(event) : onEdit(editing.index, event))} /></Suspense>
      ) : null}
      {removing ? (
        <Suspense fallback={null}><ConfirmDialog label={t('scenarios.projection.events.remove_title')}
          confirmation={t('scenarios.projection.events.remove_confirm', { name: menuName(removing) })}
          onClose={() => setRemoving(null)} onConfirm={() => remove(removing)} /></Suspense>
      ) : null}
    </fieldset>
  );
}
