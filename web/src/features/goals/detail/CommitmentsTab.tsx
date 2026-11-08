import { useTranslation } from 'react-i18next';
import { Money } from '@/components/Money';
import { SortableTable } from '@/components/page/SortableTable';
import type { GoalCommitment } from '@/api/goals';
import { Dash } from '../GoalStatus';
import '@/locales/goals';

/** The monthly outflows an emergency fund counts, by source; a source without a rate says so instead of a figure. */
export function CommitmentsTab({ commitments, months }: { commitments: GoalCommitment[]; months?: number | null }) {
  const { t } = useTranslation();
  const label = (c: GoalCommitment) => t(`goals_detail.commitments.label.${c.source}`, { defaultValue: c.source });
  const monthly = (c: GoalCommitment) => c.monthly ? <Money value={c.monthly} tabular />
    : c.missingCurrency ? <span className="text-sm text-status-warning-text">{t('goals_detail.commitments.missing_currency', { currency: c.missingCurrency })}</span>
    : <Dash />;
  return (
    <div className="space-y-3">
      {months ? <p className="text-sm text-muted-foreground">{t('goals_detail.commitments.months', { count: months })}</p> : null}
      {commitments.length === 0 ? <p className="text-muted-foreground">{t('goals_detail.commitments.empty')}</p> : (
        <SortableTable<GoalCommitment> caption={t('goals_detail.tab_commitments')} rows={commitments} rowKey={(c) => c.source}
          columns={[
            { id: 'source', header: t('goals_detail.commitments.source'), sort: label, cell: label },
            { id: 'count', header: t('goals_detail.commitments.count'), align: 'end', sort: (c) => c.count, cell: (c) => <span className="tabular-nums">{c.count}</span> },
            { id: 'monthly', header: t('goals_detail.commitments.monthly'), align: 'end', cell: monthly },
          ]}
          mobile={(c) => (
            <div className="flex items-center justify-between gap-2"><span>{label(c)} · {c.count}</span>{monthly(c)}</div>
          )} />
      )}
    </div>
  );
}
