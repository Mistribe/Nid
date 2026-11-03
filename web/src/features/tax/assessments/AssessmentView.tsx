import { useNavigate } from '@tanstack/react-router';
import type { TFunction } from 'i18next';
import { Fragment } from 'react';
import { useTranslation } from 'react-i18next';
import type { components } from '@/api/schema';
import { present, toProblem } from '@/api/errors';
import { OwnerChips } from '@/components/OwnerChips';
import { RowActions } from '@/components/page/RowActions';
import { Section } from '@/components/page/Section';
import { StatLine } from '@/components/page/StatLine';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { SourceLink } from '@/features/documents/SourceLink';
import { ZERO, percentText } from '../format';
import { eur } from '../ResultTable';
import { invalidateAssessments, useDeleteTaxAssessment, type TaxAssessment } from './api';
import { isPerAmount, perLineYear } from './assessmentSchema';
import '@/locales/tax';
import '@/locales/taxAssessments';

type Person = components['schemas']['PersonDto'];
type Declarant = TaxAssessment['declarants'][number];
type FieldKey = (typeof DECLARANT_ROWS)[number] | (typeof MONEY_GROUPS)[number][1][number] | 'shares' | 'referenceIncome' | 'averageRate' | 'marginalRate';
type Money = { amount: string; currency: string } | null | undefined;

const DECLARANT_ROWS = [
  'grossSalaries', 'overtimeExempt', 'deduction', 'deductionAmount', 'netSalaries', 'withholding', 'creditsAdvance',
  'perCeilingYear', 'perUnusedOldest', 'perUnusedMiddle', 'perUnusedLatest', 'perCeilingComputed', 'perCeilingNext',
] as const;
const MONEY_GROUPS = [
  ['household', ['grossIncome', 'taxableIncome', 'capitalIncomeDeclared', 'capitalIncomeTaxable', 'dividendsEligible', 'capitalIncomeAlreadyLevied', 'rentalIncomeNet', 'flatRateIncome', 'exemptIncomeDeclared', 'exemptIncomeNet']],
  ['tax', ['scaleTax', 'proportionalTax', 'taxBeforeCredits', 'flatRatePrepaidCredit', 'pinelReduction', 'childcareExpenses', 'creditsDeclared', 'creditsRetained', 'netTax', 'withholding', 'balance']],
  ['carried', ['rentalDeficitOnRentalIncome', 'rentalDeficitOnGlobalIncome', 'rentalDeficitCarried']],
] as const;

/** The server's balance, signed: a refund is negative. The sign is read from the string, nothing is computed. */
function balanceText(t: TFunction, balance: Money): string {
  if (!balance) return '—';
  if (ZERO.test(balance.amount)) return eur(balance.amount);
  return balance.amount.startsWith('-') ? t('tax.assessment.refunded', { amount: eur(balance.amount.slice(1)) }) : t('tax.assessment.to_pay', { amount: eur(balance.amount) });
}

function declarantText(t: TFunction, d: Declarant, key: (typeof DECLARANT_ROWS)[number]): string | null {
  if (key === 'deduction') return d.deduction ? t(d.deduction === 'actual' ? 'tax.assessment.deduction_actual' : 'tax.assessment.deduction_allowance') : null;
  return d[key] ? eur(d[key].amount) : null;
}

/** Every figure of a notice as shown, keyed so two notices line up: a declarant's by rank. */
function figures(t: TFunction, a: TaxAssessment, persons: Person[]): Map<string, { label: string; text: string }> {
  const out = new Map<string, { label: string; text: string }>();
  const label = (key: FieldKey) => t(`tax.assessment.field.${key}`);
  const money = (m: Money) => (m ? eur(m.amount) : '—');
  out.set('year', { label: t('tax.assessment.changes.year'), text: String(a.year) });
  out.set('issuedYear', { label: t('tax.assessment.changes.issued_year'), text: String(a.issuedYear) });
  out.set('shares', { label: label('shares'), text: a.shares ?? '—' });
  out.set('referenceIncome', { label: label('referenceIncome'), text: money(a.referenceIncome) });
  out.set('averageRate', { label: label('averageRate'), text: a.averageRate ? percentText(t, a.averageRate) : '—' });
  out.set('marginalRate', { label: label('marginalRate'), text: a.marginalRate ? percentText(t, a.marginalRate) : '—' });
  for (const [, keys] of MONEY_GROUPS)
    for (const key of keys) out.set(key, { label: label(key), text: key === 'balance' ? balanceText(t, a[key]) : money(a[key]) });
  for (const d of a.declarants) {
    const who = t('tax.assessment.changes.of', { declarant: t('tax.assessment.declarant', { rank: d.rank }), name: persons.find((p) => p.id === d.personId)?.displayName ?? '' });
    for (const key of DECLARANT_ROWS) {
      const name = t(`tax.assessment.field.${key === 'withholding' ? 'withholding_declarant' : key}`, isPerAmount(key) ? { year: perLineYear(key, a.year) } : {});
      out.set(`${d.rank}.${key}`, { label: `${name} ${who}`, text: declarantText(t, d, key) ?? '—' });
    }
  }
  return out;
}

