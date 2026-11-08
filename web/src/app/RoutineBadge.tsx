import { useSearch } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { CountBadge } from '@/components/CountBadge';
import { DASHBOARD } from '@/features/dashboard/api';
import { isRoutineTodo } from '@/features/dashboard/todo';
import type { RootSearch } from '@/lib/search';
import { useSession } from './Providers';

/** The number of things to do on the dashboard (same scope as the page); nothing at zero, on error or without the core module. */
export function RoutineBadge({ dot = false }: { dot?: boolean }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { capabilities } = useSession();
  const search: RootSearch = useSearch({ strict: false });
  const scope = search.scope === 'me' ? 'me' : 'household';
  const count = $api.useQuery('get', DASHBOARD, { params: { query: { scope } } }, { enabled: capabilities.modules.includes('core') }).data?.todo.filter(isRoutineTodo).length;
  if (!count) return null;
  return <CountBadge count={count} label={t('nav.routine_badge', { count })} dot={dot} />;
}
