import { Link, type LinkProps } from '@tanstack/react-router';
import { ArrowDownIcon, ArrowUpIcon, ChevronDownIcon, ChevronRightIcon } from 'lucide-react';
import { useMemo, useState, type MouseEvent, type ReactNode } from 'react';
import { Checkbox } from '@/components/ui/checkbox';
import { Table, TableBody, TableCaption, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { cn } from '@/lib/utils';

/** `wrap`: the header and cells may break onto several lines, for a long text column in a wide table. */
type Column<R> = { id: string; header: string; cell: (r: R) => ReactNode; sort?: (r: R) => string | number; align?: 'end'; wrap?: boolean };

/** A checkbox before each top-level row and « select all » in the header; the page owns the selected keys. */
export type Selection<R> = {
  selected: ReadonlySet<string>; onChange: (next: ReadonlySet<string>) => void; label: (r: R) => string; allLabel: string; disabled?: (r: R) => boolean;
};

/** A table from `md:` up, a list of `mobile(r)` rows below. Sorting is a view on the rows the server returned. */
export function SortableTable<R>({ caption, rows, rowKey, columns, rowLink, mobile, initialSort, subRows, expandLabel, rowClassName, selection }: {
  caption: string; rows: R[]; rowKey: (r: R) => string; columns: Column<R>[]; rowLink?: (r: R) => LinkProps;
  mobile: (r: R) => ReactNode; initialSort?: { id: string; desc: boolean }; rowClassName?: (r: R) => string | undefined; selection?: Selection<R>;
} & (
  | { subRows?: undefined; expandLabel?: undefined }
  /** Children of a row; a row with more than one gets a chevron and unfolds them, indented, under the same sort. */
  | { subRows: (r: R) => R[] | undefined; expandLabel: (r: R) => string }
)) {
  const [sort, setSort] = useState(initialSort ?? null);
  const [open, setOpen] = useState<ReadonlySet<string>>(new Set());
  const sortRows = useMemo(() => {
    const by = columns.find((c) => c.id === sort?.id)?.sort;
    if (!by || !sort) return (list: R[]) => list;
    const direction = sort.desc ? -1 : 1;
    // A missing key (undefined, NaN or the -Infinity of amountKey) sorts last whatever the direction.
    const missing = (v: unknown) => v == null || (typeof v === 'number' && (Number.isNaN(v) || v === Number.NEGATIVE_INFINITY));
    return (list: R[]) => [...list].sort((a, b) => {
      const x = by(a), y = by(b);
      if (missing(x) || missing(y)) return Number(missing(x)) - Number(missing(y));
      return direction * (typeof x === 'number' && typeof y === 'number' ? x - y : String(x).localeCompare(String(y), 'fr'));
    });
  }, [columns, sort]);
  const lines = useMemo(() => sortRows(rows).flatMap((r) => {
    const kids = subRows?.(r);
    const expandable = !!kids && kids.length > 1;
    const unfolded = expandable && open.has(rowKey(r));
    return [{ row: r, depth: 0, expandable, unfolded }, ...(unfolded ? sortRows(kids).map((k) => ({ row: k, depth: 1, expandable: false, unfolded: false })) : [])];
  }), [sortRows, rows, subRows, open, rowKey]);

  const flip = (key: string) => setOpen((o) => { const n = new Set(o); if (!n.delete(key)) n.add(key); return n; });
  // A click on another control of the row, or bubbling from a portal (menu, dialog), must not toggle it.
  const onRowClick = (key: string) => (e: MouseEvent<HTMLElement>) => {
    const target = e.target as HTMLElement;
    if (!e.currentTarget.contains(target) || target.closest('a,button,input,select,textarea,[role=menuitem],[role=dialog]')) return;
    flip(key);
  };
  // 24px hit area (WCAG 2.5.8), centred on the row.
  const chevron = (r: R, unfolded: boolean) => (
    <button type="button" aria-expanded={unfolded} aria-label={expandLabel?.(r)}
      onClick={() => flip(rowKey(r))}
      className="mr-1 inline-flex size-6 shrink-0 items-center justify-center rounded-sm align-middle focus-visible:ring-[3px] focus-visible:ring-ring/50 focus-visible:outline-none">
      {unfolded ? <ChevronDownIcon aria-hidden="true" className="size-4" /> : <ChevronRightIcon aria-hidden="true" className="size-4" />}
    </button>
  );

  // « Select all » covers the rows the page shows and may select; the rest of the selection is left as it is.
  const selectable = selection ? rows.filter((r) => !selection.disabled?.(r)).map(rowKey) : [];
  const chosen = selectable.filter((k) => selection?.selected.has(k)).length;
  const all = chosen === 0 ? false : chosen === selectable.length ? true : 'indeterminate';
  const selectAll = (on: boolean) => {
    if (!selection) return;
    const next = new Set(selection.selected);
    for (const k of selectable) if (on) next.add(k); else next.delete(k);
    selection.onChange(next);
  };
  const box = (r: R, depth: number) => {
    if (!selection || depth > 0) return null;
    const key = rowKey(r);
    return (
      <Checkbox aria-label={selection.label(r)} checked={selection.selected.has(key)} disabled={selection.disabled?.(r)} className="mt-0.5"
        onCheckedChange={(on) => { const next = new Set(selection.selected); if (on === true) next.add(key); else next.delete(key); selection.onChange(next); }} />
    );
  };

  const toggle = (id: string) => setSort((s) => (s?.id === id ? { id, desc: !s.desc } : { id, desc: false }));

  return (
    <>
      <Table className="hidden md:table">
        <TableCaption className="sr-only">{caption}</TableCaption>
        <TableHeader>
          <TableRow>
            {selection ? (
              <TableHead className="w-8">
                <Checkbox aria-label={selection.allLabel} checked={all} disabled={selectable.length === 0} onCheckedChange={(on) => selectAll(on === true)} />
              </TableHead>
            ) : null}
            {columns.map((c) => (
              <TableHead key={c.id} className={cn(c.align === 'end' && 'text-right', c.wrap && 'whitespace-normal')}
                aria-sort={c.sort ? (sort?.id === c.id ? (sort.desc ? 'descending' : 'ascending') : 'none') : undefined}>
                {c.sort ? (
                  <button type="button" onClick={() => toggle(c.id)}
                    className={cn('inline-flex items-center gap-1 rounded-sm hover:text-muted-foreground focus-visible:ring-[3px] focus-visible:ring-ring/50 focus-visible:outline-none', c.align === 'end' && 'flex-row-reverse')}>
                    {c.header}
                    {sort?.id === c.id ? (sort.desc ? <ArrowDownIcon aria-hidden="true" className="size-3.5" /> : <ArrowUpIcon aria-hidden="true" className="size-3.5" />) : null}
                  </button>
                ) : c.header}
              </TableHead>
            ))}
          </TableRow>
        </TableHeader>
        <TableBody>
          {lines.map(({ row: r, depth, expandable, unfolded }) => (
            <TableRow key={rowKey(r)} className={cn(rowLink && !expandable && 'relative', expandable && 'cursor-pointer', rowClassName?.(r))}
              onClick={expandable ? onRowClick(rowKey(r)) : undefined}>
              {selection ? <TableCell className="w-8">{box(r, depth)}</TableCell> : null}
              {columns.map((c, i) => (
                <TableCell key={c.id} className={cn(c.align === 'end' && 'text-right tabular-nums', c.wrap && 'whitespace-normal', i === 0 && depth > 0 && 'pl-8')}>
                  {i === 0 && expandable && chevron(r, unfolded)}
                  {rowLink && !expandable && i === 0
                    ? <Link {...rowLink(r)} className="after:absolute after:inset-0 after:content-[''] focus-visible:outline-none focus-visible:after:ring-[3px] focus-visible:after:ring-ring/50">{c.cell(r)}</Link>
                    : c.cell(r)}
                </TableCell>
              ))}
            </TableRow>
          ))}
        </TableBody>
      </Table>
      {selection ? (
        <label className="flex items-center gap-3 px-3 text-sm md:hidden">
          <Checkbox checked={all} disabled={selectable.length === 0} onCheckedChange={(on) => selectAll(on === true)} />
          {selection.allLabel}
        </label>
      ) : null}
      <ul aria-label={caption} className="divide-y rounded-lg border md:hidden">
        {lines.map(({ row: r, depth, expandable, unfolded }) => (
          <li key={rowKey(r)} className={cn(depth > 0 && 'pl-5', rowClassName?.(r))}>
            {expandable
              ? <div className="flex cursor-pointer items-center p-3" onClick={onRowClick(rowKey(r))}>{chevron(r, unfolded)}<div className="min-w-0 flex-1">{mobile(r)}</div></div>
              : selection && depth === 0 ? <div className="flex items-start gap-3 p-3">{box(r, depth)}<div className="min-w-0 flex-1">{mobile(r)}</div></div>
              : rowLink ? <Link {...rowLink(r)} className="block p-3">{mobile(r)}</Link> : <div className="p-3">{mobile(r)}</div>}
          </li>
        ))}
      </ul>
    </>
  );
}