/** Issue #321: a correction's figures that change, as recorded then as proposed; the whole notice follows it. */
export function AssessmentChanges({ before, after, persons }: { before: TaxAssessment; after: TaxAssessment; persons: Person[] }) {
  const { t } = useTranslation();
  const [old, next] = [figures(t, before, persons), figures(t, after, persons)];
  const keys = [...new Set([...next.keys(), ...old.keys()])].filter((k) => old.get(k)?.text !== next.get(k)?.text);
  return (
    <Section title={t('tax.assessment.changes.title')}>
      {keys.length === 0 ? <p>{t('tax.assessment.changes.none')}</p> : (
        <Table aria-label={t('tax.assessment.changes.title')}>
          <TableHeader>
            <TableRow>
              <TableHead>{t('tax.assessment.figure')}</TableHead>
              <TableHead className="text-right">{t('tax.assessment.changes.recorded')}</TableHead>
              <TableHead className="text-right">{t('tax.assessment.changes.proposed')}</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {keys.map((k) => (
              <TableRow key={k}>
                <TableHead scope="row" className="font-normal">{(next.get(k) ?? old.get(k))!.label}</TableHead>
                <TableCell className="text-right tabular-nums text-muted-foreground">{old.get(k)?.text ?? '—'}</TableCell>
                <TableCell className="text-right font-medium tabular-nums">{next.get(k)?.text ?? '—'}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
    </Section>
  );
}

/**
 * One past notice, read-only: its key figures, then every printed figure, a column per declarant for the personal ones.
 * `actions` false: a notice not recorded yet (a proposal's), which has no menu to correct or delete it.
 */
export function AssessmentView({ assessment: a, persons, actions = true }: { assessment: TaxAssessment; persons: Person[]; actions?: boolean }) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const remove = useDeleteTaxAssessment();
  const declarants = [...a.declarants].sort((x, y) => x.rank - y.rank);
  const money = (m: Money) => (m ? eur(m.amount) : '—');
  const label = (key: FieldKey) => t(`tax.assessment.field.${key}`);

  const declarantRows = DECLARANT_ROWS.flatMap((key) => {
    const cells = declarants.map((d) => declarantText(t, d, key));
    // The declarant's withholding is the notice's « RAS sur salaires et pensions », not the household's total.
    return cells.every((c) => c === null) ? [] : [{ key, label: t(`tax.assessment.field.${key === 'withholding' ? 'withholding_declarant' : key}`, isPerAmount(key) ? { year: perLineYear(key, a.year) } : {}), cells }];
  });
  const groups = [
    { key: 'declarants' as const, rows: declarantRows },
    ...MONEY_GROUPS.map(([group, keys]) => ({
      key: group,
      rows: [
        ...(group === 'tax' ? [['averageRate', a.averageRate], ['marginalRate', a.marginalRate]] as const : [])
          .flatMap(([key, rate]) => (rate ? [{ key, label: label(key), cells: [percentText(t, rate)] }] : [])),
        ...keys.flatMap((key) => {
          const value = a[key];
          if (!value) return [];
          return [{ key, label: label(key), cells: [key === 'balance' ? balanceText(t, value) : eur(value.amount)] }];
        }),
      ],
    })),
  ].filter((g) => g.rows.length > 0);

  return (
    <Section title={t('tax.assessment.title', { year: a.year })}>
      <p className="text-sm text-muted-foreground">{t('tax.assessment.issued', { year: a.issuedYear })}</p>
      <StatLine items={[
        { label: label('shares'), value: a.shares ?? '—' },
        { label: label('referenceIncome'), value: money(a.referenceIncome) },
        { label: label('netTax'), value: money(a.netTax) },
        { label: label('balance'), value: balanceText(t, a.balance) },
      ]} />
      <div className="flex flex-wrap items-center gap-2">
        <OwnerChips owners={declarants.map((d) => ({ personId: d.personId, shareBp: 10000 / declarants.length }))} shares={false} />
        <SourceLink documentId={a.documentId} />
        {actions ? (
          <RowActions menuId={`assessment-menu-${a.id}`} menuLabel={t('tax.assessment.menu', { year: a.year })}
            items={[{ label: t('tax.assessment.correct'), onSelect: () => void navigate({ to: '/tax/assessments/$id/edit', params: { id: a.id } }) }]}
            confirmation={t('tax.assessment.delete_confirm', { year: a.year })} remove={remove}
            present={(_t, error) => present(toProblem(error))} invalidate={invalidateAssessments}
            onConfirm={() => remove.mutate({ params: { path: { id: a.id } } })} />
        ) : null}
      </div>
      <Table aria-label={t('tax.assessment.table', { year: a.year })}>
        <TableHeader>
          <TableRow>
            <TableHead><span className="sr-only">{t('tax.assessment.figure')}</span></TableHead>
            {declarants.map((d) => (
              <TableHead key={d.rank} className="text-right">
                {t('tax.assessment.declarant', { rank: d.rank })}
                <span className="block font-normal text-muted-foreground">{persons.find((p) => p.id === d.personId)?.displayName}</span>
              </TableHead>
            ))}
          </TableRow>
        </TableHeader>
        <TableBody>
          {groups.map((g) => (
            <Fragment key={g.key}>
              <TableRow>
                <TableCell colSpan={declarants.length + 1} className="pt-4 font-semibold">{t(`tax.assessment.group.${g.key}`)}</TableCell>
              </TableRow>
              {g.rows.map((r) => (
                <TableRow key={r.key}>
                  <TableHead scope="row" className="font-normal">{r.label}</TableHead>
                  {r.cells.length === 1 && g.key !== 'declarants'
                    ? <TableCell colSpan={declarants.length} className="text-right tabular-nums">{r.cells[0]}</TableCell>
                    : r.cells.map((c, i) => <TableCell key={i} className="text-right tabular-nums">{c ?? '—'}</TableCell>)}
                </TableRow>
              ))}
            </Fragment>
          ))}
        </TableBody>
      </Table>
    </Section>
  );
}
