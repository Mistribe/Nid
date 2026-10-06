import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { LocalDate } from '@/components/LocalDate';
import { Money } from '@/components/Money';
import type { IncomePhase } from '../api';
import { cadenceText } from '../format';
import { PhaseStatus } from './PhaseStatus';

/** One label's phases (spec §7), already in order (`groupPhases`); named by its caption. */
export function PhasesTable({ label, phases, actions, editing }: {
  label: string; phases: IncomePhase[]; actions?: (phase: IncomePhase) => ReactNode; editing?: { id: string; form: ReactNode } | null;
}) {
  const { t } = useTranslation();
  const cols = actions ? 8 : 7;
  const end = (p: IncomePhase) =>
    p.endsOn ? <LocalDate value={p.endsOn} /> : p.maxCycles != null ? t('income_detail.capped_end', { count: p.maxCycles }) : t('income_detail.open_end');
  return (
    <table className="w-full text-sm">
      <caption className="text-left font-medium">{label}</caption>
      <thead>
        <tr>
          <th scope="col" className="text-left">{t('income_detail.col_start')}</th>
          <th scope="col" className="text-left">{t('income_detail.col_end')}</th>
          <th scope="col" className="text-left">{t('income_detail.col_net')}</th>
          <th scope="col" className="text-left">{t('income_detail.col_cadence')}</th>
          <th scope="col" className="text-left">{t('income_detail.col_gross')}</th>
          <th scope="col" className="text-left">{t('income_detail.col_status')}</th>
          <th scope="col" className="text-left">{t('income_detail.col_note')}</th>
          {actions ? <th scope="col" className="text-left">{t('income_detail.col_actions')}</th> : null}
        </tr>
      </thead>
      <tbody>
        {phases.map((p) => editing?.id === p.id ? (
          <tr key={p.id}><td colSpan={cols}>{editing.form}</td></tr>
        ) : (
          <tr key={p.id}>
            <td><LocalDate value={p.startsOn} /></td>
            <td>{end(p)}</td>
            <td><Money value={p.netAmount} tabular /></td>
            <td>{cadenceText(t, p.every)}</td>
            <td>{p.grossAmount ? <Money value={p.grossAmount} tabular /> : null}</td>
            <td><PhaseStatus status={p.status} /></td>
            <td>{p.note}</td>
            {actions ? <td>{actions(p)}</td> : null}
          </tr>
        ))}
      </tbody>
    </table>
  );
}
