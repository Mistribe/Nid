import { Link, type LinkProps } from '@tanstack/react-router';
import { ArrowDownIcon, ArrowUpIcon } from 'lucide-react';
import { useMemo, useState, type ReactNode } from 'react';
import { Table, TableBody, TableCaption, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { cn } from '@/lib/utils';

type Column<R> = { id: string; header: string; cell: (r: R) => ReactNode; sort?: (r: R) => string | number; align?: 'end' };

/** A table from `md:` up, a list of `mobile(r)` rows below. Sorting is a view on the rows the server returned. */
export function SortableTable<R>({ caption, rows, rowKey, columns, rowLink, mobile, initialSort }: {
  caption: string; rows: R[]; rowKey: (r: R) => string; columns: Column<R>[]; rowLink?: (r: R) => LinkProps;
  mobile: (r: R) => ReactNode; initialSort?: { id: string; desc: boolean };
}) {
  const [sort, setSort] = useState(initialSort ?? null);
  const sorted = useMemo(() => {
    const by = columns.find((c) => c.id === sort?.id)?.sort;
    if (!by || !sort) return rows;
    const direction = sort.desc ? -1 : 1;
    return [...rows].sort((a, b) => {
      const x = by(a), y = by(b);
      return direction * (typeof x === 'number' && typeof y === 'number' ? x - y : String(x).localeCompare(String(y), 'fr'));
    });
  }, [rows, columns, sort]);

  const toggle = (id: string) => setSort((s) => (s?.id === id ? { id, desc: !s.desc } : { id, desc: false }));

  return (
    <>
      <Table className="hidden md:table">
        <TableCaption className="sr-only">{caption}</TableCaption>
        <TableHeader>
          <TableRow>
            {columns.map((c) => (
              <TableHead key={c.id} className={cn(c.align === 'end' && 'text-right')}
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
          {sorted.map((r) => (
            <TableRow key={rowKey(r)} className={cn(rowLink && 'relative')}>
              {columns.map((c, i) => (
                <TableCell key={c.id} className={cn(c.align === 'end' && 'text-right tabular-nums')}>
                  {rowLink && i === 0
                    ? <Link {...rowLink(r)} className="after:absolute after:inset-0 after:content-[''] focus-visible:outline-none focus-visible:after:ring-[3px] focus-visible:after:ring-ring/50">{c.cell(r)}</Link>
                    : c.cell(r)}
                </TableCell>
              ))}
            </TableRow>
          ))}
        </TableBody>
      </Table>
      <ul aria-label={caption} className="divide-y rounded-lg border md:hidden">
        {sorted.map((r) => (
          <li key={rowKey(r)}>{rowLink ? <Link {...rowLink(r)} className="block p-3">{mobile(r)}</Link> : <div className="p-3">{mobile(r)}</div>}</li>
        ))}
      </ul>
    </>
  );
}
