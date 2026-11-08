import { useTranslation } from 'react-i18next';
import { formatCurrency } from '@/components/Money';
import { Section } from '@/components/page/Section';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import type { HouseholdPoint } from '@/api/dashboard';
import { monthLabel } from '@/lib/format';
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
 * the base's point and the gap at a scenario point share its index. Every figure is a server string.
 */
export default function ProjectionScenarioTab({ comparison, real }: { comparison: ProjectionComparison; real: boolean }) {
  const { t } = useTranslation();
  const text = useMilestoneText();
  const currency = comparison.base.currency;
  const money = (amount: string | undefined) => (amount === undefined ? t('scenarios.dash') : formatCurrency(amount, currency));
  const points = comparison.scenario.points;
  const rows = points.flatMap((p, i) => (i > 0 && (isYearEnd(p.on) || i === points.length - 1) ? [{ p, i }] : []));
  const milestones = comparison.scenario.milestones;
  return (
    <>
      <Section title={t('scenarios.projection.detail.chart')} srTitle>
        <ProjectionComparisonChart series={baseAndScenario(comparison, t)} currency={comparison.base.currency} milestones={comparison.scenario.milestones} real={real} className="h-72" />
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
