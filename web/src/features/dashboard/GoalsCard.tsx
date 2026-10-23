import { Link } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { SectionCard } from '@/components/page/SectionCard';
import { useGoals } from '../goals/api';
import { Amounts, GoalProgress, GoalStatus } from '../goals/GoalStatus';
import '@/locales/goals';

const MAX = 4;

/** The home page's goals: the first four open ones in server order (late first); nothing while loading, on error or with no goal. */
export function GoalsCard({ scope }: { scope: 'me' | 'household' }) {
  const { t } = useTranslation();
  const { data } = useGoals(scope, false);
  if (!data || data.items.length === 0) return null;
  return (
    <SectionCard title={t('goals_card.title')} actions={<Link to="/goals" className="text-sm hover:underline">{t('goals_card.all')}</Link>}>
      <ul className="divide-y">
        {data.items.slice(0, MAX).map((g) => (
          <li key={g.id} className="flex flex-wrap items-center justify-between gap-x-4 gap-y-1 py-2">
            <Link to="/goals/$id" params={{ id: g.id }} className="font-medium hover:underline">{g.name}</Link>
            <GoalProgress pct={g.progressPct} />
            <span className="text-sm text-muted-foreground"><Amounts goal={g} /></span>
            <GoalStatus status={g.status} />
          </li>
        ))}
      </ul>
    </SectionCard>
  );
}
