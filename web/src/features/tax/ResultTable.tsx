import { Fragment, useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { InfoTip } from '@/components/InfoTip';
import { LocalDate } from '@/components/LocalDate';
import { formatCurrency, formatMoney } from '@/components/Money';
import { Button } from '@/components/ui/button';
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from '@/components/ui/collapsible';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import type { TFunction } from 'i18next';
import type { TaxEstimate, TaxResult } from '@/api/tax';
import { directionOf } from '@/lib/amount';
import { balanceWords } from './balance';
import { METHODS, ORIGINS, partsText, percentText, regimeText, tipText, ZERO } from './format';
import '@/locales/tax';

export const eur = (amount: string) => formatMoney({ amount, currency: 'EUR' });
/** An amount without its sign: the direction is said in words. */
export const eurUnsigned = (amount: string) => formatCurrency(amount, 'EUR', { signDisplay: 'never' });
/** Whole euros: the room in the bracket is floored by the server. */
export const wholeEur = (amount: string) => formatCurrency(amount, 'EUR', { maximumFractionDigits: 0 });

/** A tax result: a negative one is « … en votre faveur », never a minus sign. */
export function resultText(t: TFunction, amount: string): string {
  return directionOf(amount) === 'down' ? t('tax.row.in_your_favour', { amount: eurUnsigned(amount) }) : eur(amount);
}

export type ResultGroup = 'income' | 'income_tax' | 'other' | 'left';
export const RESULT_GROUPS: readonly ResultGroup[] = ['income', 'income_tax', 'other', 'left'];
/** The server's figure that closes each group. */
export const GROUP_TOTAL: Record<ResultGroup, (r: TaxResult) => string> = {
  income: (r) => r.netTaxableIncome, income_tax: (r) => r.incomeTax, other: (r) => r.totalTax, left: (r) => r.netAfterTax,
};

/** A step: its title, the figure shown, a subtotal in bold, the raw amount when a zero in every column may be folded, and what the term means. */
type Row = { title: string; cell: (r: TaxResult) => string; strong?: boolean; amount?: (r: TaxResult) => string; tip?: string };

/** A row's title, with the InfoTip that explains it when it has one. */
export function Term({ title, tip }: { title: string; tip?: string }) {
  return tip ? <span className="inline-flex items-center gap-1.5">{title}<InfoTip label={title}>{tip}</InfoTip></span> : <>{title}</>;
}

/**
 * Spec §4, step by step in four groups: one column per result (the estimate, then the simulation), every figure the
 * server's. A step at 0 € in every column is folded behind one button.
 */
export function ResultTable({ columns, label, declarants = true, groups: shown = RESULT_GROUPS }: {
  columns: { title: string; result: TaxResult }[]; label: string; /** False when a DeclarantTable shows each person's lines. */ declarants?: boolean;
  /** The groups to show; with one, its card says what it is and no heading row repeats it. */ groups?: readonly ResultGroup[];
}) {
  const { t } = useTranslation();
  const [showZeros, setShowZeros] = useState(false);
  const first = columns[0]!.result;
  const money = (title: string, amount: (r: TaxResult) => string, strong?: boolean, tip?: string): Row => ({ title, cell: (r) => eur(amount(r)), strong, amount, tip });
  const tip = (key: Parameters<typeof tipText>[1]) => tipText(t, key);
  const all: { id: ResultGroup; title: string; rows: Row[] }[] = [
    { id: 'income', title: t('tax.group.income'), rows: [
      ...(declarants ? first.declarants : []).flatMap((d, i) => [
        money(t('tax.row.salary_net', { name: d.name }), (r) => r.declarants[i]?.netTaxableIncome ?? '0'),
        money(t('tax.row.allowance', { name: d.name }), (r) => r.declarants[i]?.allowance ?? '0', false, tip('allowance')),
        money(t('tax.row.pensions', { name: d.name }), (r) => r.declarants[i]?.pensions ?? '0', false, tip('pensions')),
        money(t('tax.row.pension_allowance', { name: d.name }), (r) => r.declarants[i]?.pensionAllowance ?? '0', false, tip('pension_allowance')),
        money(t('tax.row.per', { name: d.name }), (r) => r.declarants[i]?.perDeducted ?? '0', false, tip('per_deduction')),
      ]),
      money(t('tax.row.deficit_carried'), (r) => r.rentalDeficitCarriedUsed, false, tip('deficit_carried')),
      { title: t('tax.row.rental'), cell: (r) => t('tax.row.rental_value', { amount: eur(r.rentalTaxable), regime: regimeText(t, r.rentalRegime) }), amount: (r) => r.rentalTaxable, tip: tip('rental') },
      money(t('tax.row.deficit'), (r) => r.rentalDeficit, false, tip('rental_deficit')),
      money(t('tax.row.deficit_forward'), (r) => r.rentalDeficitCarriedForward, false, tip('deficit_forward')),
      money(t('tax.row.dividend_allowance'), (r) => r.dividendAllowance, false, tip('dividend_allowance')),
      money(t('tax.row.alimony'), (r) => r.alimonyDeducted, false, tip('alimony')),
      money(t('tax.row.age_disability'), (r) => r.ageDisabilityAllowance, false, tip('age_disability')),
      money(t('tax.row.realised_gains'), (r) => r.realisedGainsTaxable, false, tip('realised_gains')),
      money(t('tax.row.losses_forward'), (r) => r.capitalLossesCarriedForward, false, tip('losses_forward')),
      money(t('tax.row.net_taxable'), (r) => r.netTaxableIncome, true, tip('net_taxable')),
      money(t('tax.row.reference_income'), (r) => r.referenceIncome, false, tip('reference_income')),
    ] },
    { id: 'income_tax', title: t('tax.group.income_tax'), rows: [
      { title: t('tax.row.parts'), cell: (r) => partsText(r.parts), tip: tip('parts') },
      { title: t('tax.row.gross_tax'), cell: (r) => (r.familyCapApplied ? t('tax.row.capped', { amount: eur(r.grossTax) }) : eur(r.grossTax)), tip: tip('gross_tax') },
      money(t('tax.row.decote'), (r) => r.decote, false, tip('decote')),
      money(t('tax.row.pinel'), (r) => r.pinelReduction, false, tip('pinel')),
      money(t('tax.row.donations'), (r) => r.donationReduction, false, tip('donations')),
      money(t('tax.row.childcare'), (r) => r.childcareCredit, false, tip('childcare')),
      money(t('tax.row.home_employment'), (r) => r.homeEmploymentCredit, false, tip('home_employment')),
      money(t('tax.row.niche_cap'), (r) => r.nicheCapExcess, false, tip('niche_cap')),
      { title: t('tax.row.income_tax'), cell: (r) => resultText(t, r.incomeTax), strong: true },
    ] },
    { id: 'other', title: t('tax.group.other'), rows: [
      money(t('tax.row.flat_tax'), (r) => r.capitalFlatTax, false, tip('flat_tax')),
      money(t('tax.row.social_levies'), (r) => r.socialLevies, false, tip('social_levies')),
      money(t('tax.row.high_income'), (r) => r.highIncomeContribution, false, tip('high_income')),
      { title: t('tax.row.total_tax'), cell: (r) => resultText(t, r.totalTax), strong: true },
    ] },
    { id: 'left', title: t('tax.group.left'), rows: [
      money(t('tax.row.net_before'), (r) => r.netBeforeTax),
      money(t('tax.row.net_after'), (r) => r.netAfterTax, true),
      money(t('tax.row.net_after_monthly'), (r) => r.netAfterTaxMonthly),
      { title: t('tax.row.marginal_rate'), cell: (r) => percentText(t, r.marginalRate), tip: tip('marginal_rate') },
      { title: t('tax.row.withholding_rate'), cell: (r) => percentText(t, r.withholding.rate), tip: tip('withholding_rate') },
      { title: t('tax.row.withholding_balance'), tip: tip('balance'), cell: (r) => {
        const b = r.withholding.balance, words = balanceWords(t, b, r.year);
        return ZERO.test(b) ? words : t('tax.row.balance_value', { amount: eurUnsigned(b), words });
      } },
    ] },
  ];
  const groups = all.filter((g) => shown.includes(g.id));
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
            <Fragment key={g.id}>
              {groups.length > 1 ? (
                <TableRow className="hover:bg-transparent">
                  <TableHead colSpan={columns.length + 1} className="pt-6 text-xs font-semibold uppercase tracking-wide text-muted-foreground">{g.title}</TableHead>
                </TableRow>
              ) : null}
              {g.rows.filter((row) => showZeros || !zero(row)).map(({ title, cell, strong, tip: about }) => (
                <TableRow key={title} className={strong ? 'font-semibold' : undefined}>
                  <TableHead scope="row" className={strong ? 'font-semibold' : 'font-normal'}><Term title={title} tip={about} /></TableHead>
                  {columns.map((c) => <TableCell key={c.title} className="text-right tabular-nums">{cell(c.result)}</TableCell>)}
                </TableRow>
              ))}
            </Fragment>
          ))}
        </TableBody>
      </Table>
      {groups.some((g) => g.id === 'income') ? <p className="text-xs text-muted-foreground">{t('tax.row.reference_income_note')}</p> : null}
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
  const rows: { title: string; amount: (d: TaxResult['declarants'][number]) => string; strong?: boolean; origin?: boolean; tip?: string }[] = [
    { title: t('tax.declarant.salary_net'), amount: (d) => d.netTaxableIncome, origin: true },
    { title: t('tax.declarant.allowance'), amount: (d) => d.allowance, tip: tipText(t, 'allowance') },
    { title: t('tax.declarant.pensions'), amount: (d) => d.pensions, tip: tipText(t, 'pensions') },
    { title: t('tax.declarant.pension_allowance'), amount: (d) => d.pensionAllowance, tip: tipText(t, 'pension_allowance') },
    { title: t('tax.declarant.per'), amount: (d) => d.perDeducted, tip: tipText(t, 'per_deduction') },
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
            <TableHead><span className="sr-only">{t('tax.declarant.line')}</span></TableHead>
            {result.declarants.map((d, i) => <TableHead key={columnKey(d, i)} className="text-right">{d.name}</TableHead>)}
          </TableRow>
        </TableHeader>
        <TableBody>
          <TableRow className="hover:bg-transparent">
            <TableHead colSpan={result.declarants.length + 1} scope="colgroup" className="pt-6 text-xs font-semibold uppercase tracking-wide text-muted-foreground">{t('tax.declarant.salaries')}</TableHead>
          </TableRow>
          {rows.filter(({ amount, strong }) => strong || !result.declarants.every((d) => ZERO.test(amount(d)))).map(({ title, amount, strong, origin, tip }) => (
            <TableRow key={title} className={strong ? 'font-semibold' : undefined}>
              <TableHead scope="row" className={strong ? 'font-semibold' : 'font-normal'}><Term title={title} tip={tip} /></TableHead>
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
