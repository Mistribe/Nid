import type { TFunction } from 'i18next';
import { Fragment } from 'react';
import { useTranslation } from 'react-i18next';
import { Bar, BarChart, CartesianGrid, Cell, LabelList, XAxis, YAxis } from 'recharts';
import type { TaxStep } from '@/api/tax';
import { ChartContainer, ChartLegend, ChartLegendContent, ChartTooltip, type ChartConfig } from '@/components/ui/chart';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { directionOf } from '@/lib/amount';
import { toPlot } from '@/lib/plot';
import { eurUnsigned, resultText } from '../ResultTable';
import '@/locales/tax';

/** The steps that are a running total, drawn from zero. */
export const STEP_TOTALS = ['net_taxable', 'income_tax', 'total_tax'] as const;
const isTotal = (code: string) => (STEP_TOTALS as readonly string[]).includes(code);

/** A step the server added later reads as « Autre »: a household never sees a raw code. */
export const stepLabel = (t: TFunction, code: string) => t(`tax.step.${code}`, { defaultValue: t('tax.steps.other') });

type Kind = 'add' | 'remove' | 'total';
const kindOf = (s: TaxStep): Kind => (isTotal(s.code) ? 'total' : directionOf(s.amount) === 'up' ? 'add' : 'remove');
const FILL: Record<Kind, string> = { add: 'var(--chart-2)', remove: 'var(--status-good)', total: 'var(--chart-1)' };
const amountText = (t: TFunction, s: TaxStep, kind: Kind) => (kind === 'total' ? resultText(t, s.to) : eurUnsigned(s.amount));

type Bar = { key: string; label: string; range: [number, number]; kind: string; text: string; totalText: string; fill: string };

// Sizes a mark only: the figure itself is never shown from this number.
const tiny = (s: TaxStep) => Math.abs(toPlot(s.amount)) < 1;

/** One group's steps as floating bars, in the server's order; a step under one euro (the rounding) is left out here and kept in the table. */
export function StepsChart({ steps, title, legend = false }: { steps: TaxStep[]; title: string; legend?: boolean }) {
  const { t } = useTranslation();
  const bars: Bar[] = steps.filter((s) => isTotal(s.code) || !tiny(s)).map((s) => {
    const kind = kindOf(s);
    const [a, b] = [toPlot(s.from), toPlot(s.to)];
    return {
      key: s.code, label: stepLabel(t, s.code), range: [Math.min(a, b), Math.max(a, b)], kind: t(`tax.steps.${kind}`),
      text: amountText(t, s, kind), totalText: kind === 'total' ? resultText(t, s.to) : '', fill: FILL[kind],
    };
  });
  const config = {
    add: { label: t('tax.steps.add'), color: FILL.add }, remove: { label: t('tax.steps.remove'), color: FILL.remove },
    total: { label: t('tax.steps.total'), color: FILL.total },
  } satisfies ChartConfig;
  const axis = new Intl.NumberFormat('fr-FR', { style: 'currency', currency: 'EUR', notation: 'compact' });
  return (
    <figure aria-label={title} className="space-y-1">
      <figcaption className="text-sm font-medium">{title}</figcaption>
      <ChartContainer config={config} className="aspect-auto w-full" style={{ height: bars.length * 32 + 40 }}>
        <BarChart data={bars} layout="vertical" title={title} accessibilityLayer margin={{ top: 8, right: 80 }}>
          <CartesianGrid horizontal={false} />
          <XAxis type="number" domain={['auto', 'auto']} tickLine={false} axisLine={false} tickFormatter={(n: number) => axis.format(n)} />
          <YAxis type="category" dataKey="label" width={170} tickLine={false} axisLine={false} />
          <ChartTooltip cursor={false} content={<StepTooltip />} />
          {legend && <ChartLegend content={() => (
            <ChartLegendContent payload={(['add', 'remove', 'total'] as const).map((k) => ({ value: k, dataKey: k, color: FILL[k] }))} />
          )} />}
          <Bar dataKey="range" maxBarSize={24} radius={4} animationDuration={800}>
            {bars.map((b) => <Cell key={b.key} fill={b.fill} />)}
            <LabelList dataKey="totalText" position="right" className="fill-foreground text-xs" />
          </Bar>
        </BarChart>
      </ChartContainer>
    </figure>
  );
}

/** Both groups' steps in one table: what each step does, and its amount, without a sign. */
export function StepsTable({ steps }: { steps: TaxStep[] }) {
  const { t } = useTranslation();
  return (
    <Table aria-label={t('tax.steps.table')}>
      <TableHeader>
        <TableRow>
          <TableHead scope="col">{t('tax.steps.step')}</TableHead>
          <TableHead scope="col">{t('tax.steps.effect')}</TableHead>
          <TableHead scope="col" className="text-right">{t('tax.steps.amount')}</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {(['income', 'tax'] as const).map((g) => {
          const rows = steps.filter((s) => s.group === g);
          return rows.length === 0 ? null : (
            <Fragment key={g}>
              <TableRow><TableHead scope="colgroup" colSpan={3} className="text-foreground">{t(`tax.steps.${g}`)}</TableHead></TableRow>
              {rows.map((s) => {
                const kind = kindOf(s);
                return (
                  <TableRow key={s.code}>
                    <TableHead scope="row" className={kind === 'total' ? 'font-semibold text-foreground' : 'font-normal text-foreground'}>{stepLabel(t, s.code)}</TableHead>
                    <TableCell>{t(`tax.steps.${kind}`)}</TableCell>
                    <TableCell className="text-right tabular-nums">{amountText(t, s, kind)}</TableCell>
                  </TableRow>
                );
              })}
            </Fragment>
          );
        })}
      </TableBody>
    </Table>
  );
}

function StepTooltip({ active, payload }: { active?: boolean; payload?: ReadonlyArray<{ payload?: Bar }> }) {
  const row = payload?.[0]?.payload;
  if (!active || !row) return null;
  return (
    <div className="grid gap-1 rounded-xl border bg-background px-3 py-2 text-xs shadow-md">
      <div className="font-medium">{row.label}</div>
      <div className="text-muted-foreground">{row.kind}</div>
      <div className="tabular-nums">{row.text}</div>
    </div>
  );
}
