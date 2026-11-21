import { keepPreviousData } from '@tanstack/react-query';
import { Link, useNavigate, useSearch } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import type { TaxSearch } from '@/app/router';
import { DetailTabs, useDetailTab } from '@/components/page/DetailTabs';
import { PageHeader } from '@/components/page/PageHeader';
import { YearStepper } from '@/components/page/YearStepper';
import { DetailTabsSkeleton, HeroSkeleton, Loading, SectionCardSkeleton, StatLineSkeleton, TableSkeleton } from '@/components/page/skeletons';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { todayIso } from '@/lib/today';
import { FIRST_ESTIMATE_YEAR, TAX, TAX_HISTORY } from '@/api/tax';
import { useTaxAssessments } from './assessments/api';
import { AssessmentTab } from './assessment/AssessmentTab';
import { DeclarationsTab } from './declarations/DeclarationsTab';
import { DetailTab } from './detail/DetailTab';
import { EvolutionTab } from './evolution/EvolutionTab';
import { OverviewTab } from './overview/OverviewTab';
import { ProfileForm } from './profile/ProfileForm';
import { ProfileSubmit } from './profile/ProfileSubmit';
import { useProfileDraft } from './profile/useProfileDraft';
import { SimulationTab } from './simulation/SimulationTab';
import { WithholdingTab } from './withholding/WithholdingTab';
import '@/locales/tax';

/** The page's cold load: the tab strip, then the skeleton of the tab asked for. */
function TabsLoading({ label, overview }: { label: string; overview: boolean }) {
  return (
    <Loading label={label}>
      <DetailTabsSkeleton tabs={8} />
      {overview
        ? <><HeroSkeleton /><StatLineSkeleton items={6} /><SectionCardSkeleton /></>
        : <SectionCardSkeleton><TableSkeleton columns={2} /></SectionCardSkeleton>}
    </Loading>
  );
}

export const TABS = ['overview', 'detail', 'withholding', 'assessment', 'evolution', 'simulation', 'profile', 'declarations'] as const;
export type TaxTab = (typeof TABS)[number];

/**
 * Tax estimate spec §9: the overview first, then the calculation step by step, the household's profile, and a
 * simulation of edited inputs. Every figure is the server's; the page only formats them.
 */
