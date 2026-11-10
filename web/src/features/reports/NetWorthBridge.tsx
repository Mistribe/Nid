import type { TFunction } from 'i18next';
import { useTranslation } from 'react-i18next';
import { Bar, BarChart, CartesianGrid, Cell, XAxis, YAxis } from 'recharts';
import type { ReviewPart } from '@/api/reports';
import { formatCurrency, formatMoney, type MoneyValue } from '@/components/Money';
import { ChartContainer, ChartTooltip, type ChartConfig } from '@/components/ui/chart';
import { Table, TableBody, TableCell, TableHead, TableRow } from '@/components/ui/table';
import { directionOf } from '@/lib/amount';
import { toPlot } from '@/lib/plot';
import '@/locales/reports';

// dataviz.md, « Bilan : passage du patrimoine net »: the totals in the accent, a rise in the good status, a fall in destructive.
const TOTAL = 'var(--primary)';
const RISE = 'var(--status-good)';
const FALL = 'var(--destructive)';

export const signed = (value: MoneyValue) => formatCurrency(value.amount, value.currency, { signDisplay: 'exceptZero' });
/** A part the server added later reads as its raw key rather than breaking the page. */
const partLabel = (t: TFunction, key: string) => t(`reports.parts.${key}`, { defaultValue: key });

type Row = { key: string; label: string; range: [number, number]; text: string; fill: string };

/**
 * The year's net worth from its start to its end, one floating bar per part between them. Each bar spans the `low` and
 * `high` the server sent; `toPlot` only turns them into plot coordinates. The table under it says every figure.
 */
export function NetWorthBridge({ start, parts, end }: { start: MoneyValue; parts: ReviewPart[]; end: MoneyValue }) {
  const { t } = useTranslation();
  const rows: Row[] = [
    { key: 'start', label: t('reports.net_worth.opening'), range: [0, toPlot(start.amount)], text: formatMoney(start), fill: TOTAL },
    ...parts.map((p) => ({
      key: p.key, label: partLabel(t, p.key), range: [toPlot(p.low.amount), toPlot(p.high.amount)] as [number, number],
      text: signed(p.amount), fill: directionOf(p.amount.amount) === 'down' ? FALL : RISE,
    })),
    { key: 'end', label: t('reports.net_worth.closing'), range: [0, toPlot(end.amount)], text: formatMoney(end), fill: TOTAL },
  ];
  const config = { range: { label: t('reports.net_worth.amount'), color: TOTAL } } satisfies ChartConfig;
  const axis = new Intl.NumberFormat('fr-FR', { style: 'currency', currency: end.currency, notation: 'compact' });
  return (
    <div className="space-y-3">
      <ChartContainer config={config} className="aspect-auto h-72 w-full">
        <BarChart data={rows} layout="vertical" title={t('reports.net_worth.chart')} accessibilityLayer margin={{ top: 8, right: 16 }}>
          <CartesianGrid horizontal={false} />
          <XAxis type="number" tickLine={false} axisLine={false} tickFormatter={(n: number) => axis.format(n)} />
          <YAxis type="category" dataKey="label" width={170} tickLine={false} axisLine={false} />
          <ChartTooltip cursor={false} content={<BridgeTooltip />} />
          <Bar dataKey="range" radius={2} animationDuration={800}>
            {rows.map((r) => <Cell key={r.key} fill={r.fill} />)}
          </Bar>
        </BarChart>
      </ChartContainer>
      <Table aria-label={t('reports.net_worth.table')}>
        <TableBody>
          {rows.map((r) => (
            <TableRow key={r.key}>
              <TableHead scope="row" className={r.fill === TOTAL ? 'font-semibold text-foreground' : 'font-normal text-foreground'}>{r.label}</TableHead>
              <TableCell className={`text-right tabular-nums ${r.fill === TOTAL ? 'font-semibold' : ''}`}>{r.text}</TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </div>
  );
}

function BridgeTooltip({ active, payload }: { active?: boolean; payload?: ReadonlyArray<{ payload?: Row }> }) {
  const row = payload?.[0]?.payload;
  if (!active || !row) return null;
  return (
    <div className="grid gap-1 rounded-xl border bg-background px-3 py-2 text-xs shadow-md">
      <div className="font-medium">{row.label}</div>
      <div className="tabular-nums">{row.text}</div>
    </div>
  );
}
