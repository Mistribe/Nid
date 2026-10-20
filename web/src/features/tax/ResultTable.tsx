import { useTranslation } from 'react-i18next';
import { formatMoney } from '@/components/Money';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import type { TaxResult } from './api';
import { percentText, regimeText } from './format';

export const eur = (amount: string) => formatMoney({ amount, currency: 'EUR' });

/** Spec §4, step by step: one column per result (the estimate, then the simulation), every figure the server's. */
export function ResultTable({ columns, label }: { columns: { title: string; result: TaxResult }[]; label: string }) {
  const { t } = useTranslation();
  const first = columns[0]!.result;
  const rows: [string, (r: TaxResult) => string, boolean?][] = [
    ...first.declarants.flatMap((d, i): [string, (r: TaxResult) => string][] => [
      [t('tax.row.salary_net', { name: d.name }), (r) => eur(r.declarants[i]?.netTaxableIncome ?? '0')],
      [t('tax.row.allowance', { name: d.name }), (r) => eur(r.declarants[i]?.allowance ?? '0')],
      [t('tax.row.per', { name: d.name }), (r) => eur(r.declarants[i]?.perDeducted ?? '0')],
    ]),
    [t('tax.row.rental'), (r) => t('tax.row.rental_value', { amount: eur(r.rentalTaxable), regime: regimeText(t, r.rentalRegime) })],
    [t('tax.row.deficit'), (r) => eur(r.rentalDeficit)],
    [t('tax.row.net_taxable'), (r) => eur(r.netTaxableIncome), true],
    [t('tax.row.parts'), (r) => r.parts],
    [t('tax.row.gross_tax'), (r) => (r.familyCapApplied ? t('tax.row.capped', { amount: eur(r.grossTax) }) : eur(r.grossTax))],
    [t('tax.row.decote'), (r) => eur(r.decote)],
    [t('tax.row.pinel'), (r) => eur(r.pinelReduction)],
    [t('tax.row.donations'), (r) => eur(r.donationReduction)],
    [t('tax.row.childcare'), (r) => eur(r.childcareCredit)],
    [t('tax.row.home_employment'), (r) => eur(r.homeEmploymentCredit)],
    [t('tax.row.niche_cap'), (r) => eur(r.nicheCapExcess)],
    [t('tax.row.income_tax'), (r) => eur(r.incomeTax), true],
    [t('tax.row.flat_tax'), (r) => eur(r.capitalFlatTax)],
    [t('tax.row.social_levies'), (r) => eur(r.socialLevies)],
    [t('tax.row.total_tax'), (r) => eur(r.totalTax), true],
    [t('tax.row.net_before'), (r) => eur(r.netBeforeTax)],
    [t('tax.row.net_after'), (r) => eur(r.netAfterTax), true],
    [t('tax.row.net_after_monthly'), (r) => eur(r.netAfterTaxMonthly)],
    [t('tax.row.marginal_rate'), (r) => percentText(t, r.marginalRate)],
    [t('tax.row.withholding_rate'), (r) => percentText(t, r.withholding.rate)],
    [t('tax.row.withholding_balance'), (r) => eur(r.withholding.balance)],
  ];
  return (
    <Table aria-label={label}>
      <TableHeader>
        <TableRow>
          <TableHead>{t('tax.row.step')}</TableHead>
          {columns.map((c) => <TableHead key={c.title} className="text-right">{c.title}</TableHead>)}
        </TableRow>
      </TableHeader>
      <TableBody>
        {rows.map(([title, cell, strong]) => (
          <TableRow key={title} className={strong ? 'font-semibold' : undefined}>
            <TableHead scope="row" className={strong ? 'font-semibold' : 'font-normal'}>{title}</TableHead>
            {columns.map((c) => <TableCell key={c.title} className="text-right tabular-nums">{cell(c.result)}</TableCell>)}
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}