export function TaxPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const search: TaxSearch = useSearch({ strict: false });
  const navigate = useNavigate();
  const scope = search.scope === 'me' ? 'me' : 'household';
  const thisYear = todayIso().slice(0, 4);
  const lastYear = String(Number(thisYear) - 1);
  const [rawTab] = useDetailTab(TABS);
  // As `useDetailTab`'s setter, and the 2044 choice leaves with the Declarations tab.
  const setTab = (next: TaxTab) => {
    void navigate({ to: '.', replace: true, resetScroll: false, search: ((prev: TaxSearch) => ({ ...prev, tab: next === 'overview' ? undefined : next, page: undefined, form: next === 'declarations' ? prev.form : undefined })) as never });
  };
  // The declarations are filed for the year just ended; the estimate opens on the current one. The default stays out of the URL.
  const defaultYear = rawTab === 'declarations' ? lastYear : thisYear;
  const list = useTaxAssessments(scope);
  const assessments = list.data?.items ?? [];
  // Each year says what the household has for it: its notice, Nid's estimate, or both.
  const yearHint = (y: string) => {
    const notice = assessments.some((a) => String(a.year) === y);
    if (Number(y) >= FIRST_ESTIMATE_YEAR) return notice ? t('tax.year_hint.both') : t('tax.year_hint.estimate');
    return notice ? t('tax.year_hint.notice') : '';
  };
  const known = [lastYear, thisYear, ...assessments.map((a) => String(a.year))];
  // A year the server refuses (before the rules without a notice, or after next year) falls back to the tab's default; one of the
  // rules is estimated even when no list offers it yet. Until the notices are known, a past year in the URL is kept (and not estimated).
  const awaiting = list.isPending && Number(search.year) < FIRST_ESTIMATE_YEAR;
  const estimable = Number(search.year) >= FIRST_ESTIMATE_YEAR && Number(search.year) <= Number(thisYear) + 1;
  const year = awaiting || estimable || known.includes(search.year ?? '') ? search.year! : defaultYear;
  const setYear = (next: string) => {
    void navigate({ to: '.', replace: true, resetScroll: false, search: ((prev: Record<string, unknown>) => ({ ...prev, year: next === defaultYear ? undefined : next })) as never });
  };
  const past = Number(year) < FIRST_ESTIMATE_YEAR;
  // The year changes with the tab (the 2044's is last year): the page stays on the previous estimate while the next loads, so the
  // menu does not vanish under the pointer.
  const answer = $api.useQuery('get', TAX, { params: { query: { year: Number(year), scope } } }, { enabled: !past, placeholderData: keepPreviousData });
  const estimate = past ? undefined : answer.data;
  // The years the whole history holds data for join the notices' and this year's: the estimate's own stops at the year it shows.
  // The Évolution tab reads the same query, so it is asked once.
  const history = $api.useQuery('get', TAX_HISTORY, { params: { query: { scope } } });
  const years = [...new Set([...known, year, ...(history.data?.years ?? []).filter((h) => h.source === 'estimate').map((h) => String(h.year))])]
    .sort().map((y) => ({ value: y, hint: yearHint(y) }));
  const notices = assessments.filter((a) => String(a.year) === year);
  // The Avis tab exists only for a year with notices: a URL asking for it elsewhere shows the overview.
  const tab = rawTab === 'assessment' && !notices.length ? 'overview' : rawTab;
  // The previous year's estimate kept while the overview's own loads would show last year's hero under this year's filter.
  const stale = !past && answer.isPlaceholderData && tab === 'overview';
  const pending = (past && awaiting) || (!past && !estimate && !answer.error) || stale;
  const assessmentTab = <AssessmentTab notices={notices} gaps={estimate?.gaps} />;
  // Held here, not in the tab: Radix unmounts a hidden tab, and the draft outlives a tab switch.
  const draft = useProfileDraft(estimate, scope);

  return (
    <div className="space-y-6" aria-busy={answer.isPlaceholderData || undefined}>
      <div className={tab === 'declarations' ? 'print:hidden' : undefined}>
        <PageHeader title={t('tax.title')} actions={<>
          <div className="print:hidden"><YearStepper label={t('tax.year')} value={year} years={years} onChange={setYear} /></div>
          <Button asChild className="print:hidden"><Link to="/tax/assessments/new">{t('tax.add_assessment')}</Link></Button>
        </>} />
      </div>
      {pending ? <TabsLoading label={t('tax.loading')} overview={rawTab === 'overview' && !past} /> : null}
      {past && !awaiting && notices.length ? (
        // A year before the estimates: its notices, then the years side by side. The overview is Avis here, so it stays out of the URL.
        <DetailTabs value={rawTab === 'evolution' ? 'evolution' : 'assessment'} onChange={(next) => setTab(next === 'assessment' ? 'overview' : next)} tabs={[
          { id: 'assessment', label: t('tax.tab.assessment'), content: assessmentTab },
          { id: 'evolution', label: t('tax.tab.evolution'),
            content: <EvolutionTab year={year} scope={scope} /> },
        ]} />
      ) : null}
      {!past && answer.error ? <ProblemMessage problem={present(toProblem(answer.error))} /> : null}
      {estimate && !stale ? (
        <DetailTabs value={tab} onChange={setTab} tabs={[
          { id: 'overview', label: t('tax.tab.overview'), content: <OverviewTab estimate={estimate} onTab={setTab} /> },
          { id: 'detail', label: t('tax.tab.detail'), content: <DetailTab estimate={estimate} onTab={setTab} /> },
          { id: 'withholding', label: t('tax.tab.withholding'), content: <WithholdingTab estimate={estimate} /> },
          ...(notices.length ? [{ id: 'assessment' as const, label: t('tax.tab.assessment'), content: assessmentTab }] : []),
          { id: 'evolution', label: t('tax.tab.evolution'),
            content: <EvolutionTab year={year} scope={scope} history={estimate.history} /> },
          { id: 'simulation', label: t('tax.tab.simulation'), content: <SimulationTab key={`${year}-${scope}`} estimate={estimate} scope={scope} /> },
          { id: 'profile', label: t('tax.tab.profile'), content: draft ? <ProfileForm key={draft.key} estimate={estimate} draft={draft} /> : null,
            action: draft ? <ProfileSubmit draft={draft} /> : undefined },
          { id: 'declarations', label: t('tax.tab.declarations'), content: <DeclarationsTab estimate={estimate} year={year} scope={scope} /> },
        ]} />
      ) : null}
    </div>
  );
}
