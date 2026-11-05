import { Link, useNavigate } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import i18n from '@/app/i18n';
import board from '@/locales/fr.subscriptions-board.json';
import { Bar, CartesianGrid, BarChart, LabelList, XAxis, YAxis } from 'recharts';
import { formatMoney, type MoneyValue } from '@/components/Money';
import { ChartContainer, ChartLegend, ChartLegendContent, ChartTooltip, type ChartConfig } from '@/components/ui/chart';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { toPlot } from '../../investments/chart/plot';
import type { MonthView } from '../api';

i18n.addResourceBundle('fr', 'subscriptions-board', board, true, true);

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
  const { t } = useTranslation('subscriptions-board');
  const navigate = useNavigate();
  // Keeps the current scope, like the other overview links; a click on a bar goes where its table row's link goes.
  const open = (month: string) => void navigate({ to: '/subscriptions', search: (prev) => ({ scope: prev.scope, tab: 'calendar', month }) });
  const barClick = { cursor: 'pointer', onClick: (_: unknown, i: number) => open(months[i]!.month) };
  const config = {
    recurring: { label: t('overview.months_recurring'), color: 'var(--chart-1)' },
    longCycle: { label: t('overview.months_long'), color: 'var(--chart-2)' },
  } satisfies ChartConfig;
  const rows: Row[] = months.map((m) => {
    const long = m.longCycle ? toPlot(m.longCycle.amount) : 0;
    return {
      name: abbreviate(m.month),
      recurring: m.recurring ? toPlot(m.recurring.amount) : undefined,
      longCycle: m.longCycle ? long : undefined,
      recurringText: text(m.recurring), longCycleText: text(m.longCycle),
      // Over the bar, only what the long cycles add (one short line, never the sum); the month's whole is in the tooltip and the table.
      // A no-break space: the label wraps on spaces, and « + » alone above the amount read as a stray mark (issue 359).
      label: long !== 0 ? `+\u00a0${text(m.longCycle)}` : '',
    };
  });
  return (
    <figure aria-label={t('overview.months')}>
      <ChartContainer config={config} className="h-64 w-full">
        <BarChart data={rows} maxBarSize={24} accessibilityLayer margin={{ top: 24 }}>
          <CartesianGrid vertical={false} />
          <XAxis dataKey="name" tickLine={false} tickMargin={8} interval={0} />
          <YAxis hide />
          <ChartTooltip content={<MonthTooltip />} />
          <ChartLegend content={<ChartLegendContent />} />
          <Bar dataKey="recurring" stackId="m" fill="var(--color-recurring)" stroke="var(--card)" strokeWidth={2} radius={[4, 4, 0, 0]} animationDuration={800} {...barClick} />
          <Bar dataKey="longCycle" stackId="m" fill="var(--color-longCycle)" stroke="var(--card)" strokeWidth={2} radius={[4, 4, 0, 0]} animationDuration={800} {...barClick}>
            <LabelList dataKey="label" position="top" className="fill-foreground text-[11px] font-medium" />
          </Bar>
        </BarChart>
      </ChartContainer>
      {/* Hidden until a month link takes focus, so keyboard users see where they are. */}
      <div data-testid="months-table" className="sr-only focus-within:not-sr-only focus-within:mt-2">
      <Table aria-label={t('overview.months')}>
        <TableHeader>
          <TableRow>
            <TableHead>{t('overview.months_month')}</TableHead>
            <TableHead>{t('overview.months_recurring')}</TableHead>
            <TableHead>{t('overview.months_long')}</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {rows.map((r, i) => (
            <TableRow key={months[i]!.month}>
              <TableCell><Link to="/subscriptions" search={(prev) => ({ scope: prev.scope, tab: 'calendar', month: months[i]!.month })}>{r.name}</Link></TableCell><TableCell>{r.recurringText}</TableCell><TableCell>{r.longCycleText}</TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
      </div>
    </figure>
  );
}

function MonthTooltip({ active, payload }: { active?: boolean; payload?: ReadonlyArray<{ payload?: Row }> }) {
  const { t } = useTranslation('subscriptions-board');
  const row = payload?.[0]?.payload;
  if (!active || !row) return null;
  return (
    <div className="grid gap-1 rounded-lg border bg-popover/95 px-3 py-2 text-xs shadow-raised backdrop-blur-sm">
      <div className="font-medium">{row.name}</div>
      <div className="tabular-nums">{t('overview.months_recurring')} : {row.recurringText}</div>
      <div className="tabular-nums">{t('overview.months_long')} : {row.longCycleText}</div>
    </div>
  );
}
