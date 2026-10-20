import { useTranslation } from 'react-i18next';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { Section } from '../income/Section';
import type { TaxEstimate } from './api';
import { percentText } from './format';
import { eur } from './ResultTable';

const ZERO = /^-?0(\.0+)?$/;

/**
 * Tax estimate spec §4bis: what to type in « Actualiser suite à une hausse ou une baisse de vos revenus » on impots.gouv.fr, and the
 * rate and monthly amounts that should follow. Every figure is the server's.
 */
export function WithholdingSection({ estimate }: { estimate: TaxEstimate }) {
  const { t } = useTranslation();
  const w = estimate.result.withholding;
  const declared: [string, string][] = [
    ...w.declarants.flatMap((d, i): [string, string][] => {
      const per = estimate.result.declarants[i]?.perDeducted ?? '0';
      return [
        [t('tax.withholding.salaries', { name: d.name }), eur(d.salaries)],
        ...(ZERO.test(per) ? [] : [[t('tax.withholding.per', { name: d.name }), eur(per)] as [string, string]]),
      ];
    }),
    ...(ZERO.test(w.rentalDeclared) ? [] : [[t('tax.withholding.rental'), eur(w.rentalDeclared)] as [string, string]]),
  ];
  const expected: [string, string][] = [
    [t('tax.withholding.rate'), percentText(t, w.rate)],
    ...w.declarants.map((d): [string, string] => [t('tax.withholding.monthly', { name: d.name }), eur(d.monthly)]),
    ...(ZERO.test(w.rentalMonthly) ? [] : [[t('tax.withholding.rental_monthly'), eur(w.rentalMonthly)] as [string, string]]),
    [t('tax.withholding.annual'), eur(w.annual)],
    [t('tax.withholding.left_out'), eur(w.leftOut)],
    [t('tax.withholding.balance'), eur(w.balance)],
  ];
  const table = (label: string, rows: [string, string][]) => (
    <Table aria-label={label}>
      <TableHeader><TableRow><TableHead>{t('tax.withholding.line')}</TableHead><TableHead className="text-right">{t('tax.withholding.amount')}</TableHead></TableRow></TableHeader>
      <TableBody>
        {rows.map(([title, value]) => (
          <TableRow key={title}><TableHead scope="row" className="font-normal">{title}</TableHead><TableCell className="text-right tabular-nums">{value}</TableCell></TableRow>
        ))}
      </TableBody>
    </Table>
  );

  return (
    <>
      <Section title={t('tax.withholding.title')}>
        <p className="text-sm text-muted-foreground">{t('tax.withholding.where', { year: estimate.year })}</p>
        {table(t('tax.withholding.declared'), declared)}
        {ZERO.test(w.rentalDeclared) ? null : <p className="text-sm text-muted-foreground">{t('tax.withholding.rental_hint', { gross: eur(estimate.inputs.rentalGross) })}</p>}
      </Section>
      <Section title={t('tax.withholding.expected')}>
        {table(t('tax.withholding.expected'), expected)}
        <p className="text-sm text-muted-foreground">{t('tax.withholding.help')}</p>
      </Section>
    </>
  );
}
