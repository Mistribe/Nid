import type { TFunction } from 'i18next';
import { useTranslation } from 'react-i18next';
import { Bar, BarChart, CartesianGrid, Cell, XAxis, YAxis } from 'recharts';
import type { ReviewPart } from '@/api/reports';
import { formatCurrency, formatMoney, type MoneyValue } from '@/components/Money';
import { ChartContainer, ChartTooltip, type ChartConfig } from '@/components/ui/chart';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { directionOf } from '@/lib/amount';
import { toPlot } from '@/lib/plot';
import '@/locales/reports';

// dataviz.md, « Bilan : passage du patrimoine net »: a rise in the good status, a fall in destructive, no change neutral.
const TONE = { up: 'var(--status-good)', down: 'var(--destructive)', flat: 'var(--muted-foreground)' } as const;

export const signed = (value: MoneyValue) => formatCurrency(value.amount, value.currency, { signDisplay: 'exceptZero' });
/** A part the server added later reads as its raw key rather than breaking the page. */
const partLabel = (t: TFunction, key: string) => t(`reports.parts.${key}`, { defaultValue: key });

type PartBar = { key: string; label: string; range: [number, number]; text: string; fill: string };
type Line = { key: string; label: string; text: string; previous: string; total: boolean };

/**
 * The year's net worth parts as floating bars, each between the `low` and `high` the server sent (`toPlot` only turns them
 * into plot coordinates), on an axis fitted to the parts: the start and end values would dwarf them. The table under it says
 * every figure, the year before beside each part.
 */
export function NetWorthBridge({ year, start, parts, previousParts, end }: {
  year: number; start: MoneyValue; parts: ReviewPart[]; previousParts: ReviewPart[]; end: MoneyValue;
}) {
  const { t } = useTranslation();
  const bars: PartBar[] = parts.map((p) => ({
    key: p.key, label: partLabel(t, p.key), range: [toPlot(p.low.amount), toPlot(p.high.amount)], text: signed(p.amount),
    fill: TONE[directionOf(p.amount.amount)],
  }));
  const before = (key: string) => previousParts.find((p) => p.key === key);
  const none = t('reports.net_worth.none');
  const lines: Line[] = [
    { key: 'start', label: t('reports.net_worth.opening'), text: formatMoney(start), previous: none, total: true },
    ...parts.map((p) => {
      const previous = before(p.key);
      return { key: p.key, label: partLabel(t, p.key), text: signed(p.amount), previous: previous ? signed(previous.amount) : none, total: false };
    }),
    // A part of last year alone, so the year before's column adds up to its change.
    ...previousParts.filter((p) => !parts.some((q) => q.key === p.key))
      .map((p) => ({ key: p.key, label: partLabel(t, p.key), text: none, previous: signed(p.amount), total: false })),
    { key: 'end', label: t('reports.net_worth.closing'), text: formatMoney(end), previous: none, total: true },
  ];
  const config = { range: { label: t('reports.net_worth.amount'), color: TONE.up } } satisfies ChartConfig;
  const axis = new Intl.NumberFormat('fr-FR', { style: 'currency', currency: end.currency, notation: 'compact' });
  return (
    <div className="space-y-3">
      <ChartContainer config={config} className="aspect-auto h-64 w-full">
        <BarChart data={bars} layout="vertical" title={t('reports.net_worth.chart')} accessibilityLayer margin={{ top: 8, right: 16 }}>
          <CartesianGrid horizontal={false} />
          {/* 'auto' fits the axis to the parts' span with round ticks around it, without counting from zero. */}
          <XAxis type="number" domain={['auto', 'auto']} tickLine={false} axisLine={false} tickFormatter={(n: number) => axis.format(n)} />
          <YAxis type="category" dataKey="label" width={170} tickLine={false} axisLine={false} />
          <ChartTooltip cursor={false} content={<BridgeTooltip />} />
          <Bar dataKey="range" radius={2} animationDuration={800}>
            {bars.map((r) => <Cell key={r.key} fill={r.fill} />)}
          </Bar>
        </BarChart>
      </ChartContainer>
      <Table aria-label={t('reports.net_worth.table')}>
        <TableHeader>
          <TableRow>
            <TableHead scope="col">{t('reports.net_worth.part')}</TableHead>
            <TableHead scope="col" className="text-right">{year}</TableHead>
            <TableHead scope="col" className="text-right">{year - 1}</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {lines.map((r) => (
            <TableRow key={r.key}>
              <TableHead scope="row" className={r.total ? 'font-semibold text-foreground' : 'font-normal text-foreground'}>{r.label}</TableHead>
              <TableCell className={`text-right tabular-nums ${r.total ? 'font-semibold' : ''}`}>{r.text}</TableCell>
              <TableCell className="text-right tabular-nums text-muted-foreground">{r.previous}</TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </div>
  );
}

function BridgeTooltip({ active, payload }: { active?: boolean; payload?: ReadonlyArray<{ payload?: PartBar }> }) {
  const row = payload?.[0]?.payload;
  if (!active || !row) return null;
  return (
    <div className="grid gap-1 rounded-xl border bg-background px-3 py-2 text-xs shadow-md">
      <div className="font-medium">{row.label}</div>
      <div className="tabular-nums">{row.text}</div>
    </div>
  );
}
