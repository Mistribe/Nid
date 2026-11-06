import type { ReactNode } from 'react';
import { Card, CardContent, CardHeader } from '@/components/ui/card';
import { Skeleton } from '@/components/ui/skeleton';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { cn } from '@/lib/utils';

/*
 * The loading state of a screen, built from its page parts (design-system.md §6.1): each part has a skeleton of the same
 * footprint, and a screen stacks them in the order of its loaded page, so nothing jumps when the data arrives.
 */

const repeat = (n: number) => Array.from({ length: n }, (_, i) => i);

/** The root of a loading state: one status region named after what loads (`…loading`); the parts inside are decoration. */
export function Loading({ label, className, children }: { label: string; className?: string; children: ReactNode }) {
  return <div role="status" aria-busy="true" aria-label={label} className={cn('space-y-6', className)}>{children}</div>;
}

/** `PageHeader`: the title and the footprint of its action buttons. */
export function PageHeaderSkeleton({ actions = 1 }: { actions?: number }) {
  return (
    <div className="flex flex-wrap items-center justify-between gap-x-4 gap-y-2 sm:flex-nowrap sm:items-start">
      <Skeleton className="h-9 w-64 max-w-full" />
      {actions ? <div className="flex gap-2">{repeat(actions).map((i) => <Skeleton key={i} className="h-9 w-32" />)}</div> : null}
    </div>
  );
}

/** `StatLine`: `items` label and figure pairs, in its grid. */
export function StatLineSkeleton({ items = 4 }: { items?: number }) {
  return (
    <div className="grid grid-cols-2 gap-x-6 gap-y-3 md:grid-cols-[repeat(auto-fill,minmax(10rem,1fr))]">
      {repeat(items).map((i) => <div key={i} className="space-y-1"><Skeleton className="h-4 w-24 max-w-full" /><Skeleton className="h-6 w-32 max-w-full" /></div>)}
    </div>
  );
}

/** `DetailTabs`: the strip of `tabs` triggers. */
export function DetailTabsSkeleton({ tabs = 3 }: { tabs?: number }) {
  return <div className="flex gap-1 overflow-hidden pb-1.5">{repeat(tabs).map((i) => <Skeleton key={i} className="h-8 w-24 flex-none" />)}</div>;
}

/** `SortableTable`: a header row and `rows` dense rows of `columns` cells from `md:` up, the list of rows below. */
export function TableSkeleton({ columns, rows = 5 }: { columns: number; rows?: number }) {
  return (
    <>
      <Table aria-hidden="true" className="hidden md:table">
        <TableHeader>
          <TableRow>{repeat(columns).map((c) => <TableHead key={c}><Skeleton className="h-4 w-16" /></TableHead>)}</TableRow>
        </TableHeader>
        <TableBody>
          {repeat(rows).map((r) => (
            <TableRow key={r}>{repeat(columns).map((c) => <TableCell key={c}><Skeleton className={cn('h-4', c === 0 ? 'w-40' : 'w-20')} /></TableCell>)}</TableRow>
          ))}
        </TableBody>
      </Table>
      <div className="divide-y rounded-lg border md:hidden">
        {repeat(rows).map((r) => <div key={r} className="space-y-2 p-3"><Skeleton className="h-4 w-2/3" /><Skeleton className="h-4 w-1/3" /></div>)}
      </div>
    </>
  );
}

/** `Section`: its title, then `children` (a block by default). */
export function SectionSkeleton({ children }: { children?: ReactNode }) {
  return <div className="space-y-2"><Skeleton className="h-6 w-40" />{children ?? <Skeleton className="h-40 w-full" />}</div>;
}

/** `SectionCard`: the card with its title, then `children` (a chart's area by default). */
export function SectionCardSkeleton({ children }: { children?: ReactNode }) {
  return (
    <Card className="min-w-0 gap-3 py-4">
      <CardHeader><Skeleton className="h-6 w-40" /></CardHeader>
      <CardContent>{children ?? <Skeleton className="h-64 w-full" />}</CardContent>
    </Card>
  );
}

/**
 * A page loading: header (`actions` buttons), figure line (`stats` figures, 0 for none), tab strip (`tabs`, 0 for none),
 * then `children`, the first section of the loaded page (a `Section` by default).
 */
export function PageSkeleton({ label, actions = 1, stats = 4, tabs = 0, children }: {
  label: string; actions?: number; stats?: number; tabs?: number; children?: ReactNode;
}) {
  return (
    <Loading label={label}>
      <div className="space-y-3"><PageHeaderSkeleton actions={actions} />{stats ? <StatLineSkeleton items={stats} /> : null}</div>
      {tabs ? <DetailTabsSkeleton tabs={tabs} /> : null}
      {children ?? <SectionSkeleton />}
    </Loading>
  );
}

/** `Hero`: its label, the large figure and its change. */
export function HeroSkeleton() {
  return (
    <div className="rounded-[22px] border p-5">
      <div className="space-y-2"><Skeleton className="h-4 w-32" /><Skeleton className="h-10 w-56 max-w-full" /><Skeleton className="h-4 w-40" /></div>
    </div>
  );
}

/** An overview tab: the hero when it has one, its figure line, a chart's card, then a table's card. */
export function OverviewSkeleton({ label, hero = false, stats = 4 }: { label: string; hero?: boolean; stats?: number }) {
  return (
    <Loading label={label}>
      {hero ? <HeroSkeleton /> : null}
      <StatLineSkeleton items={stats} />
      <SectionCardSkeleton />
      <SectionCardSkeleton><TableSkeleton columns={4} rows={3} /></SectionCardSkeleton>
    </Loading>
  );
}
