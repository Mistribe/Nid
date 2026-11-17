import { useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Bar, BarChart, Cell, LabelList, XAxis, YAxis } from 'recharts';
import type { TFunction } from 'i18next';
import type { TaxEstimate } from '@/api/tax';
import { SectionCard } from '@/components/page/SectionCard';
import { StatLine } from '@/components/page/StatLine';
import { TableToggle } from '@/components/page/TableToggle';
import { Button } from '@/components/ui/button';
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from '@/components/ui/collapsible';
import { ChartContainer, type ChartConfig } from '@/components/ui/chart';
import { Table, TableBody, TableCell, TableHead, TableRow } from '@/components/ui/table';
import { toPlot } from '@/lib/plot';
import { balanceWords } from '../balance';
import { percentText, tipText, ZERO } from '../format';
import { eur, eurUnsigned, resultText, Term } from '../ResultTable';
import '@/locales/tax';

/** The balance in words, then its amount without a sign; the words alone when there is nothing to settle. */
const gapText = (t: TFunction, balance: string, year: number) => (ZERO.test(balance)
  ? balanceWords(t, balance, year) : t('tax.withholding.gap', { words: balanceWords(t, balance, year), amount: eurUnsigned(balance) }));

/** What the card's dl rows read: a muted label and its amount. */
function Rows({ rows, label }: { rows: [ReactNode, string][]; label?: string }) {
  return (
    <dl aria-label={label} className="grid grid-cols-[1fr_auto] gap-x-4 text-sm">
      {rows.map(([title, value], i) => (
        <div key={i} className="col-span-2 grid grid-cols-subgrid items-baseline border-b py-1 last:border-b-0">
          <dt className="text-muted-foreground">{title}</dt>
          <dd className="text-right font-medium tabular-nums">{value}</dd>
        </div>
      ))}
    </dl>
  );
}

/** « En savoir plus » unfolding a help paragraph. */
function More({ children }: { children: string }) {
  const { t } = useTranslation();
  return (
    <Collapsible>
      <CollapsibleTrigger asChild><Button variant="link" className="h-auto p-0 text-sm">{t('tax.withholding.more')}</Button></CollapsibleTrigger>
      <CollapsibleContent><p className="pt-1 text-sm text-muted-foreground">{children}</p></CollapsibleContent>
    </Collapsible>
  );
}

/**
 * The chart's two bars: `value` only sizes them, `text` is the server's figure. A bar at zero or a refund draws a hairline (0.01), never a
 * negative or empty bar: the label says it in words.
 */
export function withheldBars(t: TFunction, estimate: TaxEstimate) {
  const w = estimate.result.withholding;
  return [
    { key: 'annual', label: t('tax.withholding.annual'), value: Math.max(0.01, toPlot(w.annual)), text: eur(w.annual), fill: 'var(--chart-1)', tip: undefined as string | undefined },
    { key: 'due', label: t('tax.withholding.due'), value: Math.max(0.01, toPlot(estimate.result.incomeTax)), text: resultText(t, estimate.result.incomeTax), fill: 'var(--chart-2)', tip: tipText(t, 'due') },
  ];
}

/**
 * Tax estimate spec §4bis: « Est-ce que je suis assez (ou trop) prélevé ? » answered with the rate and monthly amounts, the year's
 * withholding against the tax due, then what to type on impots.gouv.fr and the safe cut. Every figure is the server's; `toPlot` only sizes bars.
 */
