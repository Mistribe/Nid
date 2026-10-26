import type { TFunction } from 'i18next';
import { useTranslation } from 'react-i18next';
import { formatCurrency } from '@/components/Money';
import { Section } from '@/components/page/Section';
import { Button } from '@/components/ui/button';
import { Table, TableBody, TableCell, TableHead, TableRow } from '@/components/ui/table';
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

/**
 * Tax spec §4.5: the PER of each declarant (ceiling from the notice or estimated, contributions, deducted, left to pay before
 * December 31st), then the household's tax saved and room in the bracket. Every figure is the server's; `names[i]` is the
 * envelopes the i-th declarant's contributions are read from, and `onProfile` opens the tax profile where the ceiling is typed.
 */
export function PerSection({ result, names, year, rulesYear, onProfile }: {
  result: TaxResult; names: string[]; year: number; rulesYear: number; onProfile?: () => void;
}) {
  const { t } = useTranslation();
  const people = result.declarants.map((d, i) => ({ d, per: d.per, flows: names[i] ?? '', key: d.person ?? `${i}` }));
  const shown = people.filter((p) => p.per);
  if (!shown.length && !result.per) return null;
  const household = result.per;
  const over = overCeiling(result);
  const estimated = estimatedCeiling(result);
  return (
    <Section title={t('tax.per.title')}>
      <p className="text-sm text-muted-foreground">{t('tax.per.scale', { rulesYear })}</p>
      {over.length || estimated.length ? (
        <ul className="list-disc space-y-1 pl-5 text-sm">
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
      <div className="grid max-w-3xl gap-6 sm:grid-cols-2">
        {shown.map(({ d, per, flows, key }) => {
          const lines: { label: string; value: string; note?: string; strong?: boolean }[] = [
            { label: t(per!.source === 'notice' ? 'tax.per.ceiling_notice' : 'tax.per.ceiling_estimated'), value: eur(per!.ceiling),
              note: ZERO.test(per!.expiring) ? undefined : t('tax.per.expiring', { amount: eur(per!.expiring) }) },
            { label: t('tax.per.contributions'), value: eur(per!.contributions), note: flows ? t('tax.per.flows', { names: flows }) : undefined },
            { label: t('tax.per.deducted'), value: eur(per!.deducted) },
            ...(ZERO.test(per!.excess) ? [] : [{ label: t('tax.per.excess'), value: eur(per!.excess) }]),
            { label: t('tax.per.remaining'), value: eur(per!.remaining), strong: true },
          ];
          return (
            <div key={key} role="group" aria-label={d.name}>
              <h3 className="mb-1 font-medium">{d.name}</h3>
              <Table>
                <TableBody>{lines.map((l) => <PerRow key={l.label} {...l} />)}</TableBody>
              </Table>
            </div>
          );
        })}
        {household?.pooled ? (
          <div role="group" aria-label={t('tax.per.household')}>
            <h3 className="mb-1 font-medium">{t('tax.per.household')}</h3>
            <Table><TableBody><PerRow label={t('tax.per.remaining')} value={eur(household.remaining)} strong /></TableBody></Table>
          </div>
        ) : null}
      </div>
      {household ? (
        <dl className="space-y-1 text-sm">
          <div className="flex gap-1.5"><dt className="text-muted-foreground">{t('tax.per.tax_saved')}</dt><dd className="font-medium tabular-nums">{eur(household.taxSaved)}</dd></div>
          {ZERO.test(household.roomInBracket) ? null : (
            <div><dd className="text-muted-foreground">{t('tax.per.room', { room: wholeEur(household.roomInBracket), rate: percentText(t, result.marginalRate) })}</dd></div>
          )}
        </dl>
      ) : null}
    </Section>
  );
}

function PerRow({ label, value, note, strong }: { label: string; value: string; note?: string; strong?: boolean }) {
  return (
    <TableRow className={strong ? 'font-semibold' : undefined}>
      <TableHead scope="row" className={strong ? 'font-semibold' : 'font-normal'}>
        {label}{note ? <span className="block text-xs font-normal text-muted-foreground">{note}</span> : null}
      </TableHead>
      <TableCell className="text-right tabular-nums">{value}</TableCell>
    </TableRow>
  );
}
