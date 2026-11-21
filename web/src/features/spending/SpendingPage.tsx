import { useNavigate, useSearch } from '@tanstack/react-router';
import { lazy, Suspense, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { useBankingStatus } from '@/api/banking';
import type { SpendingSearch } from '@/app/router';
import { DetailTabs, useDetailTab } from '@/components/page/DetailTabs';
import { PageHeader } from '@/components/page/PageHeader';
import { Loading, TableSkeleton } from '@/components/page/skeletons';
import { Badge } from '@/components/ui/badge';
import type { RootSearch } from '@/lib/search';
import { todayIso } from '@/lib/today';
import { ImportStatement } from '../investments/operations/ImportStatement';
import { SPENDING } from './api';
import '@/locales/spending';

// Each tab loads when opened: the overview's figures, the bank lines, the categories and their rules.
const SpendingOverview = lazy(() => import('./SpendingOverview').then((m) => ({ default: m.SpendingOverview })));
const SpendingControls = lazy(() => import('./SpendingOverview').then((m) => ({ default: m.SpendingControls })));
const OperationsPage = lazy(() => import('@/features/operations/OperationsPage').then((m) => ({ default: m.OperationsPage })));
const CategoriesTab = lazy(() => import('./CategoriesTab'));

const TABS = ['overview', 'operations', 'categories'] as const;

/** Dépenses (#423): the overview by category (`/spending`), the bank lines (`?tab=operations`), the categories and their rules (`?tab=categories`). */
export function SpendingPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const [tab] = useDetailTab(TABS);
  // A tab's filters stay on it (an `?category=` would silently filter another); only Foyer / Moi carries over.
  const change = (next: (typeof TABS)[number]) => void navigate({
    from: '/spending', to: '/spending', replace: true, resetScroll: false,
    search: (prev: RootSearch & SpendingSearch) => ({ scope: prev.scope, tab: next === 'overview' ? undefined : next }),
  });
  const lazyTab = (content: ReactNode) => (
    <Suspense fallback={<Loading label={t('spending.loading')}><TableSkeleton columns={4} /></Loading>}>{content}</Suspense>
  );
  const actions = tab === 'overview' ? <Suspense fallback={null}><SpendingControls /></Suspense> : tab === 'operations' ? <ImportStatement /> : null;
  const toClassify = useToClassify();
  return (
    <div className="space-y-4">
      <PageHeader title={t('spending.title')} actions={actions} />
      <DetailTabs value={tab} onChange={change} tabs={[
        { id: 'overview', label: t('spending.tabs.overview'), content: lazyTab(<SpendingOverview />) },
        { id: 'operations', label: t('spending.tabs.operations'), content: lazyTab(<OperationsPage />), badge: toClassify ? (
          <Badge variant="outline" className="border-transparent bg-status-warning/15 text-status-warning-text"><span aria-hidden="true">{t('spending.to_classify_short', { count: toClassify })}</span><span className="sr-only">{t('spending.to_classify', { count: toClassify })}</span></Badge>
        ) : null },
        { id: 'categories', label: t('spending.tabs.categories'), content: lazyTab(<CategoriesTab />) },
      ]} />
    </div>
  );
}

/**
 * #426: the current month's lines to classify, for the « Opérations » tab's badge; the overview's own question for that month (the same
 * query, asked once), without loading its chunk.
 */
function useToClassify() {
  const { $api } = useApi();
  const search: RootSearch = useSearch({ strict: false });
  const status = useBankingStatus();
  const query = { scope: search.scope === 'me' ? 'me' as const : 'household' as const, month: todayIso().slice(0, 7) };
  return $api.useQuery('get', SPENDING, { params: { query } }, { enabled: status.data?.enabled === true }).data?.uncategorised.count ?? 0;
}
