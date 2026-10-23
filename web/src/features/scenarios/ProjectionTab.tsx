import { useTranslation } from 'react-i18next';
import { CartesianGrid, Line, LineChart, XAxis, YAxis } from 'recharts';
import { LocalDate } from '@/components/LocalDate';
import { formatMoney, Money } from '@/components/Money';
import { Section } from '@/components/page/Section';
import { ChartContainer, ChartLegend, ChartLegendContent, ChartTooltip, type ChartConfig } from '@/components/ui/chart';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { toPlot } from '@/features/investments/chart/plot';
import type { ScenarioResult } from './api';

type Point = ScenarioResult['points'][number];

/**
 * Spec §7 « Projection »: net worth month by month without (neutral, dashed) and with the purchase (primary), dataviz.md
 * « sans et avec »; then the year-end points (and the horizon's) as a table, the server's figures only.
 */
export default function ProjectionTab({ result }: { result: ScenarioResult }) {
  const { t } = useTranslation();
  const points = result.points;
  const currency = result.gap.currency;
  const rows = points.map((p) => ({ on: p.on, without: toPlot(p.without.amount), with: toPlot(p.with.amount), point: p }));
  const config = {
    without: { label: t('scenarios.detail.without'), color: 'var(--muted-foreground)' },
    with: { label: t('scenarios.detail.with'), color: 'var(--primary)' },
  } satisfies ChartConfig;
  const axis = new Intl.NumberFormat('fr-FR', { style: 'currency', currency, notation: 'compact' });
  const yearEnds = points.filter((p, i) => p.on.endsWith('-12-31') || i === points.length - 1);
  return (
    <>
      <Section title={t('scenarios.detail.projection')}>
        <figure aria-label={t('scenarios.detail.projection')}>
          <ChartContainer config={config} className="h-72 w-full">
            <LineChart data={rows} accessibilityLayer margin={{ top: 16, right: 8 }}>
              <CartesianGrid vertical={false} />
              <XAxis dataKey="on" tickFormatter={(on: string) => on.slice(0, 4)} minTickGap={32} tickLine={false} axisLine={false} tickMargin={8} />
              <YAxis width={64} tickLine={false} axisLine={false} tickFormatter={(n: number) => axis.format(n)} />
              <ChartTooltip content={<PointTooltip />} />
              <ChartLegend content={<ChartLegendContent />} />
              <Line dataKey="without" stroke="var(--color-without)" strokeWidth={2} strokeDasharray="5 4" dot={false} isAnimationActive={false} />
              <Line dataKey="with" stroke="var(--color-with)" strokeWidth={2} dot={false} isAnimationActive={false} />
            </LineChart>
          </ChartContainer>
        </figure>
      </Section>
      <Section title={t('scenarios.detail.points')}>
        <Table aria-label={t('scenarios.detail.points')}>
          <TableHeader>
            <TableRow>
              <TableHead>{t('scenarios.detail.col_date')}</TableHead>
              <TableHead className="text-right">{t('scenarios.detail.without')}</TableHead>
              <TableHead className="text-right">{t('scenarios.detail.with')}</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {yearEnds.map((p) => (
              <TableRow key={p.on}>
                <TableHead scope="row" className="font-normal"><LocalDate value={p.on} /></TableHead>
                <TableCell className="text-right tabular-nums"><Money value={p.without} tabular /></TableCell>
                <TableCell className="text-right tabular-nums"><Money value={p.with} tabular /></TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </Section>
    </>
  );
}

function PointTooltip({ active, payload }: { active?: boolean; payload?: ReadonlyArray<{ payload?: { point: Point } }> }) {
  const { t } = useTranslation();
  const p = payload?.[0]?.payload?.point;
  if (!active || !p) return null;
  return (
    <div className="grid gap-1 rounded-lg border bg-background px-2.5 py-1.5 text-xs shadow-xl">
      <div className="font-medium"><LocalDate value={p.on} /></div>
      <div className="tabular-nums">{t('scenarios.detail.without')} : {formatMoney(p.without)}</div>
      <div className="tabular-nums">{t('scenarios.detail.with')} : {formatMoney(p.with)}</div>
    </div>
  );
}
