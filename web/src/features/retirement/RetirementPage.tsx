import { useQueryClient } from '@tanstack/react-query';
import { Link, useSearch } from '@tanstack/react-router';
import { Suspense, lazy, useState } from 'react';
import { toast } from 'sonner';
import type { TFunction } from 'i18next';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { SOURCES } from '@/api/income';
import { RETIREMENT } from '@/api/retirement';
import { useSession } from '@/app/Providers';
import { formatCurrency } from '@/components/Money';
import { DetailTabs, useDetailTab } from '@/components/page/DetailTabs';
import { PageHeader } from '@/components/page/PageHeader';
import { RowMenu } from '@/components/page/RowMenu';
import { OverviewSkeleton } from '@/components/page/skeletons';
import { SortableTable } from '@/components/page/SortableTable';
import { yyyyMmLabel as month } from '@/lib/format';
import type { RootSearch } from '@/lib/search';
import { RetirementDialog } from '@/features/settings/RetirementDialog';
import { Answer, type Scope } from './Answer';
import { PensionDialog } from './PensionDialog';
import type { Departure, RetirementView } from './api';
import '@/locales/retirement';

const OverviewTab = lazy(() => import('./overview/OverviewTab').then((m) => ({ default: m.OverviewTab })));
const AnalysisTab = lazy(() => import('./overview/OverviewTab').then((m) => ({ default: m.AnalysisTab })));

const TABS = ['overview', 'analysis', 'departures'] as const;

const age = (d: Departure, t: TFunction) => (d.months > 0 ? t('settings.projection.age_years_months', { years: d.years, months: d.months }) : t('settings.projection.age_years', { count: d.years }));

/** Retirement spec §5 on the overview grammar (issue #141): the gap and the curve, then « Analyse » (the capital, the assumptions), then who leaves when. */
export function RetirementPage() {
  const { t } = useTranslation();
  const search: RootSearch = useSearch({ strict: false });
  const scope = search.scope === 'me' ? 'me' : 'household';
  const [tab, setTab] = useDetailTab(TABS);
  const fallback = <OverviewSkeleton label={t('retirement.loading')} />;
  return (
    <div className="space-y-4">
      <PageHeader title={t('nav.retirement')} />
      <DetailTabs value={tab} onChange={setTab} tabs={[
        { id: 'overview', label: t('retirement.tab_overview'), content: <Suspense fallback={fallback}><OverviewTab scope={scope} /></Suspense> },
        { id: 'analysis', label: t('retirement.tab_analysis'), content: <Suspense fallback={fallback}><AnalysisTab scope={scope} /></Suspense> },
        { id: 'departures', label: t('retirement.tab_departures'), content: <Answer scope={scope}>{(data) => <Departures data={data} scope={scope} />}</Answer> },
      ]} />
    </div>
  );
}

/** Who leaves when, with the pension; the row menu edits the departure or enters the reader's own pension. */
function Departures({ data, scope }: { data: RetirementView; scope: Scope }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const money = (amount: string) => formatCurrency(amount, data.currency);
  const { session } = useSession();
  const queryClient = useQueryClient();
  const [dialog, setDialog] = useState<{ kind: 'departure' | 'pension'; d: Departure } | null>(null);
  const close = () => setDialog(null);
  const done = (message: string) => {
    close();
    toast.success(message);
    void queryClient.invalidateQueries({ queryKey: ['get', RETIREMENT] });
  };
  // A private pension is invisible in « Foyer »: ask for the reader's own sources (scope=me) before offering to enter another.
  // Her own pension sources, private ones included (scope=me, kind=pension): shared ones of the other adult stay in the list, so the owner is tested.
  const mine = $api.useQuery('get', SOURCES, { params: { query: { scope: 'me', kind: 'pension' } } }, { enabled: scope === 'household' });
  const ownsPension = (mine.data?.items ?? []).some((s) => s.owners.some((o) => o.personId === session.person.id));
  const canEnter = scope === 'me' || mine.data !== undefined;
  const actions = (d: Departure) => (
    <RowMenu label={t('retirement.row_actions', { name: d.name })} items={[
      { label: t('retirement.edit_departure'), onSelect: () => setDialog({ kind: 'departure', d }) },
      ...(d.person === session.person.id && d.departure && d.sources.length === 0 && canEnter && !ownsPension ? [{ label: t('retirement.enter_pension'), onSelect: () => setDialog({ kind: 'pension' as const, d }) }] : []),
    ]} />
  );

  return (
    <>
      <div className="space-y-2">
        <SortableTable
          caption={t('retirement.departures')}
          rows={data.departures}
          rowKey={(d) => d.person}
          columns={[
            { id: 'name', header: t('retirement.adult'), cell: (d) => d.name },
            { id: 'departure', header: t('retirement.departure'), cell: (d) => (d.departure ? month(d.departure) : '—') },
            { id: 'age', header: t('retirement.age'), cell: (d) => age(d, t) },
            { id: 'pension', header: t('retirement.pension'), align: 'end', cell: (d) => <Pension d={d} money={money} hidden={scope === 'household' && ownsPension && d.person === session.person.id} /> },
            { id: 'actions', header: t('retirement.actions'), align: 'end', cell: actions },
          ]}
          mobile={(d) => (
            <div className="space-y-0.5">
              <div className="font-medium">{d.name}</div>
              <div className="text-sm text-muted-foreground">{d.departure ? month(d.departure) : '—'} · {age(d, t)}</div>
              <div className="text-sm"><Pension d={d} money={money} hidden={scope === 'household' && ownsPension && d.person === session.person.id} /></div>
              <div>{actions(d)}</div>
            </div>
          )}
        />
        {scope === 'household' ? <p className="text-sm text-muted-foreground">{t('retirement.household_pensions')}</p> : null}
      </div>
      {dialog?.kind === 'departure' ? (
        <RetirementDialog person={{ id: dialog.d.person, displayName: dialog.d.name, birthDate: dialog.d.birthDate, retirementAge: dialog.d.years, retirementMonths: dialog.d.months }} onClose={close} onDone={done} />
      ) : null}
      {dialog?.kind === 'pension' && dialog.d.departure ? (
        <PensionDialog personId={dialog.d.person} departure={dialog.d.departure} currency={data.currency} onClose={close} onDone={done} />
      ) : null}
    </>
  );
}

function Pension({ d, money, hidden }: { d: Departure; money: (amount: string) => string; hidden: boolean }) {
  const { t } = useTranslation();
  return (
    <div className="space-y-0.5">
      <div className="tabular-nums">{d.pension ? money(d.pension) : '—'}</div>
      {d.sources.map((s) => (
        <div key={s.source} className="text-sm">
          <Link to="/income/$id" params={{ id: s.source }} className="underline underline-offset-2">{s.name}</Link>
        </div>
      ))}
      {hidden && d.sources.length === 0 ? <div className="text-sm text-muted-foreground">{t('retirement.private_pension')}</div> : null}
    </div>
  );
}
