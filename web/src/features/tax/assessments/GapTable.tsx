import { useTranslation } from 'react-i18next';
import type { components } from '@/api/schema';
import type { TaxEstimate } from '@/api/tax';
import { formatCurrency } from '@/components/Money';
import { SectionCard } from '@/components/page/SectionCard';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { eur } from '../ResultTable';
import { balanceText } from './AssessmentView';
import '@/locales/tax';

type Person = components['schemas']['PersonDto'];
const CODES = ['net_tax', 'taxable_income', 'reference_income', 'withholding', 'balance'] as const;

/** The notice against Nid's estimate, line by line: the server's figures and its signed gap (notice − Nid), nothing computed here. */
export function GapTable({ gaps, persons }: { gaps: NonNullable<TaxEstimate['gaps']>; persons: Person[] }) {
  const { t } = useTranslation();
  const rows = [
    ...CODES.flatMap((code) => gaps.lines.filter((l) => l.code === code)
      .map((l) => ({ key: code, label: t(`tax.gaps.code.${code}`), notice: l.notice, nid: l.nid, gap: l.gap, balance: code === 'balance' }))),
    ...gaps.declarants.map((d) => ({
      key: d.person, label: t('tax.gaps.declarant', { name: persons.find((p) => p.id === d.person)?.displayName ?? '—' }),
      notice: d.notice, nid: d.nid, gap: d.gap, balance: false,
    })),
  ];
  const money = (amount: string | null | undefined, balance: boolean) => (balance ? balanceText(t, amount) : amount == null ? '—' : eur(amount));
  return (
    <SectionCard title={t('tax.gaps.title')} note={t('tax.gaps.note')} className="space-y-2">
      <Table aria-label={t('tax.gaps.title')}>
        <TableHeader>
          <TableRow>
            <TableHead>{t('tax.gaps.line')}</TableHead>
            <TableHead className="text-right">{t('tax.gaps.notice')}</TableHead>
            <TableHead className="text-right">{t('tax.gaps.nid')}</TableHead>
            <TableHead className="text-right">{t('tax.gaps.gap')}</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {rows.map((r) => (
            <TableRow key={r.key}>
              <TableCell>{r.label}</TableCell>
              <TableCell className="text-right tabular-nums">{money(r.notice, r.balance)}</TableCell>
              <TableCell className="text-right tabular-nums">{money(r.nid, r.balance)}</TableCell>
              <TableCell className="text-right tabular-nums">{r.gap == null ? '—' : formatCurrency(r.gap, 'EUR', { signDisplay: 'exceptZero' })}</TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
      <p className="text-sm text-muted-foreground">{t('tax.gaps.withholding_note')}</p>
    </SectionCard>
  );
}
