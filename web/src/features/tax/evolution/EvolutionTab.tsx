import type { TFunction } from 'i18next';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Bar, BarChart, Cell, LabelList, ReferenceLine, XAxis, YAxis } from 'recharts';
import { useApi } from '@/api/ApiProvider';
import { FIRST_ESTIMATE_YEAR, TAX_HISTORY, type TaxYear } from '@/api/tax';
import { SectionCard } from '@/components/page/SectionCard';
import { SeriesLink } from '@/components/page/SeriesLink';
import { Loading, SectionCardSkeleton, TableSkeleton } from '@/components/page/skeletons';
import { TableToggle } from '@/components/page/TableToggle';
import { ChartContainer, ChartLegend, ChartLegendContent, ChartTooltip, type ChartConfig } from '@/components/ui/chart';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { toPlot } from '@/lib/plot';
import { ChangesSection } from '../ChangesSection';
import { percentText } from '../format';
import { eur, resultText } from '../ResultTable';
import '@/locales/tax';

const FILL = 'var(--chart-1)';
const ESTIMATE_FILL = 'color-mix(in oklab, var(--chart-1) 40%, transparent)';

const sourceText = (t: TFunction, source: TaxYear['source']) => (source === 'assessment' ? t('tax.evolution.from.assessment') : t('tax.evolution.from.estimate'));

type BarRow = { year: string; plot: number; source: TaxYear['source']; rateText: string; taxText: string; sourceText: string };

/**
 * « Comment mon impôt a changé ? »: the server's yearly history (a notice's tax where there is one, else the estimate's), one bar per
 * year it lists. `history` is the estimate's own; without it the tab asks for the series. Bars are sized with `toPlot`, every figure is the server's.
 */
export function EvolutionTab({ year, scope, history }: {
  year: string; scope: 'me' | 'household'; history?: TaxYear[] | null;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const [asTable, setAsTable] = useState(false);
  const fetched = $api.useQuery('get', TAX_HISTORY, { params: { query: { scope } } }, { enabled: !history });
  const rows = history ?? fetched.data?.years ?? [];

  if (!rows.length) {
    return fetched.isPending && !history
      ? <Loading label={t('tax.loading')}><SectionCardSkeleton><TableSkeleton columns={5} rows={3} /></SectionCardSkeleton></Loading>
      : <p className="text-sm text-muted-foreground">{t('tax.evolution.empty')}</p>;
  }
  return (
    <div className="animate-enter space-y-4">
      <h2 className="text-title">{t('tax.evolution.question')}</h2>
      <SectionCard title={t('tax.evolution.title')} note={t('tax.evolution.intro')} className="space-y-3" actions={(
        <>
          {rows.some((r) => r.source === 'assessment') ? <SeriesLink keys={['tax:yearly']} /> : null}
          <TableToggle asTable={asTable} onToggle={() => setAsTable((v) => !v)} />
        </>
      )}>
        {asTable ? <HistoryTable rows={rows} current={year} /> : <HistoryChart rows={rows} current={year} />}
      </SectionCard>
      {Number(year) >= FIRST_ESTIMATE_YEAR ? <ChangesSection year={year} scope={scope} /> : null}
    </div>
  );
}

function HistoryChart({ rows, current }: { rows: TaxYear[]; current: string }) {
  const { t } = useTranslation();
  const bars: BarRow[] = rows.map((r) => ({
    year: String(r.year), plot: toPlot(r.tax), source: r.source, rateText: r.averageRate ? percentText(t, r.averageRate) : '',
    taxText: resultText(t, r.tax), sourceText: sourceText(t, r.source),
  }));
  const sources = (['assessment', 'estimate'] as const).filter((s) => rows.some((r) => r.source === s));
  const legendFill = { assessment: FILL, estimate: ESTIMATE_FILL };
  const config = {
    assessment: { label: t('tax.evolution.from.assessment'), color: FILL }, estimate: { label: t('tax.evolution.from.estimate'), color: ESTIMATE_FILL },
  } satisfies ChartConfig;
  return (
    <figure aria-label={t('tax.evolution.title')}>
      <ChartContainer config={config} className="aspect-auto w-full" style={{ height: 240 }}>
        <BarChart data={bars} title={t('tax.evolution.title')} accessibilityLayer margin={{ top: 20, right: 8, left: 8 }}>
          <XAxis dataKey="year" minTickGap={24} tickLine={false} axisLine={false} />
          <YAxis hide />
          <ReferenceLine y={0} stroke="var(--border)" />
          <ChartTooltip cursor={false} content={<HistoryTooltip />} />
          <ChartLegend content={() => (
            <ChartLegendContent payload={sources.map((s) => ({ value: s, dataKey: s, color: legendFill[s] }))} />
          )} />
          <Bar dataKey="plot" maxBarSize={24} animationDuration={800}>
            {bars.map((b) => (
              <Cell key={b.year} fill={FILL} radius={(b.plot < 0 ? [0, 0, 4, 4] : [4, 4, 0, 0]) as unknown as number} fillOpacity={b.source === 'estimate' ? 0.4 : undefined}
                stroke={b.year === current ? 'var(--foreground)' : undefined} strokeWidth={b.year === current ? 2 : undefined} />
            ))}
            {/* The rate sits at the bar's end: above a positive bar, below a negative one. */}
            <LabelList dataKey="rateText" content={({ x, y, width, height, index }) => {
              const b = bars[Number(index)]!;
              const [top, bottom] = [Math.min(Number(y), Number(y) + Number(height)), Math.max(Number(y), Number(y) + Number(height))];
              return <text x={Number(x) + Number(width) / 2} y={b.plot < 0 ? bottom + 14 : top - 6} textAnchor="middle" className="fill-foreground text-xs">{b.rateText}</text>;
            }} />
          </Bar>
        </BarChart>
      </ChartContainer>
    </figure>
  );
}

function HistoryTable({ rows, current }: { rows: TaxYear[]; current: string }) {
  const { t } = useTranslation();
  return (
    <Table aria-label={t('tax.evolution.title')}>
      <TableHeader>
        <TableRow>
          <TableHead>{t('tax.evolution.year')}</TableHead>
          <TableHead className="text-right">{t('tax.figure.net_taxable')}</TableHead>
          <TableHead className="text-right">{t('tax.evolution.tax')}</TableHead>
          <TableHead className="text-right">{t('tax.evolution.rate')}</TableHead>
          <TableHead>{t('tax.evolution.source')}</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {rows.map((r) => (
          <TableRow key={r.year} aria-current={String(r.year) === current ? 'true' : undefined} className={String(r.year) === current ? 'font-medium' : undefined}>
            <TableCell>{r.year}</TableCell>
            <TableCell className="text-right tabular-nums">{r.netTaxable ? eur(r.netTaxable) : '—'}</TableCell>
            <TableCell className="text-right tabular-nums">{resultText(t, r.tax)}</TableCell>
            <TableCell className="text-right tabular-nums">{r.averageRate ? percentText(t, r.averageRate) : '—'}</TableCell>
            <TableCell className="text-muted-foreground">{sourceText(t, r.source)}</TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}

function HistoryTooltip({ active, payload }: { active?: boolean; payload?: ReadonlyArray<{ payload?: BarRow }> }) {
  const row = payload?.[0]?.payload;
  if (!active || !row) return null;
  return (
    <div className="grid gap-1 rounded-xl border bg-background px-3 py-2 text-xs shadow-md">
      <div className="font-medium">{row.year}</div>
      <div className="tabular-nums">{row.taxText}</div>
      <div className="text-muted-foreground">{row.sourceText}</div>
    </div>
  );
}
