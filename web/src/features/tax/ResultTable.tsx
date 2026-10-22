import { Fragment, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { formatMoney } from '@/components/Money';
import { Button } from '@/components/ui/button';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import type { TaxResult } from './api';
import { percentText, regimeText, ZERO } from './format';
import '@/locales/tax';

export const eur = (amount: string) => formatMoney({ amount, currency: 'EUR' });

/** A step: its title, the figure shown, a subtotal in bold, and the raw amount when a zero in every column may be folded. */
type Row = { title: string; cell: (r: TaxResult) => string; strong?: boolean; amount?: (r: TaxResult) => string };

/**
 * Spec §4, step by step in four groups: one column per result (the estimate, then the simulation), every figure the
 * server's. A step at 0 € in every column is folded behind one button.
 */
export function ResultTable({ columns, label, declarants = true }: {
  columns: { title: string; result: TaxResult }[]; label: string; /** False when a DeclarantTable shows each person's lines. */ declarants?: boolean;
}) {
  const { t } = useTranslation();
  const [showZeros, setShowZeros] = useState(false);
  const first = columns[0]!.result;
  const money = (title: string, amount: (r: TaxResult) => string, strong?: boolean): Row => ({ title, cell: (r) => eur(amount(r)), strong, amount });
  const groups: { title: string; rows: Row[] }[] = [
    { title: t('tax.group.income'), rows: [
      ...(declarants ? first.declarants : []).flatMap((d, i) => [
        money(t('tax.row.salary_net', { name: d.name }), (r) => r.declarants[i]?.netTaxableIncome ?? '0'),
        money(t('tax.row.allowance', { name: d.name }), (r) => r.declarants[i]?.allowance ?? '0'),
        money(t('tax.row.per', { name: d.name }), (r) => r.declarants[i]?.perDeducted ?? '0'),
      ]),
      { title: t('tax.row.rental'), cell: (r) => t('tax.row.rental_value', { amount: eur(r.rentalTaxable), regime: regimeText(t, r.rentalRegime) }), amount: (r) => r.rentalTaxable },
      money(t('tax.row.deficit'), (r) => r.rentalDeficit),
      money(t('tax.row.net_taxable'), (r) => r.netTaxableIncome, true),
    ] },
    { title: t('tax.group.income_tax'), rows: [
      { title: t('tax.row.parts'), cell: (r) => r.parts },
      { title: t('tax.row.gross_tax'), cell: (r) => (r.familyCapApplied ? t('tax.row.capped', { amount: eur(r.grossTax) }) : eur(r.grossTax)) },
      money(t('tax.row.decote'), (r) => r.decote),
      money(t('tax.row.pinel'), (r) => r.pinelReduction),
      money(t('tax.row.donations'), (r) => r.donationReduction),
      money(t('tax.row.childcare'), (r) => r.childcareCredit),
      money(t('tax.row.home_employment'), (r) => r.homeEmploymentCredit),
      money(t('tax.row.niche_cap'), (r) => r.nicheCapExcess),
      money(t('tax.row.income_tax'), (r) => r.incomeTax, true),
    ] },
    { title: t('tax.group.other'), rows: [
      money(t('tax.row.flat_tax'), (r) => r.capitalFlatTax),
      money(t('tax.row.social_levies'), (r) => r.socialLevies),
      money(t('tax.row.total_tax'), (r) => r.totalTax, true),
    ] },
    { title: t('tax.group.left'), rows: [
      money(t('tax.row.net_before'), (r) => r.netBeforeTax),
      money(t('tax.row.net_after'), (r) => r.netAfterTax, true),
      money(t('tax.row.net_after_monthly'), (r) => r.netAfterTaxMonthly),
      { title: t('tax.row.marginal_rate'), cell: (r) => percentText(t, r.marginalRate) },
      { title: t('tax.row.withholding_rate'), cell: (r) => percentText(t, r.withholding.rate) },
      { title: t('tax.row.withholding_balance'), cell: (r) => eur(r.withholding.balance) },
    ] },
  ];
  const zero = (row: Row) => !row.strong && row.amount !== undefined && columns.every((c) => ZERO.test(row.amount!(c.result)));
  const folded = groups.flatMap((g) => g.rows).filter(zero).length;

  return (
    <div className="max-w-3xl space-y-2">
      <Table aria-label={label}>
        <TableHeader>
          <TableRow>
            <TableHead>{t('tax.row.step')}</TableHead>
            {columns.map((c) => <TableHead key={c.title} className="text-right">{c.title}</TableHead>)}
          </TableRow>
        </TableHeader>
        <TableBody>
          {groups.map((g) => (
            <Fragment key={g.title}>
              <TableRow className="hover:bg-transparent">
                <TableHead colSpan={columns.length + 1} className="pt-6 text-xs font-semibold uppercase tracking-wide text-muted-foreground">{g.title}</TableHead>
              </TableRow>
              {g.rows.filter((row) => showZeros || !zero(row)).map(({ title, cell, strong }) => (
                <TableRow key={title} className={strong ? 'font-semibold' : undefined}>
                  <TableHead scope="row" className={strong ? 'font-semibold' : 'font-normal'}>{title}</TableHead>
                  {columns.map((c) => <TableCell key={c.title} className="text-right tabular-nums">{cell(c.result)}</TableCell>)}
                </TableRow>
              ))}
            </Fragment>
          ))}
        </TableBody>
      </Table>
      {folded ? (
        <Button variant="link" className="h-auto p-0" aria-expanded={showZeros} onClick={() => setShowZeros(!showZeros)}>
          {showZeros ? t('tax.zeros.hide') : t('tax.zeros.show', { count: folded })}
        </Button>
      ) : null}
    </div>
  );
}

/** Each declarant's salary steps side by side, one column per person; a line at 0 € for everyone is left out. */
export function DeclarantTable({ result }: { result: TaxResult }) {
  const { t } = useTranslation();
  const rows: [string, (d: TaxResult['declarants'][number]) => string, boolean?][] = [
    [t('tax.declarant.salary_net'), (d) => d.netTaxableIncome],
    [t('tax.declarant.allowance'), (d) => d.allowance],
    [t('tax.declarant.per'), (d) => d.perDeducted],
    [t('tax.declarant.taxable'), (d) => d.taxable, true],
  ];
  return (
    <div className="max-w-3xl">
      <Table aria-label={t('tax.declarant.title')}>
        <TableHeader>
          <TableRow>
            <TableHead>{t('tax.declarant.info')}</TableHead>
            {result.declarants.map((d) => <TableHead key={d.name} className="text-right">{d.name}</TableHead>)}
          </TableRow>
        </TableHeader>
        <TableBody>
          {rows.filter(([, amount, strong]) => strong || !result.declarants.every((d) => ZERO.test(amount(d)))).map(([title, amount, strong]) => (
            <TableRow key={title} className={strong ? 'font-semibold' : undefined}>
              <TableHead scope="row" className={strong ? 'font-semibold' : 'font-normal'}>{title}</TableHead>
              {result.declarants.map((d) => <TableCell key={d.name} className="text-right tabular-nums">{eur(amount(d))}</TableCell>)}
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </div>
  );
}
