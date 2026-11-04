import { Link, useNavigate, useSearch } from '@tanstack/react-router';
import { lazy, Suspense } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { useSession } from '@/app/Providers';
import type { DashboardSearch, RootSearch } from '@/app/router';
import { EmptyState } from '@/components/EmptyState';
import { PageHeader } from '@/components/page/PageHeader';
import { ProblemMessage } from '@/components/ProblemMessage';
import { buttonVariants } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';
import { Notice, Upcoming } from './Agenda';
import { DASHBOARD, type DashboardView } from './api';
import { HouseholdMonth } from './HouseholdMonth';
import { KeyFigures, type Panel } from './KeyFigures';

// The home page is in the initial bundle; Recharts is not (spec 2D2a §5).
const NetWorthCurve = lazy(() => import('./NetWorthCurve').then((m) => ({ default: m.NetWorthCurve })));
const ProjectionCurve = lazy(() => import('./ProjectionCurve').then((m) => ({ default: m.ProjectionCurve })));
const GoalsCard = lazy(() => import('./GoalsCard').then((m) => ({ default: m.GoalsCard })));
const FigurePanel = lazy(() => import('./panels/FigurePanel').then((m) => ({ default: m.FigurePanel })));

/** Spec §4.1: the home page — figures the server computed for the Foyer / Moi scope. */
export function DashboardPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const search: RootSearch & DashboardSearch = useSearch({ strict: false });
  const scope = search.scope === 'me' ? 'me' : 'household';
  const answer = $api.useQuery('get', DASHBOARD, { params: { query: { scope } } });

  return (
    <div className="space-y-4">
      <PageHeader title={t('dashboard.title')} />
      <Body data={answer.data} error={answer.error} scope={scope} panel={search.panel} />
    </div>
  );
}

function Body({ data, error, scope, panel }: { data?: DashboardView; error: unknown; scope: 'me' | 'household'; panel?: Panel }) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const { capabilities } = useSession();
  if (!data && error) return <ProblemMessage problem={present(toProblem(error))} />;
  if (!data)
    return (
      <ul aria-busy="true" aria-label={t('dashboard.loading')} className="grid grid-cols-2 gap-3 md:grid-cols-4">
        {[0, 1, 2, 3].map((i) => <li key={i}><Skeleton className="h-20 w-full rounded-xl" /></li>)}
      </ul>
    );
  // Counts, not amounts: nothing at all yet is a welcome, not four « inconnu ».
  if (data.envelopes + data.subscriptions + data.incomeSources === 0)
    return (
      <EmptyState module="/dashboard" title={t('dashboard.empty_title')} body={t('dashboard.empty_body')}
        action={<ModuleLinks />} />
    );
  const open = (panel: Panel) => void navigate({ to: '/dashboard', search: (prev: RootSearch & DashboardSearch) => ({ ...prev, panel }) });
  // Closing drops `panel` without a history entry, so Back leaves the page rather than reopening it.
  const close = () => void navigate({ to: '/dashboard', replace: true, resetScroll: false, search: (prev: RootSearch & DashboardSearch) => ({ ...prev, panel: undefined }) });
  return (
    <div className="grid grid-cols-1 gap-4 md:grid-cols-3">
      <div className="space-y-4 md:col-span-3">
        <Notice d={data} />
        <KeyFigures d={data} onOpen={open} scope={scope} />
      </div>
      <div className="md:col-span-3"><HouseholdMonth d={data} scope={scope} /></div>
      <div className={data.envelopes > 0 ? 'md:order-2' : 'md:col-span-3'}><Upcoming d={data} /></div>
      {/* DOM order is the phone order (À venir before the curve); desktop: curve left, À venir right. */}
      {data.envelopes > 0 ? (
        <div className="md:order-1 md:col-span-2" data-guide="dashboard.curve">
          <Suspense fallback={<Skeleton className="h-80 w-full rounded-xl" />}>
            <NetWorthCurve />
          </Suspense>
        </div>
      ) : null}
      <div className="md:col-span-3 empty:hidden"><Suspense fallback={null}><ProjectionCurve /></Suspense></div>
      {capabilities.modules.includes('goals') ? (
        <div className="md:col-span-3 empty:hidden"><Suspense fallback={null}><GoalsCard scope={scope} /></Suspense></div>
      ) : null}
      {panel ? <Suspense fallback={null}><FigurePanel panel={panel} onClose={close} d={data} scope={scope} /></Suspense> : null}
    </div>
  );
}

const LINKS = [
  { module: 'investments', to: '/investments/new', label: 'dashboard.add_envelope' },
  { module: 'realestate', to: '/properties/new', label: 'dashboard.add_property' },
  { module: 'subscriptions', to: '/subscriptions/new', label: 'dashboard.add_subscription' },
  { module: 'income', to: '/income/new', label: 'dashboard.add_income' },
] as const;

/** Spec 3E1 §3 and §5: each link only when its module is enabled. */
function ModuleLinks() {
  const { t } = useTranslation();
  const { capabilities } = useSession();
  return (
    <>
      {LINKS.filter((l) => capabilities.modules.includes(l.module)).map((l) => (
        <Link key={l.to} to={l.to} className={buttonVariants({ variant: 'outline' })}>{t(l.label)}</Link>
      ))}
    </>
  );
}