export function WithholdingTab({ estimate }: { estimate: TaxEstimate }) {
  const { t } = useTranslation();
  const [asTable, setAsTable] = useState(false);
  const w = estimate.result.withholding;
  const cut = w.cut;
  const rental = !ZERO.test(w.rentalMonthly);
  const declared: [ReactNode, string][] = [
    ...w.declarants.flatMap((d, i): [ReactNode, string][] => {
      const per = estimate.result.declarants[i]?.perDeducted ?? '0';
      return [
        [t('tax.withholding.salaries', { name: d.name }), eur(d.salaries)],
        ...(ZERO.test(d.pensions) ? [] : [[t('tax.withholding.pensions', { name: d.name }), eur(d.pensions)] as [ReactNode, string]]),
        ...(ZERO.test(per) ? [] : [[t('tax.withholding.per', { name: d.name }), eur(per)] as [ReactNode, string]]),
      ];
    }),
    ...(ZERO.test(w.rentalDeclared) ? [] : [[<Term title={t('tax.withholding.rental')} tip={t('tax.withholding.rental_hint', { gross: eur(estimate.inputs.rentalGross) })} />, eur(w.rentalDeclared)] as [ReactNode, string]]),
  ];
  const bars = withheldBars(t, estimate);
  const config = { annual: { label: bars[0]!.label, color: 'var(--chart-1)' }, due: { label: bars[1]!.label, color: 'var(--chart-2)' } } satisfies ChartConfig;

  return (
    <div className="animate-enter space-y-4">
      <h2 className="text-title">{t('tax.withholding.question')}</h2>
      <StatLine items={[
        { label: t('tax.withholding.rate'), value: percentText(t, w.rate), tip: tipText(t, 'withholding_rate') },
        ...w.declarants.map((d) => ({ label: t('tax.withholding.monthly', { name: d.name }), value: eur(d.monthly) })),
        ...(rental ? [{ label: t('tax.withholding.rental_monthly'), value: eur(w.rentalMonthly), hint: t('tax.withholding.rental_monthly_hint'), tip: tipText(t, 'rental_instalment') }] : []),
      ]} />
      <SectionCard title={t('tax.withholding.expected')} actions={<TableToggle asTable={asTable} onToggle={() => setAsTable((v) => !v)} />} className="space-y-3">
        {asTable ? (
          <Table aria-label={t('tax.withholding.chart')}>
            <caption className="sr-only">{t('tax.withholding.chart')}</caption>
            <TableBody>
              {bars.map((b) => (
                <TableRow key={b.key}><TableHead scope="row" className="font-normal text-foreground"><Term title={b.label} tip={b.tip} /></TableHead><TableCell className="text-right tabular-nums">{b.text}</TableCell></TableRow>
              ))}
            </TableBody>
          </Table>
        ) : (
          <figure aria-label={t('tax.withholding.chart')}>
            <ChartContainer config={config} className="aspect-auto w-full" style={{ height: 150 }}>
              <BarChart data={bars} layout="vertical" title={t('tax.withholding.chart')} accessibilityLayer barCategoryGap="20%" margin={{ top: 20, right: 8, left: 8 }}>
                <XAxis type="number" hide domain={[0, (m: number) => Math.max(m, 1)]} />
                <YAxis type="category" dataKey="label" hide />
                <Bar dataKey="value" maxBarSize={24} radius={4} animationDuration={800}>
                  {bars.map((b) => <Cell key={b.key} fill={b.fill} />)}
                  {/* The caption sits above its bar so the plot keeps the card's width on a phone. */}
                  <LabelList dataKey="text" content={({ x, y, index }) => (
                    <text x={Number(x)} y={Number(y) - 6} className="fill-foreground text-xs">{`${bars[Number(index)]!.label} : ${bars[Number(index)]!.text}`}</text>
                  )} />
                </Bar>
              </BarChart>
            </ChartContainer>
          </figure>
        )}
        <p className="text-sm font-medium">{gapText(t, w.balance, estimate.year)}</p>
        {ZERO.test(w.leftOut) ? null : <Rows rows={[[<Term title={t('tax.withholding.left_out')} tip={tipText(t, 'left_out')} />, eur(w.leftOut)]]} />}
        <More>{t('tax.withholding.help')}</More>
      </SectionCard>
      <SectionCard title={t('tax.withholding.title')} className="space-y-3">
        <p className="text-sm text-muted-foreground">{t('tax.withholding.where', { year: estimate.year })}</p>
        <Rows rows={declared} label={t('tax.withholding.declared')} />
      </SectionCard>
      {cut ? (
        <SectionCard title={t('tax.withholding.cut.title')} className="space-y-3">
          <p className="text-sm text-muted-foreground">{t('tax.withholding.cut.intro')}</p>
          <StatLine items={[
            { label: t('tax.withholding.cut.rate'), value: percentText(t, cut.rate), tip: tipText(t, 'expected_rate') },
            ...cut.declarants.map((d) => ({ label: t('tax.withholding.cut.monthly', { name: d.name }), value: eur(d.monthly) })),
            ...(ZERO.test(w.rentalDeclared) ? [] : [{ label: t('tax.withholding.cut.rental_monthly'), value: eur(cut.rentalMonthly), tip: tipText(t, 'rental_instalment') }]),
            { label: t('tax.withholding.cut.less'), value: eur(cut.less) },
            { label: t('tax.withholding.cut.balance'), value: gapText(t, cut.balance, estimate.year) },
          ]} />
          <Rows rows={cut.declarants.flatMap((d): [string, string][] => [
            [t('tax.withholding.cut.salaries', { name: d.name }), eur(d.salaries)],
            ...(ZERO.test(d.pensions) ? [] : [[t('tax.withholding.cut.pensions', { name: d.name }), eur(d.pensions)] as [string, string]]),
          ])} />
          <More>{t('tax.withholding.cut.help')}</More>
        </SectionCard>
      ) : null}
    </div>
  );
}
