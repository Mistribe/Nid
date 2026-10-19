import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { LocalDate } from '@/components/LocalDate';
import { Money } from '@/components/Money';
import { Table, TableBody, TableCaption, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import type { IncomePhase } from '../api';
import { cadenceText } from '../format';
import { PhaseStatus } from './PhaseStatus';

/** One label's phases (spec §7), already in order (`groupPhases`); named by its caption. */
export function PhasesTable({ label, phases, actions }: { label: string; phases: IncomePhase[]; actions?: (phase: IncomePhase) => ReactNode }) {
  const { t } = useTranslation();
  const end = (p: IncomePhase) =>
    p.endsOn ? <LocalDate value={p.endsOn} /> : p.maxCycles != null ? t('income_detail.capped_end', { count: p.maxCycles }) : t('income_detail.open_end');
  return (
    <Table className="caption-top">
      <TableCaption className="mt-0 mb-2 text-left font-medium text-foreground">{label}</TableCaption>
      <TableHeader>
        <TableRow>
          <TableHead>{t('income_detail.col_start')}</TableHead>
          <TableHead>{t('income_detail.col_end')}</TableHead>
          <TableHead className="text-right">{t('income_detail.col_net')}</TableHead>
          <TableHead>{t('income_detail.col_cadence')}</TableHead>
          <TableHead className="text-right">{t('income_detail.col_gross')}</TableHead>
          <TableHead>{t('income_detail.col_status')}</TableHead>
          <TableHead>{t('income_detail.col_note')}</TableHead>
          {actions ? <TableHead className="w-0"><span className="sr-only">{t('income_detail.col_actions')}</span></TableHead> : null}
        </TableRow>
      </TableHeader>
      <TableBody>
        {phases.map((p) => (
          <TableRow key={p.id}>
            <TableCell><LocalDate value={p.startsOn} /></TableCell>
            <TableCell>{end(p)}</TableCell>
            <TableCell className="text-right tabular-nums"><Money value={p.netAmount} tabular /></TableCell>
            <TableCell>{cadenceText(t, p.every)}</TableCell>
            <TableCell className="text-right tabular-nums">{p.grossAmount ? <Money value={p.grossAmount} tabular /> : null}</TableCell>
            <TableCell><PhaseStatus status={p.status} /></TableCell>
            <TableCell className="whitespace-normal">{p.note}</TableCell>
            {actions ? <TableCell>{actions(p)}</TableCell> : null}
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}
