import { Link } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { LocalDate } from '@/components/LocalDate';
import { Money } from '@/components/Money';
import { SortableTable } from '@/components/page/SortableTable';
import { amountKey } from '@/components/page/sortKey';
import { type GoalFunding } from '@/api/goals';
import { pctText } from '../format';
import { Dash } from '../GoalStatus';
import '@/locales/goals';

/** The envelopes funding the goal, each with its share and what the server could not value; none visible says so (the page notice says the goal is partial). */
export function FundingTab({ funding }: { funding: GoalFunding[] }) {
  const { t } = useTranslation();
  if (funding.length === 0) return <p className="text-muted-foreground">{t('goals_detail.funding_none')}</p>;
  const flags = (f: GoalFunding) => f.flags.length === 0 ? null : (
    <ul className="space-y-0.5 text-sm text-status-warning-text">
      {f.flags.map((flag) => <li key={flag}>{t(`goals_detail.flag.${flag}`, { defaultValue: flag })}</li>)}
    </ul>
  );
  const name = (f: GoalFunding) => <Link to="/investments/$id" params={{ id: f.envelopeId }} className="font-medium hover:underline">{f.name}</Link>;
  const share = (f: GoalFunding) => t('goals_list.pct', { value: pctText(f.shareBp / 100, 2) });
  const value = (f: GoalFunding) => f.value ? <Money value={f.value} tabular /> : <Dash />;
  return (
    <SortableTable<GoalFunding> caption={t('goals_detail.tab_funding')} rows={funding} rowKey={(f) => f.envelopeId}
      columns={[
        { id: 'name', header: t('goals_detail.funding.envelope'), sort: (f) => f.name, cell: name },
        { id: 'share', header: t('goals_detail.funding.share'), align: 'end', sort: (f) => f.shareBp, cell: share },
        { id: 'value', header: t('goals_detail.funding.value'), align: 'end', sort: (f) => amountKey(f.value?.amount), cell: value },
        { id: 'contribution', header: t('goals_detail.funding.contribution'), align: 'end', sort: (f) => amountKey(f.contribution?.amount),
          cell: (f) => f.contribution ? <Money value={f.contribution} tabular /> : <Dash /> },
        { id: 'asOf', header: t('goals_detail.funding.as_of'), sort: (f) => f.asOf ?? '', cell: (f) => f.asOf ? <LocalDate value={f.asOf} style="short" /> : <Dash /> },
        { id: 'flags', header: t('goals_detail.funding.flags'), cell: flags },
      ]}
      mobile={(f) => (
        <div className="space-y-1">
          <div className="flex items-center justify-between gap-2">{name(f)}<span className="tabular-nums">{share(f)}</span></div>
          <div className="text-sm text-muted-foreground">{value(f)}</div>
          {flags(f)}
        </div>
      )} />
  );
}
