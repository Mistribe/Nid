import { Fragment, useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { LocalDate } from '@/components/LocalDate';
import { formatMoney } from '@/components/Money';
import { Button } from '@/components/ui/button';
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from '@/components/ui/collapsible';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import type { TaxEstimate, TaxResult } from '@/api/tax';
import { METHODS, ORIGINS, partsText, percentText, regimeText, ZERO } from './format';
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
      money(t('tax.row.deficit_carried'), (r) => r.rentalDeficitCarriedUsed),
      { title: t('tax.row.rental'), cell: (r) => t('tax.row.rental_value', { amount: eur(r.rentalTaxable), regime: regimeText(t, r.rentalRegime) }), amount: (r) => r.rentalTaxable },
      money(t('tax.row.deficit'), (r) => r.rentalDeficit),
      money(t('tax.row.net_taxable'), (r) => r.netTaxableIncome, true),
      money(t('tax.row.reference_income'), (r) => r.referenceIncome),
    ] },
    { title: t('tax.group.income_tax'), rows: [
      { title: t('tax.row.parts'), cell: (r) => partsText(r.parts) },
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
    <div className="space-y-2">
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
      <p className="text-xs text-muted-foreground">{t('tax.row.reference_income_note')}</p>
      {folded ? (
        <Button variant="link" className="h-auto p-0" aria-expanded={showZeros} onClick={() => setShowZeros(!showZeros)}>
          {showZeros ? t('tax.zeros.hide') : t('tax.zeros.show', { count: folded })}
        </Button>
      ) : null}
    </div>
  );
}

type SalaryDetail = TaxEstimate['salaryDetail'];

/** « saisi », « estimé » or « mixte »: where a person's taxable salary comes from; a method the server added later reads as itself. */
function MethodPill({ method }: { method: string }) {
  const { t } = useTranslation();
  const known = METHODS.find((m) => m === method);
  return <span className="ml-2 rounded-full border px-2 py-0.5 text-xs font-normal text-muted-foreground">{known ? t(`tax.declarant.method.${known}`) : method}</span>;
}

/** The payments counted for one person, by source, each with its date, note, amount and whether it was typed or estimated. */
function Payments({ name, payments }: { name: string; payments: SalaryDetail }) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const sources = [...new Set(payments.map((p) => p.item))];
  return (
    <Collapsible open={open} onOpenChange={setOpen} className="text-left font-normal">
      <CollapsibleTrigger asChild>
        <Button variant="link" className="h-auto p-0 text-xs">{open ? t('tax.declarant.hide_payments') : t('tax.declarant.show_payments', { count: payments.length })}</Button>
      </CollapsibleTrigger>
      <CollapsibleContent>
        <div role="group" aria-label={t('tax.declarant.payments', { name })} className="mt-1 space-y-2 text-xs">
          {sources.map((item) => (
            <div key={item}>
              <p className="font-medium">{payments.find((p) => p.item === item)!.name}</p>
              <ul className="space-y-0.5">
                {payments.filter((p) => p.item === item).map((p, n) => {
                  const origin = ORIGINS.find((o) => o === p.category);
                  return (
                    <li key={`${n}-${p.on}`}>
                      <LocalDate value={p.on} />{p.note ? <> · {p.note}</> : null} · {eur(p.amount)} · {origin ? t(`tax.declarant.origin.${origin}`) : p.category}
                    </li>
                  );
                })}
              </ul>
            </div>
          ))}
        </div>
      </CollapsibleContent>
    </Collapsible>
  );
}

/**
 * Each declarant's salary steps side by side, one column per person; a line at 0 € for everyone is left out. `children`
 * are more rows in the same columns (the PER's), after the salaries.
 */
export function DeclarantTable({ result, detail = [], adults = [], children }: {
  result: TaxResult; detail?: SalaryDetail; adults?: TaxEstimate['adults']; children?: ReactNode;
}) {
  const { t } = useTranslation();
  // `origin`: the line that says where the salaries come from (the method, the payments behind it).
  const rows: { title: string; amount: (d: TaxResult['declarants'][number]) => string; strong?: boolean; origin?: boolean }[] = [
    { title: t('tax.declarant.salary_net'), amount: (d) => d.netTaxableIncome, origin: true },
    { title: t('tax.declarant.allowance'), amount: (d) => d.allowance },
    { title: t('tax.declarant.per'), amount: (d) => d.perDeducted },
    { title: t('tax.declarant.taxable'), amount: (d) => d.taxable, strong: true },
  ];
  // A column is a person by id: two adults may share a display name.
  const columnKey = (d: TaxResult['declarants'][number], i: number) => d.person ?? `${i}`;
  const paymentsOf = (d: TaxResult['declarants'][number]) => (d.person && adults.some((a) => a.id === d.person) ? detail.filter((p) => p.person === d.person) : []);
  return (
    <div>
      <Table aria-label={t('tax.declarant.title')}>
        <TableHeader>
          <TableRow>
            <TableHead>{t('tax.declarant.info')}</TableHead>
            {result.declarants.map((d, i) => <TableHead key={columnKey(d, i)} className="text-right">{d.name}</TableHead>)}
          </TableRow>
        </TableHeader>
        <TableBody>
          <TableRow className="hover:bg-transparent">
            <TableHead colSpan={result.declarants.length + 1} scope="colgroup" className="pt-6 text-xs font-semibold uppercase tracking-wide text-muted-foreground">{t('tax.declarant.salaries')}</TableHead>
          </TableRow>
          {rows.filter(({ amount, strong }) => strong || !result.declarants.every((d) => ZERO.test(amount(d)))).map(({ title, amount, strong, origin }) => (
            <TableRow key={title} className={strong ? 'font-semibold' : undefined}>
              <TableHead scope="row" className={strong ? 'font-semibold' : 'font-normal'}>{title}</TableHead>
              {result.declarants.map((d, i) => {
                const payments = origin ? paymentsOf(d) : [];
                return (
                  <TableCell key={columnKey(d, i)} className="text-right tabular-nums">
                    {eur(amount(d))}{origin && d.method ? <MethodPill method={d.method} /> : null}
                    {payments.length ? <Payments name={d.name} payments={payments} /> : null}
                  </TableCell>
                );
              })}
            </TableRow>
          ))}
          {children}
        </TableBody>
      </Table>
    </div>
  );
}
