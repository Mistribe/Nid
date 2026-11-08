import { Link } from '@tanstack/react-router';
import { seeAllClass } from '@/components/page/seeAll';
import { useTranslation } from 'react-i18next';
import { tileOf } from '@/components/nav';
import { GroupedList, GroupedRow } from '@/components/page/GroupedList';
import { SectionCard } from '@/components/page/SectionCard';
import { useGoals } from '@/api/goals';
import { Amounts, GoalProgress, GoalStatus } from '../goals/GoalStatus';
import '@/locales/goals';

const MAX = 4;

/** The home page's goals: the first four open ones in server order (late first); nothing while loading, on error or with no goal. */
export function GoalsCard({ scope }: { scope: 'me' | 'household' }) {
  const { t } = useTranslation();
  const { data } = useGoals(scope, false);
  if (!data || data.items.length === 0) return null;
  return (
    <SectionCard title={t('goals_card.title')} actions={<Link to="/goals" className={seeAllClass}>{t('goals_card.all')}</Link>}>
      <GroupedList>
        {data.items.slice(0, MAX).map((g) => (
          // The progress bar and the amounts go in `detail`: a row has no other place for them.
          <GroupedRow key={g.id} link={{ to: '/goals/$id', params: { id: g.id } }} tile={tileOf('/goals')} title={g.name}
            detail={<span className="flex flex-wrap items-center gap-x-4 gap-y-1 whitespace-nowrap"><GoalProgress pct={g.progressPct} /><Amounts goal={g} /></span>}
            trailing={<GoalStatus status={g.status} />} />
        ))}
      </GroupedList>
    </SectionCard>
  );
}
