import { useTranslation } from 'react-i18next';
import { formatCurrency } from '@/components/Money';
import { Section } from '@/components/page/Section';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import type { HouseholdPoint } from '@/api/dashboard';
import { ProblemMessage } from '@/components/ProblemMessage';
import { RangesSummary } from '@/features/dashboard/ProjectionRanges';
import { monthLabel } from '@/lib/format';
import { PROJECTION_SCENARIO } from '../api';
import type { ProjectionComparison } from './api';
import { ProjectionComparisonChart, useMilestoneText } from './ProjectionComparisonChart';
import { baseAndScenario } from './chartSeries';
import '@/locales/scenarios';

/** yyyy-mm-dd as jj/mm/aaaa (a string swap, nothing computed). */
const dayFirst = (iso: string) => iso.split('-').reverse().join('/');
const isYearEnd = (on: string) => on.endsWith('-12-31');
const reading = (p: HouseholdPoint | undefined, real: boolean) => (p ? (real ? p.real : p.nominal) : undefined);

/**
 * Spec §8 « Projection » of a projection scenario: the base and the scenario on one chart, the scenario's milestones, then each year
 * end (and the horizon's month) as a table of the base, the scenario and the gap, in the chosen reading. The grids are the same:
 * the base's point and the gap at a scenario point share its index. Every figure is a server string. Ranges spec §6: with `ranges`,
 * the scenario's bands, asked apart so the page itself stays quick.
 */
export default function ProjectionScenarioTab({ id, comparison, real, ranges }: { id: string; comparison: ProjectionComparison; real: boolean; ranges: boolean }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const ranged = $api.useQuery('get', PROJECTION_SCENARIO, { params: { path: { id }, query: { ranges: true } } }, { enabled: ranges });
  const bands = ranges ? ranged.data?.result.scenario.ranges ?? undefined : undefined;
  const text = useMilestoneText();
  const currency = comparison.base.currency;
  const money = (amount: string | undefined) => (amount === undefined ? t('scenarios.dash') : formatCurrency(amount, currency));
  const points = comparison.scenario.points;
  const rows = points.flatMap((p, i) => (i > 0 && (isYearEnd(p.on) || i === points.length - 1) ? [{ p, i }] : []));
  const milestones = comparison.scenario.milestones;
  return (
    <>
      <Section title={t('scenarios.projection.detail.chart')} srTitle>
        <ProjectionComparisonChart series={baseAndScenario(comparison, t)} currency={comparison.base.currency} milestones={comparison.scenario.milestones} real={real}
          className="h-72" band={bands?.points} />
        {ranges && ranged.isFetching && !bands ? <p role="status" className="text-sm text-muted-foreground">{t('dashboard.projection.ranges_loading')}</p> : null}
        {ranges && ranged.error ? <ProblemMessage problem={present(toProblem(ranged.error))} /> : null}
        {bands ? <RangesSummary ranges={bands} assumptions={comparison.scenario.assumptions} real={real} currency={currency} /> : null}
        {milestones.length > 0 ? (
          <ul aria-label={t('scenarios.projection.detail.milestones')} className="space-y-0.5 text-sm text-muted-foreground">
            {milestones.map((m) => (
              <li key={`${m.kind}-${m.on}-${m.item ?? m.label}`}>{t('dashboard.projection.milestone_line', { date: dayFirst(m.on), what: text(m) })}</li>
            ))}
          </ul>
        ) : null}
      </Section>
      <Section title={t('scenarios.projection.detail.table')}>
        <Table aria-label={t('scenarios.projection.detail.table')}>
          <TableHeader>
            <TableRow>
              <TableHead scope="col">{t('scenarios.projection.detail.col_year')}</TableHead>
              <TableHead scope="col" className="text-right">{t('scenarios.projection.detail.col_base')}</TableHead>
              <TableHead scope="col" className="text-right">{t('scenarios.projection.detail.col_scenario')}</TableHead>
              <TableHead scope="col" className="text-right">{t('scenarios.projection.detail.col_gap')}</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {rows.map(({ p, i }) => {
              const gap = comparison.gaps[i];
              const [year, month] = p.on.split('-').map(Number);
              return (
                <TableRow key={p.on}>
                  <TableHead scope="row" className="font-normal">{isYearEnd(p.on) ? p.on.slice(0, 4) : monthLabel(year ?? 0, month ?? 1, 'long')}</TableHead>
                  <TableCell className="text-right tabular-nums">{money(reading(comparison.base.points[i], real))}</TableCell>
                  <TableCell className="text-right tabular-nums">{money(reading(p, real))}</TableCell>
                  <TableCell className="text-right tabular-nums">{money(gap ? (real ? gap.real : gap.nominal) : undefined)}</TableCell>
                </TableRow>
              );
            })}
          </TableBody>
        </Table>
      </Section>
    </>
  );
}
