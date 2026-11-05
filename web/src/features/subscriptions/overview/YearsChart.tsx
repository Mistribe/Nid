import { useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Bar, BarChart, CartesianGrid, XAxis, YAxis } from 'recharts';
import i18n from '@/app/i18n';
import { formatMoney, type MoneyValue } from '@/components/Money';
import { Segmented } from '@/components/form/Segmented';
import { TableToggle } from '@/components/page/TableToggle';
import { ChartContainer, ChartLegend, ChartLegendContent, ChartTooltip, type ChartConfig } from '@/components/ui/chart';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { labelColour } from '@/lib/labelColour';
import board from '@/locales/fr.subscriptions-board.json';
import { toPlot } from '../../investments/chart/plot';
import type { Overview } from '../api';

i18n.addResourceBundle('fr', 'subscriptions-board', board, true, true);

type Year = Overview['years'][number];
type Row = { name: string; texts: Record<string, string>; [key: string]: number | string | undefined | Record<string, string> };

const text = (value?: MoneyValue | null) => (value ? formatMoney(value) : '—');
const plot = (value?: MoneyValue | null) => (value ? toPlot(value.amount) : undefined);
const UPCOMING_OPACITY = 0.4;

/**
 * #396: one bar per calendar year since the first charge, this year projected to December with what is still to come drawn
 * paler; « Par libellé » stacks the paid part by main label. Every figure read (tooltip, table) is the server's string.
 */
export function YearsChart({ years }: { years: Year[] }) {
  const { t } = useTranslation('subscriptions-board');
  const { t: tc } = useTranslation();
  const viewId = useId();
  const [view, setView] = useState<'label' | 'total'>('total');
  const [asTable, setAsTable] = useState(false);
  const labels = years[0]?.byLabel ?? [];
  const labelName = (l: Year['byLabel'][number]) => l.name ?? tc('subscriptions.no_label');
  const config = {
    paid: { label: t('years.paid'), color: 'var(--chart-1)' },
    upcoming: { label: t('years.upcoming'), color: view === 'total' ? 'var(--chart-1)' : 'var(--muted-foreground)' },
    ...Object.fromEntries(labels.map((l, i) => [`l${i}`, { label: labelName(l), color: l.color ? labelColour(l.color) : 'var(--muted-foreground)' }])),
  } satisfies ChartConfig;
  const rows: Row[] = years.map((y) => ({
    name: String(y.year),
    paid: plot(y.paid),
    upcoming: plot(y.upcoming),
    ...Object.fromEntries(y.byLabel.map((l, i) => [`l${i}`, plot(l.paid)])),
    texts: {
      paid: text(y.paid), upcoming: text(y.upcoming), total: text(y.total),
      ...Object.fromEntries(y.byLabel.map((l, i) => [`l${i}`, text(l.paid)])),
    },
  }));
  const series = view === 'total' ? ['paid'] : labels.map((_, i) => `l${i}`);

  return (
    <div className="space-y-3">
      <div className="flex flex-wrap items-center justify-end gap-2">
        <span id={viewId} className="sr-only">{t('years.view')}</span>
        <Segmented value={view} onValueChange={(v) => setView(v === 'label' ? 'label' : 'total')} aria-labelledby={viewId}
          options={[{ value: 'label', label: t('years.by_label') }, { value: 'total', label: t('years.total') }]} />
        <TableToggle asTable={asTable} onToggle={() => setAsTable((v) => !v)} />
      </div>
      {asTable ? (
        <div className="overflow-x-auto">
          <Table aria-label={t('years.title')}>
            <TableHeader>
              <TableRow>
                <TableHead>{t('years.year')}</TableHead>
                {view === 'label' ? labels.map((l) => <TableHead key={l.key} className="text-right">{labelName(l)}</TableHead>) : null}
                <TableHead className="text-right">{t('years.paid')}</TableHead>
                <TableHead className="text-right">{t('years.upcoming')}</TableHead>
                <TableHead className="text-right">{t('years.total')}</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {years.map((y) => (
                <TableRow key={y.year}>
                  <TableCell>{y.year}</TableCell>
                  {view === 'label' ? y.byLabel.map((l) => <TableCell key={l.key} className="text-right tabular-nums">{text(l.paid)}</TableCell>) : null}
                  <TableCell className="text-right tabular-nums">{text(y.paid)}</TableCell>
                  <TableCell className="text-right tabular-nums">{text(y.upcoming)}</TableCell>
                  <TableCell className="text-right tabular-nums">{text(y.total)}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
      ) : (
        <ChartContainer config={config} className="h-64 w-full">
          <BarChart data={rows} title={t('years.title')} maxBarSize={40} accessibilityLayer>
            <CartesianGrid vertical={false} />
            <XAxis dataKey="name" tickLine={false} tickMargin={8} minTickGap={16} />
            <YAxis hide />
            <ChartTooltip cursor={false} content={<YearTooltip series={[...series, 'upcoming']} config={config} />} />
            <ChartLegend content={<ChartLegendContent />} />
            {series.map((key) => (
              <Bar key={key} dataKey={key} stackId="y" fill={`var(--color-${key})`} stroke="var(--card)" strokeWidth={2} animationDuration={800} />
            ))}
            <Bar dataKey="upcoming" stackId="y" fill="var(--color-upcoming)" fillOpacity={UPCOMING_OPACITY} stroke="var(--card)" strokeWidth={2}
              radius={[4, 4, 0, 0]} animationDuration={800} />
          </BarChart>
        </ChartContainer>
      )}
    </div>
  );
}

function YearTooltip({ active, payload, series, config }: {
  active?: boolean; payload?: ReadonlyArray<{ payload?: Row }>; series: string[]; config: ChartConfig;
}) {
  const { t } = useTranslation('subscriptions-board');
  const row = payload?.[0]?.payload;
  if (!active || !row) return null;
  return (
    <div className="grid min-w-44 gap-1 rounded-lg border bg-popover/95 px-3 py-2 text-xs shadow-raised backdrop-blur-sm">
      <div className="font-medium">{row.name}</div>
      {series.map((key) => (
        <div key={key} className="flex justify-between gap-4"><span>{config[key]?.label}</span><span className="tabular-nums">{row.texts[key]}</span></div>
      ))}
      <div className="flex justify-between gap-4 border-t pt-1 font-medium"><span>{t('years.total')}</span><span className="tabular-nums">{row.texts.total}</span></div>
    </div>
  );
}
