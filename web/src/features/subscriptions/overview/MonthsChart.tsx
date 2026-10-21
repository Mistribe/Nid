import { useTranslation } from 'react-i18next';
import { Bar, CartesianGrid, BarChart, LabelList, XAxis, YAxis } from 'recharts';
import { formatMoney, type MoneyValue } from '@/components/Money';
import { ChartContainer, ChartTooltip, type ChartConfig } from '@/components/ui/chart';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { toPlot } from '../../investments/chart/plot';
import type { MonthView } from '../api';

type Row = { name: string; recurring?: number; longCycle?: number; recurringText: string; longCycleText: string; label: string };

const text = (value?: MoneyValue | null) => (value ? formatMoney(value) : '—');
// « 2026-11 » -> « nov. »
const abbreviate = (month: string) =>
  new Intl.DateTimeFormat('fr-FR', { timeZone: 'UTC', month: 'short' }).format(new Date(`${month}-01T00:00:00Z`));

/**
 * Subscriptions dashboard design §4.2: the next twelve months, monthly charges then long-cycle charges stacked. Every
 * figure read (label, tooltip, table) is the server's string; `toPlot` only sizes the bars.
 */
export function MonthsChart({ months }: { months: MonthView[] }) {
  const { t } = useTranslation();
  const config = {
    recurring: { label: t('subscriptions.overview.months_recurring'), color: 'var(--chart-1)' },
    longCycle: { label: t('subscriptions.overview.months_long'), color: 'var(--chart-2)' },
  } satisfies ChartConfig;
  const rows: Row[] = months.map((m) => {
    const long = m.longCycle ? toPlot(m.longCycle.amount) : 0;
    return {
      name: abbreviate(m.month),
      recurring: m.recurring ? toPlot(m.recurring.amount) : undefined,
      longCycle: m.longCycle ? long : undefined,
      recurringText: text(m.recurring), longCycleText: text(m.longCycle),
      // The two server strings side by side, never their sum.
      label: long !== 0 ? `${text(m.recurring)} + ${text(m.longCycle)}` : '',
    };
  });
  return (
    <figure aria-label={t('subscriptions.overview.months')}>
      <ChartContainer config={config} className="h-64 w-full">
        <BarChart data={rows} accessibilityLayer margin={{ top: 20 }}>
          <CartesianGrid vertical={false} />
          <XAxis dataKey="name" tickLine={false} tickMargin={8} interval={0} />
          <YAxis hide />
          <ChartTooltip content={<MonthTooltip />} />
          <Bar dataKey="recurring" stackId="m" fill="var(--color-recurring)" isAnimationActive={false} />
          <Bar dataKey="longCycle" stackId="m" fill="var(--color-longCycle)" radius={[4, 4, 0, 0]} isAnimationActive={false}>
            <LabelList dataKey="label" position="top" className="fill-foreground text-[10px]" />
          </Bar>
        </BarChart>
      </ChartContainer>
      <Table className="sr-only" aria-label={t('subscriptions.overview.months')}>
        <TableHeader>
          <TableRow>
            <TableHead>{t('subscriptions.overview.months_month')}</TableHead>
            <TableHead>{t('subscriptions.overview.months_recurring')}</TableHead>
            <TableHead>{t('subscriptions.overview.months_long')}</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {rows.map((r, i) => (
            <TableRow key={months[i]!.month}>
              <TableCell>{r.name}</TableCell><TableCell>{r.recurringText}</TableCell><TableCell>{r.longCycleText}</TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </figure>
  );
}

function MonthTooltip({ active, payload }: { active?: boolean; payload?: ReadonlyArray<{ payload?: Row }> }) {
  const { t } = useTranslation();
  const row = payload?.[0]?.payload;
  if (!active || !row) return null;
  return (
    <div className="grid gap-1 rounded-lg border bg-background px-2.5 py-1.5 text-xs shadow-xl">
      <div className="font-medium">{row.name}</div>
      <div className="tabular-nums">{t('subscriptions.overview.months_recurring')} : {row.recurringText}</div>
      <div className="tabular-nums">{t('subscriptions.overview.months_long')} : {row.longCycleText}</div>
    </div>
  );
}
