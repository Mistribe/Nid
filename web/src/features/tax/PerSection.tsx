import type { TFunction } from 'i18next';
import { useTranslation } from 'react-i18next';
import { formatCurrency } from '@/components/Money';
import { Button } from '@/components/ui/button';
import { TableCell, TableHead, TableRow } from '@/components/ui/table';
import type { TaxResult } from './api';
import { percentText, ZERO } from './format';
import { eur } from './ResultTable';
import '@/locales/tax';

/** Whole euros: the room in the bracket is floored by the server. */
const wholeEur = (amount: string) => formatCurrency(amount, 'EUR', { maximumFractionDigits: 0 });

type Declarant = TaxResult['declarants'][number];
const overCeiling = (result: TaxResult) => (result.warnings.includes('per_over_ceiling') ? result.declarants.filter((d) => d.per && !ZERO.test(d.per.excess)) : []);
const estimatedCeiling = (result: TaxResult) => (result.warnings.includes('per_ceiling_estimated')
  ? result.declarants.filter((d) => d.per?.source === 'estimated' && !ZERO.test(d.per.contributions)) : []);
const overText = (t: TFunction, d: Declarant) => t('tax.per.over_ceiling', { name: d.name, excess: eur(d.per!.excess), ceiling: eur(d.per!.ceiling) });
const estimatedText = (t: TFunction, d: Declarant, year: number) => t('tax.per.ceiling_estimated_warning', { name: d.name, year: year - 1 });

/** The PER warning codes the PER block itself tells, as it can only tell what the declarants' own figures show. */
export function perWarningsInBlock(result: TaxResult): Set<string> {
  const codes = new Set<string>();
  if (overCeiling(result).length) codes.add('per_over_ceiling');
  if (estimatedCeiling(result).length) codes.add('per_ceiling_estimated');
  return codes;
}

/** One PER warning as sentences (one per declarant), or the generic notice when no declarant's figures can tell it. */
export function perWarningTexts(t: TFunction, result: TaxResult, code: string, year: number): string[] {
  if (code === 'per_over_ceiling') {
    const named = overCeiling(result);
    return named.length ? named.map((d) => overText(t, d)) : [t('tax.warning.per_over_ceiling')];
  }
  const named = estimatedCeiling(result);
  return named.length ? named.map((d) => estimatedText(t, d, year)) : [t('tax.warning.per_ceiling_estimated')];
}

/** The ceiling's source when every declarant shown shares it, else none: each cell then tells its own. */
const sharedSource = (per: NonNullable<Declarant['per']>[]) => (per.every((p) => p.source === per[0]!.source) ? per[0]!.source : undefined);
const ceilingLabel = (t: TFunction, source: string | undefined) => (source === undefined ? t('tax.per.ceiling') : t(source === 'notice' ? 'tax.per.ceiling_notice' : 'tax.per.ceiling_estimated'));

/**
 * Tax spec §4.5, the declarants' part: each one's PER (ceiling from the notice or estimated, contributions, deducted, the minimum
 * that keeps the expiring ceiling, left to pay before December 31st) as rows of the `DeclarantTable`, so the figures sit in the same column as the person's income.
 * Every figure is the server's; `names[i]` is the envelopes the i-th declarant's contributions are read from.
 */
