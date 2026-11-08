import { keepPreviousData } from '@tanstack/react-query';
import { Link, useNavigate, useSearch } from '@tanstack/react-router';
import { X } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import type { RootSearch } from '@/lib/search';
import { ComboboxField } from '@/components/form/ComboboxField';
import { Segmented } from '@/components/form/Segmented';
import { formatCurrency } from '@/components/Money';
import { FilterField, FilterRow } from '@/components/page/FilterRow';
import { PageHeader } from '@/components/page/PageHeader';
import { TableToggle } from '@/components/page/TableToggle';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import type { HouseholdPoint } from '@/features/dashboard/api';
import { monthLabel } from '@/lib/format';
import { SCENARIO_COMPARE, SCENARIOS, type ScenarioCompare } from '../api';
import type { ChartSeries } from '../projection/chartSeries';
import { ProjectionComparisonChart } from '../projection/ProjectionComparisonChart';
import { ProjectionWarnings } from '../projection/ProjectionWarnings';
import { MAX_COMPARED, type CompareSearch } from './compareSearch';
import '@/locales/scenarios';

const YEARS = ['5y', '10y', '20y', '30y'] as const;
const DEFAULT_HORIZON = '10y';

const reading = (p: HouseholdPoint, real: boolean) => (real ? p.real : p.nominal);
const isYearEnd = (p: HouseholdPoint) => p.on.endsWith('-12-31');
const month = (yyyyMm: string) => {
  const [year, m] = yyyyMm.split('-').map(Number);
  return monthLabel(year ?? 0, m ?? 1, 'long');
};

/** Spec §14.2: the base and up to three saved scenarios on one grid, the horizon and the ids in the URL, the reading a page state. */
export function ScenarioComparePage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const navigate = useNavigate();
  const [real, setReal] = useState(true);
  const [asTable, setAsTable] = useState(false);
  const search: RootSearch & CompareSearch = useSearch({ strict: false });
  const scope = search.scope === 'me' ? 'me' : 'household';
  const ids = search.ids ? search.ids.split(',') : [];
  const horizon = search.horizon ?? DEFAULT_HORIZON;

  const list = $api.useQuery('get', SCENARIOS, { params: { query: { scope } } });
  const answer = $api.useQuery('get', SCENARIO_COMPARE,
    { params: { query: { scope, ids: ids.join(','), ...(search.horizon ? { horizon: search.horizon } : {}) } } },
    { enabled: ids.length > 0, placeholderData: keepPreviousData, retry: false });

  const go = (next: Partial<CompareSearch>) => {
    void navigate({ to: '/scenarios/compare', replace: true, search: (prev: RootSearch & CompareSearch) => ({ ...prev, ...next }) });
  };
  const choose = (index: number, id: string) => {
    const next = [...ids];
    next[index] = id;
    go({ ids: next.join(',') });
  };
  const remove = (index: number) => {
    const next = ids.filter((_, i) => i !== index);
    go({ ids: next.length > 0 ? next.join(',') : undefined });
  };
  const nameOf = (id: string) => list.data?.items.find((s) => s.id === id)?.name ?? id;
  const retirements = answer.data?.base.retirementHorizons ?? [];
  const pickers = Math.min(ids.length + 1, MAX_COMPARED);

  return (
    <article className="space-y-6">
      <PageHeader crumbs={[{ label: t('scenarios.title'), to: '/scenarios' }]} title={t('scenarios.compare.title')} />
      <div className="flex flex-wrap items-center gap-3">
        <FilterRow label={t('common.filters')}>
          {Array.from({ length: pickers }, (_, i) => (
            <FilterField key={i} label={t('scenarios.compare.picker', { n: i + 1 })} className="w-full sm:w-auto">
              {(labelId, controlId) => (
                <>
                  <div className="min-w-0 flex-1 sm:w-56 sm:flex-none">
                    <ComboboxField id={controlId} aria-labelledby={labelId} value={ids[i] ?? ''} onValueChange={(id) => choose(i, id)} disabled={!list.data}
                      options={(list.data?.items ?? []).filter((s) => s.id === ids[i] || !ids.includes(s.id)).map((s) => ({ value: s.id, label: s.name }))}
                      searchPlaceholder={t('scenarios.compare.search')} empty={t('scenarios.compare.none')} />
                  </div>
                  {ids[i] ? (
                    <Button type="button" variant="ghost" size="icon" onClick={() => remove(i)} aria-label={t('scenarios.compare.remove', { name: nameOf(ids[i]) })}>
                      <X aria-hidden="true" />
                    </Button>
                  ) : null}
                </>
              )}
            </FilterField>
          ))}
          <FilterField label={t('dashboard.projection.horizon')}>
            {(labelId) => (
              <Segmented value={horizon} onValueChange={(next) => go({ horizon: next === DEFAULT_HORIZON ? undefined : (next as CompareSearch['horizon']) })}
                aria-labelledby={labelId}
                options={[
                  ...YEARS.map((y) => ({ value: y, label: t('dashboard.projection.horizon_years', { count: Number.parseInt(y, 10) }) })),
                  ...retirements.map((r) => ({ value: `retire:${r.person}`, label: t('dashboard.projection.retire', { name: r.name }) })),
                ]} />
            )}
          </FilterField>
          <FilterField label={t('dashboard.projection.reading')}>
            {(labelId) => (
              <Segmented value={real ? 'real' : 'nominal'} onValueChange={(next) => setReal(next === 'real')} aria-labelledby={labelId}
                options={[{ value: 'real', label: t('dashboard.projection.real') }, { value: 'nominal', label: t('dashboard.projection.nominal') }]} />
            )}
          </FilterField>
        </FilterRow>
        {answer.data ? <TableToggle asTable={asTable} onToggle={() => setAsTable((v) => !v)} /> : null}
      </div>
      <Result ids={ids} answer={answer} real={real} asTable={asTable} />
    </article>
  );
}

