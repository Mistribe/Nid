import type { TFunction } from 'i18next';
import { useTranslation } from 'react-i18next';
import { Bar, BarChart, Cell, LabelList, ReferenceLine, XAxis, YAxis } from 'recharts';
import { useApi } from '@/api/ApiProvider';
import { PERSONS } from '@/api/persons';
import { SectionCard } from '@/components/page/SectionCard';
import { ChartContainer, ChartTooltip, type ChartConfig } from '@/components/ui/chart';
import { Table, TableBody, TableCell, TableFooter, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { TAX_CHANGES } from '@/api/tax';
import { directionOf } from '@/lib/amount';
import { toPlot } from '@/lib/plot';
import { partsText, tipText, ZERO } from './format';
import { eur, Term } from './ResultTable';
import '@/locales/tax';

const LINES = ['salaries', 'rental_income', 'rental_deficit', 'per_deduction', 'shares', 'decote', 'pinel', 'donations', 'childcare', 'home_employment', 'flat_tax'] as const;
/** The InfoTip that explains a line (the salaries need none). */
const TIPS: Partial<Record<(typeof LINES)[number], Parameters<typeof tipText>[1]>> = {
  rental_income: 'rental', rental_deficit: 'rental_deficit', per_deduction: 'per_deduction', shares: 'parts', decote: 'decote', pinel: 'pinel',
  donations: 'donations', childcare: 'childcare', home_employment: 'home_employment', flat_tax: 'flat_tax',
};

/** The effect of a line on the tax, signed: a rise reads « +1 200,00 € ». */
const signed = (amount: string) => (amount.startsWith('-') || ZERO.test(amount) ? eur(amount) : `+${eur(amount)}`);

type Persons = { id: string; displayName: string }[];
type Point = { label: string; plot: number; effect: string };
const AXIS = new Intl.NumberFormat('fr-FR', { style: 'currency', currency: 'EUR', notation: 'compact' });
const FILLS = { up: 'var(--status-critical)', down: 'var(--status-good)', flat: 'var(--muted-foreground)' };

/** A line's name as text (chart axis, tooltip); a person is told apart by id, two adults may share a display name. */
function lineTitle(t: TFunction, code: string, person: string | null | undefined, persons: Persons): string {
  if (code === 'salaries') return t('tax.changes.line.salaries', { name: persons.find((p) => p.id === person)?.displayName ?? '' });
  const known = LINES.find((l) => l === code);
  return known ? t(`tax.changes.line.${known}`) : code;
}

function ChangesTooltip({ active, payload }: { active?: boolean; payload?: ReadonlyArray<{ payload?: Point }> }) {
  const { t } = useTranslation();
  const row = payload?.[0]?.payload;
  if (!active || !row) return null;
  const dir = directionOf(row.effect);
  return (
    <div className="grid gap-1 rounded-xl border bg-background px-3 py-2 text-xs shadow-md">
      <div className="font-medium">{row.label}</div>
      <div className="tabular-nums">{signed(row.effect)}{dir === 'flat' ? '' : ` (${t(dir === 'up' ? 'tax.changes.rise' : 'tax.changes.cut')})`}</div>
    </div>
  );
}

/**
 * Issue #410: « Ce qui a changé depuis {année} », each line of the calculation that moved, last year's figure (the notice's when
 * it prints it) against this year's estimate and its effect on the tax, sorted by the server. Every figure is the server's.
 */
export function ChangesSection({ year, scope }: { year: string; scope: 'me' | 'household' }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const persons = $api.useQuery('get', PERSONS).data ?? [];
  const changes = $api.useQuery('get', TAX_CHANGES, { params: { query: { year: Number(year), scope } } }).data;
  if (!changes?.taxBefore && !changes?.lines.length) return null;
  const since = String(Number(year) - 1);
  const label = (code: string, person?: string | null) => {
    if (code === 'salaries') return lineTitle(t, code, person, persons);
    const known = LINES.find((l) => l === code);
    if (!known) return code;
    const tip = TIPS[known];
    return <Term title={t(`tax.changes.line.${known}`)} tip={tip ? tipText(t, tip) : undefined} />;
  };
  const points: Point[] = changes.lines.map((l) => ({ label: lineTitle(t, l.code, l.person, persons), plot: toPlot(l.effect), effect: l.effect }));
  const value = (code: string, amount: string) => (code === 'shares' ? partsText(amount) : eur(amount));
  return (
    <SectionCard title={t('tax.changes.title', { year: since })} note={t('tax.changes.intro', { year })} className="space-y-3">
      {changes.lines.length ? <>
        <figure aria-label={t('tax.changes.title', { year: since })}>
          <ChartContainer config={{} satisfies ChartConfig} className="aspect-auto w-full" style={{ height: changes.lines.length * 48 + 40 }}>
            <BarChart data={points} layout="vertical" title={t('tax.changes.title', { year: since })} accessibilityLayer margin={{ left: 8, right: 8 }}>
              <XAxis type="number" domain={['auto', 'auto']} minTickGap={24} tickLine={false} axisLine={false} tickFormatter={(n: number) => AXIS.format(n)} />
              <YAxis type="category" dataKey="label" hide />
              <ReferenceLine x={0} stroke="var(--border)" />
              <ChartTooltip cursor={false} content={<ChangesTooltip />} />
              <Bar dataKey="plot" maxBarSize={24} animationDuration={800}>
                {points.map((p, i) => <Cell key={i} fill={FILLS[directionOf(p.effect)]} radius={(p.plot < 0 ? [4, 0, 0, 4] : [0, 4, 4, 0]) as unknown as number} />)}
                {/* The name and the server's signed effect sit above the bar (as on the withholding tab): no legend, and the word keeps colour from being alone. */}
                <LabelList dataKey="label" content={({ x, y, width, index }) => {
                  const p = points[Number(index)]!;
                  const dir = directionOf(p.effect);
                  const word = dir === 'flat' ? '' : ` (${t(dir === 'up' ? 'tax.changes.rise' : 'tax.changes.cut')})`;
                  return <text x={Math.min(Number(x), Number(x) + Number(width))} y={Number(y) - 6} className="fill-foreground text-xs">{`${p.label} : ${signed(p.effect)}${word}`}</text>;
                }} />
              </Bar>
            </BarChart>
          </ChartContainer>
        </figure>
        <Table aria-label={t('tax.changes.title', { year: since })}>
          <TableHeader>
            <TableRow>
              <TableHead>{t('tax.changes.line_head')}</TableHead>
              <TableHead className="text-right">{since}</TableHead>
              <TableHead className="text-right">{year}</TableHead>
              <TableHead className="text-right">{t('tax.changes.effect')}</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {changes.lines.map((l) => (
              <TableRow key={`${l.code}-${l.person ?? ''}`}>
                <TableCell>{label(l.code, l.person)}</TableCell>
                <TableCell className="text-right tabular-nums">
                  {value(l.code, l.before)}
                  {l.fromNotice ? <div className="text-xs text-muted-foreground">{t('tax.changes.from_notice')}</div> : null}
                </TableCell>
                <TableCell className="text-right tabular-nums">{value(l.code, l.now)}</TableCell>
                <TableCell className="text-right tabular-nums font-medium">{signed(l.effect)}</TableCell>
              </TableRow>
            ))}
          </TableBody>
          {changes.taxBefore && changes.taxNow ? (
            <TableFooter>
              {changes.remainder && !ZERO.test(changes.remainder) ? (
                <TableRow>
                  <TableCell colSpan={3}>{t('tax.changes.remainder')}</TableCell>
                  <TableCell className="text-right tabular-nums">{signed(changes.remainder)}</TableCell>
                </TableRow>
              ) : null}
              <TableRow>
                <TableCell>{t('tax.changes.tax')}</TableCell>
                <TableCell className="text-right tabular-nums">
                  {eur(changes.taxBefore)}
                  {changes.taxBeforeFromNotice ? <div className="text-xs font-normal text-muted-foreground">{t('tax.changes.from_notice')}</div> : null}
                </TableCell>
                <TableCell className="text-right tabular-nums">{eur(changes.taxNow)}</TableCell>
                <TableCell />
              </TableRow>
            </TableFooter>
          ) : null}
        </Table>
      </> : <p className="text-sm text-muted-foreground">{t('tax.changes.none')}</p>}
    </SectionCard>
  );
}