export function PerRows({ result, names }: { result: TaxResult; names: string[] }) {
  const { t } = useTranslation();
  const shown = result.declarants.map((d) => d.per).filter((per) => per !== null && per !== undefined);
  if (!shown.length) return null;
  const source = sharedSource(shown);
  type Line = { label: string; cell: (per: NonNullable<Declarant['per']>, i: number) => string; note?: (per: NonNullable<Declarant['per']>, i: number) => string | undefined; strong?: boolean };
  const lines: Line[] = [
    { label: ceilingLabel(t, source), cell: (per) => eur(per.ceiling),
      note: (per) => [source === undefined ? t(per.source === 'notice' ? 'tax.per.source_notice' : 'tax.per.source_estimated') : undefined,
        // Once the minimum is paid, nothing is lost any more.
        ZERO.test(per.minimum) ? undefined : t('tax.per.expiring', { amount: eur(per.expiring) })].filter(Boolean).join(' · ') || undefined },
    { label: t('tax.per.contributions'), cell: (per) => eur(per.contributions), note: (_, i) => (names[i] ? t('tax.per.flows', { names: names[i] }) : undefined) },
    { label: t('tax.per.deducted'), cell: (per) => eur(per.deducted) },
    ...(shown.every((per) => ZERO.test(per.excess)) ? [] : [{ label: t('tax.per.excess'), cell: (per: NonNullable<Declarant['per']>) => eur(per.excess) }]),
    // While a declarant has a ceiling expiring: the year's own ceiling goes first, then the expiring part.
    ...(shown.every((per) => ZERO.test(per.expiring)) ? [] : [{ label: t('tax.per.minimum'), cell: (per: NonNullable<Declarant['per']>) => eur(per.minimum),
      note: (per: NonNullable<Declarant['per']>) => (ZERO.test(per.minimum) ? undefined
        : [t('tax.per.minimum_keeps', { amount: eur(per.expiring) }), per.minimumEstimated && per.source === 'notice' ? t('tax.per.minimum_estimated') : undefined].filter(Boolean).join(' · ')),
      strong: true }]),
    { label: t('tax.per.remaining'), cell: (per) => eur(per.remaining), strong: true },
  ];
  return (
    <>
      <TableRow className="hover:bg-transparent">
        <TableHead colSpan={result.declarants.length + 1} scope="colgroup" className="pt-6 text-xs font-semibold uppercase tracking-wide text-muted-foreground">
          {t('tax.per.title')} · {t('tax.per.scale', { rulesYear: result.rulesYear })}
        </TableHead>
      </TableRow>
      {lines.map(({ label, cell, note, strong }) => (
        <TableRow key={label} className={strong ? 'font-semibold' : undefined}>
          <TableHead scope="row" className={strong ? 'font-semibold' : 'font-normal'}>{label}</TableHead>
          {result.declarants.map((d, i) => {
            const text = d.per && note ? note(d.per, i) : undefined;
            return (
              <TableCell key={d.person ?? i} className="text-right tabular-nums">
                {d.per ? cell(d.per, i) : '—'}
                {text ? <span className="block text-xs font-normal text-muted-foreground">{text}</span> : null}
              </TableCell>
            );
          })}
        </TableRow>
      ))}
    </>
  );
}

/**
 * Tax spec §4.5, the household's part, under the declarants' table: the PER warnings the figures tell, the pooled ceiling's
 * remainder, the tax saved and the room in the bracket. `onProfile` opens the tax profile where the ceiling is typed.
 */
export function PerSummary({ result, year, onProfile }: { result: TaxResult; year: number; onProfile?: () => void }) {
  const { t } = useTranslation();
  const household = result.per;
  const over = overCeiling(result);
  const estimated = estimatedCeiling(result);
  if (!household && !over.length && !estimated.length) return null;
  return (
    <div className="max-w-3xl space-y-2 text-sm">
      {over.length || estimated.length ? (
        <ul className="list-disc space-y-1 pl-5">
          {over.map((d, i) => (
            <li key={`over-${d.person ?? i}`}>
              <span>{overText(t, d)}</span>
              {/* A ceiling typed from the notice is the right one: the way to the profile only helps an estimated one. */}
              {d.per?.source === 'estimated' ? ' ' : null}
              {d.per?.source !== 'estimated' ? null : onProfile ? <Button variant="link" className="h-auto p-0 text-sm" onClick={onProfile}>{t('tax.per.enter_ceiling')}</Button> : t('tax.per.enter_ceiling')}
            </li>
          ))}
          {estimated.map((d, i) => <li key={`est-${d.person ?? i}`}>{estimatedText(t, d, year)}</li>)}
        </ul>
      ) : null}
      {household ? (
        <dl className="flex flex-wrap gap-x-6 gap-y-1">
          {household.pooled ? (
            <div className="flex gap-1.5"><dt className="text-muted-foreground">{t('tax.per.household')}</dt><dd className="font-medium tabular-nums">{t('tax.per.household_remaining', { amount: eur(household.remaining) })}</dd></div>
          ) : null}
          <div className="flex gap-1.5"><dt className="text-muted-foreground">{t('tax.per.tax_saved')}</dt><dd className="font-medium tabular-nums">{eur(household.taxSaved)}</dd></div>
          {ZERO.test(household.roomInBracket) ? null : (
            <div><dd className="text-muted-foreground">{t('tax.per.room', { room: wholeEur(household.roomInBracket), rate: percentText(t, result.marginalRate) })}</dd></div>
          )}
        </dl>
      ) : null}
    </div>
  );
}