function Result({ ids, answer, real, asTable }: {
  ids: string[]; answer: { data?: ScenarioCompare; error: unknown; isPlaceholderData: boolean }; real: boolean; asTable: boolean;
}) {
  const { t } = useTranslation();
  if (ids.length === 0) return <p className="text-muted-foreground">{t('scenarios.compare.choose')}</p>;
  if (answer.error) {
    const problem = toProblem(answer.error);
    return <ProblemMessage problem={problem.status === 404 ? { kind: 'not_found', detail: t('scenarios.compare.not_found') } : present(problem)} />;
  }
  const data = answer.data;
  if (!data) return <Skeleton role="status" aria-busy="true" aria-label={t('scenarios.compare.loading')} className="h-64 w-full rounded-xl" />;
  const series: ChartSeries[] = [
    { key: 'base', label: t('scenarios.compare.base'), points: data.base.points },
    ...data.scenarios.map((s, i) => ({ key: `s${i}`, label: s.name, points: s.result.points })),
  ];
  return (
    <div className="space-y-4">
      {asTable
        ? <YearTable series={series} currency={data.base.currency} real={real} />
        : <ProjectionComparisonChart series={series} currency={data.base.currency} real={real} className="h-72" />}
      <HorizonTable data={data} real={real} />
      <Warnings data={data} />
      <p className="text-sm text-muted-foreground">{t('scenarios.projection.result.reminder')}</p>
    </div>
  );
}

function HorizonTable({ data, real }: { data: ScenarioCompare; real: boolean }) {
  const { t } = useTranslation();
  const money = (amount: string) => formatCurrency(amount, data.base.currency);
  const last = (points: HouseholdPoint[]) => points[points.length - 1];
  const at = (points: HouseholdPoint[]) => { const p = last(points); return p ? money(reading(p, real)) : '—'; };
  return (
    <Table aria-label={t('scenarios.compare.horizon_table')}>
      <TableHeader>
        <TableRow>
          <TableHead scope="col">{t('scenarios.compare.line')}</TableHead>
          <TableHead scope="col" className="text-right">{t('scenarios.compare.value')}</TableHead>
          <TableHead scope="col" className="text-right">{t('scenarios.compare.gap')}</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        <TableRow>
          <TableHead scope="row" className="font-normal">{t('scenarios.compare.base')}</TableHead>
          <TableCell className="text-right tabular-nums">{at(data.base.points)}</TableCell>
          <TableCell className="text-right">—</TableCell>
        </TableRow>
        {data.scenarios.map((s) => (
          <TableRow key={s.id}>
            <TableHead scope="row" className="font-normal">
              {s.kind === 'projection'
                ? <Link to="/scenarios/projection/$id" params={{ id: s.id }} className="underline-offset-4 hover:underline">{s.name}</Link>
                : s.kind === 'property_sale'
                  ? <Link to="/scenarios/sale/$id" params={{ id: s.id }} className="underline-offset-4 hover:underline">{s.name}</Link>
                  : <Link to="/scenarios/$id" params={{ id: s.id }} className="underline-offset-4 hover:underline">{s.name}</Link>}
            </TableHead>
            <TableCell className="text-right tabular-nums">{at(s.result.points)}</TableCell>
            <TableCell className="text-right tabular-nums">{money(real ? s.gapReal : s.gapNominal)}</TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}

/** The dashboard card's table: year ends and the horizon's last point, one column per line. */
function YearTable({ series, currency, real }: { series: ChartSeries[]; currency: string; real: boolean }) {
  const { t } = useTranslation();
  const grid = series[0]?.points ?? [];
  const lastIndex = grid.length - 1;
  const rows = grid.filter((p, i) => isYearEnd(p) || i === lastIndex);
  return (
    <Table aria-label={t('scenarios.compare.year_table')}>
      <TableHeader>
        <TableRow>
          <TableHead scope="col">{t('dashboard.projection.year')}</TableHead>
          {series.map((s) => <TableHead key={s.key} scope="col" className="text-right">{s.label}</TableHead>)}
        </TableRow>
      </TableHeader>
      <TableBody>
        {rows.map((p) => (
          <TableRow key={p.on}>
            <TableHead scope="row" className="font-normal">{isYearEnd(p) ? p.on.slice(0, 4) : month(p.on.slice(0, 7))}</TableHead>
            {series.map((s) => {
              const point = s.points.find((q) => q.on === p.on);
              return <TableCell key={s.key} className="text-right tabular-nums">{point ? formatCurrency(reading(point, real), currency) : '—'}</TableCell>;
            })}
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}

/** The base's warnings once, then each scenario's own under its name. */
function Warnings({ data }: { data: ScenarioCompare }) {
  const { t } = useTranslation();
  const groups = [
    { key: 'base', name: t('scenarios.compare.base'), warnings: data.base.warnings },
    ...data.scenarios.map((s) => ({ key: s.id, name: s.name, warnings: s.result.warnings })),
  ].filter((g) => g.warnings.length > 0);
  if (groups.length === 0) return null;
  return (
    <div className="space-y-2">
      {groups.map((g) => (
        <div key={g.key}>
          <p className="text-sm font-medium">{g.name}</p>
          <ProjectionWarnings warnings={g.warnings} />
        </div>
      ))}
    </div>
  );
}
