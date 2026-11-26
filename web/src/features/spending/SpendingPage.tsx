import { useNavigate } from '@tanstack/react-router';
import { lazy, Suspense, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { LINES_TO_CLASSIFY, useBankingStatus } from '@/api/banking';
import type { SpendingSearch } from '@/app/router';
import { DetailTabs, useDetailTab } from '@/components/page/DetailTabs';
import { PageHeader } from '@/components/page/PageHeader';
import { Loading, TableSkeleton } from '@/components/page/skeletons';
import { Badge } from '@/components/ui/badge';
import type { RootSearch } from '@/lib/search';
import { ImportStatement } from '../investments/operations/ImportStatement';
import { monthDays, MonthStepper, useMonth } from './MonthStepper';
import '@/locales/spending';

// Each tab loads when opened: the overview's figures, the bank lines, the categories and their rules.
const SpendingOverview = lazy(() => import('./SpendingOverview').then((m) => ({ default: m.SpendingOverview })));
const SpendingControls = lazy(() => import('./SpendingOverview').then((m) => ({ default: m.SpendingControls })));
const OperationsPage = lazy(() => import('@/features/operations/OperationsPage').then((m) => ({ default: m.OperationsPage })));
const CategoriesTab = lazy(() => import('./CategoriesTab'));
const ToClassifyTab = lazy(() => import('./ToClassifyTab'));

const TABS = ['overview', 'operations', 'to-classify', 'categories'] as const;

/** Dépenses (#423): the overview by category (`/spending`), the bank lines (`?tab=operations`), the lines to classify with Nid's suggestion
 * (`?tab=to-classify`), the categories and their rules (`?tab=categories`). */
export function SpendingPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const [tab] = useDetailTab(TABS);
  const { search, month } = useMonth();
  // The filters' own dates (a figure's lines, a range typed in) win over the month.
  const dated = !!(search.from || search.to);
  // A tab's filters stay on it (an `?category=` would silently filter another); only Foyer / Moi and the month carry over.
  const change = (next: (typeof TABS)[number]) => void navigate({
    from: '/spending', to: '/spending', replace: true, resetScroll: false,
    search: (prev: RootSearch & SpendingSearch) => ({ scope: prev.scope, month: prev.month, tab: next === 'overview' ? undefined : next }),
  });
  const lazyTab = (content: ReactNode) => (
    <Suspense fallback={<Loading label={t('spending.loading')}><TableSkeleton columns={4} /></Loading>}>{content}</Suspense>
  );
  const actions = tab === 'overview' ? <Suspense fallback={null}><SpendingControls /></Suspense> : tab === 'operations' ? <>{dated ? null : <MonthStepper />}<ImportStatement /></> : null;
  const toClassify = useToClassify();
  return (
    <div className="space-y-4">
      <PageHeader title={t('spending.title')} actions={actions} />
      <DetailTabs value={tab} onChange={change} tabs={[
        { id: 'overview', label: t('spending.tabs.overview'), content: lazyTab(<SpendingOverview />) },
        { id: 'operations', label: t('spending.tabs.operations'), content: lazyTab(<OperationsPage month={dated ? undefined : monthDays(month)} />) },
        { id: 'to-classify', label: t('spending.tabs.to_classify'), content: lazyTab(<ToClassifyTab />), badge: toClassify ? (
          <Badge variant="outline" className="border-transparent bg-status-warning/15 text-status-warning-text"><span aria-hidden="true">{toClassify}</span><span className="sr-only">{t('spending.to_classify', { count: toClassify })}</span></Badge>
        ) : null },
        { id: 'categories', label: t('spending.tabs.categories'), content: lazyTab(<CategoriesTab />) },
      ]} />
    </div>
  );
}

/** #719: the À classer tab's own count (its first page, every account, the same query), so the badge and the tab agree. */
function useToClassify() {
  const { $api } = useApi();
  const status = useBankingStatus();
  return $api.useQuery('get', LINES_TO_CLASSIFY, { params: { query: { page: 1 } } }, { enabled: status.data?.enabled === true }).data?.total ?? 0;
}
