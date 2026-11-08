import { useTranslation } from 'react-i18next';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { eur } from '../ResultTable';
import type { TaxEstimate } from '@/api/tax';
import '@/locales/tax';

const CODES = ['net_tax', 'taxable_income', 'reference_income', 'withholding', 'balance'] as const;

/** The notice against Nid's estimate, line by line: the server's figures and its signed gap (notice − Nid), nothing computed here. */
export function GapTable({ gaps, adults }: { gaps: NonNullable<TaxEstimate['gaps']>; adults: TaxEstimate['adults'] }) {
  const { t } = useTranslation();
  const rows = [
    ...CODES.flatMap((code) => gaps.lines.filter((l) => l.code === code)
      .map((l) => ({ key: code, label: t(`tax.gaps.code.${code}`), notice: l.notice, nid: l.nid, gap: l.gap }))),
    ...gaps.declarants.map((d) => ({
      key: d.person, label: t('tax.gaps.declarant', { name: adults.find((a) => a.id === d.person)?.displayName ?? '—' }),
      notice: d.notice, nid: d.nid, gap: d.gap,
    })),
  ];
  const money = (amount?: string | null) => (amount == null ? '—' : eur(amount));
  return (
    <div className="space-y-2">
      <Table aria-label={t('tax.gaps.table')}>
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
              <TableCell className="text-right tabular-nums">{money(r.notice)}</TableCell>
              <TableCell className="text-right tabular-nums">{money(r.nid)}</TableCell>
              <TableCell className="text-right tabular-nums">{money(r.gap)}</TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
      <p className="text-sm text-muted-foreground">{t('tax.gaps.note')}</p>
      <p className="text-sm text-muted-foreground">{t('tax.gaps.withholding_note')}</p>
    </div>
  );
}
