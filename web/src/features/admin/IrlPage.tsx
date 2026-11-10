import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem, type Presentation } from '@/api/errors';
import { ConfirmAction } from '@/components/ConfirmAction';
import { DateField } from '@/components/form/DateField';
import { Field } from '@/components/form/Field';
import { Segmented } from '@/components/form/Segmented';
import { formatLocalDate } from '@/components/LocalDate';
import { EmptyState } from '@/components/EmptyState';
import { ProblemMessage } from '@/components/ProblemMessage';
import { SeriesLink } from '@/components/page/SeriesLink';
import { SwitchField } from '@/components/page/SwitchField';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { fieldErrorsOf, messageFor } from '@/components/form/fieldErrors';
import { todayIso } from '@/lib/today';
import { Loading, SectionSkeleton, TableSkeleton } from '@/components/page/skeletons';
import { DetailTabs, useDetailTab } from '@/components/page/DetailTabs';
import { Pager, useListPage, useStepBack } from '@/components/page/Pager';
import { Section } from '@/components/page/Section';
import { SortableTable } from '@/components/page/SortableTable';
import { AdminFrame } from './AdminFrame';
import { AdminSeries } from './AdminSeries';
import { formatInstant } from '@/lib/format';
import '@/locales/admin';
import '@/locales/forms';

const HOUSEHOLD = '/api/v1/admin/household';
const IRL = '/api/v1/realestate/irl';
const RUNS = '/api/v1/realestate/irl/runs';

const STATUS = { running: 'admin.fx.status_running', succeeded: 'admin.fx.status_succeeded', failed: 'admin.fx.status_failed' } as const;
const QUARTERS = [1, 2, 3, 4].map((q) => ({ value: String(q), label: `T${q}` }));

/** « 2026-Q2 » as the French write it. */
const quarterText = (quarter: string) => quarter.replace('-Q', '-T');

/** Administration › IRL: the weekly collection on or off, a collection now, the known quarters, a quarter by hand, and the journal. */
export function IrlPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const [problem, setProblem] = useState<Presentation | null>(null);
  const [tab, setTab] = useDetailTab(['series', 'journal'] as const);
  const household = $api.useQuery('get', HOUSEHOLD);

  const fail = (error: unknown) => {
    setProblem(present(toProblem(error)));
  };
  const save = $api.useMutation('patch', HOUSEHOLD, {
    onSuccess: () => {
      setProblem(null);
      toast.success(t('admin.irl.saved'));
      void queryClient.invalidateQueries({ queryKey: ['get', HOUSEHOLD] });
    },
    onError: fail,
  });
  const sync = $api.useMutation('post', '/api/v1/realestate/irl/sync', {
    onSuccess: () => {
      setProblem(null);
      toast.success(t('admin.irl.sync_requested'));
      void queryClient.invalidateQueries({ queryKey: ['get', IRL] });
      void queryClient.invalidateQueries({ queryKey: ['get', RUNS] });
    },
    onError: (error) => {
      // Not a failure to explain: the update the admin asked for is already under way.
      if (toProblem(error).code === 'realestate.irl_sync_running') {
        setProblem(null);
        toast.info(t('admin.irl.already_running'));
      } else fail(error);
    },
  });
  const title = t('admin.irl.title');
  if (household.error) return <AdminFrame title={title}><ProblemMessage problem={present(toProblem(household.error))} /></AdminFrame>;
  if (!household.data) return <AdminFrame title={title}><Loading label={t('admin.loading')}><SectionSkeleton /></Loading></AdminFrame>;
  const enabled = household.data.irlEnabled;

  return (
    <AdminFrame title={title}>
      {problem ? <ProblemMessage problem={problem} /> : null}

      <section className="space-y-3">
        <SwitchField label={t('admin.irl.enabled')} checked={enabled} onCheckedChange={(irlEnabled) => { save.mutate({ body: { irlEnabled } }); }} />
        <p className="max-w-prose text-sm text-muted-foreground">{t('admin.irl.hint')}</p>
        <ConfirmAction label={t('admin.irl.sync')} confirmation={t('admin.irl.sync_confirm')} disabled={!enabled} pending={sync.isPending}
          onConfirm={() => sync.mutate({})} />
      </section>

      <ManualQuarter />

      <DetailTabs value={tab} onChange={setTab} tabs={[
        { id: 'series', label: t('admin.tab_series'), content: (
          <>
            <AdminSeries seriesKey="irl" />
            <IrlValues />
          </>
        ) },
        { id: 'journal', label: t('admin.tab_journal'), content: (
          <IrlRuns />
        ) },
      ]} />
    </AdminFrame>
  );
}

/** The stored quarters, newest first, a page at a time. */
function IrlValues() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const [page, toPage] = useListPage();
  const irl = $api.useQuery('get', IRL, { params: { query: { page } } });
  useStepBack(page, irl.data, toPage);
  return (
    <Section title={t('admin.irl.values_title')} actions={<SeriesLink keys={['irl']} />}>
      {irl.error ? <ProblemMessage problem={present(toProblem(irl.error))} /> : null}
      {!irl.error && (!irl.data || (irl.data.items.length === 0 && irl.data.total > 0)) ? <Loading label={t('admin.loading')}><TableSkeleton columns={5} /></Loading> : null}
      {irl.data && irl.data.total === 0 ? <EmptyState module="/admin" title={t('admin.irl.no_values')} body="" level={3} /> : null}
      {irl.data && irl.data.items.length > 0 ? (
        <>
          <SortableTable caption={t('admin.irl.values_title')} rows={irl.data.items} rowKey={(v) => v.quarter}
            columns={[
              { id: 'quarter', header: t('admin.irl.quarter'), cell: (v) => quarterText(v.quarter) },
              { id: 'value', header: t('admin.irl.value'), cell: (v) => <span className="tabular-nums">{v.value}</span> },
              { id: 'published', header: t('admin.irl.published'), cell: (v) => (v.publishedOn ? formatLocalDate(v.publishedOn) : null) },
              { id: 'fetched', header: t('admin.irl.fetched'), cell: (v) => formatLocalDate(v.fetchedOn) },
              { id: 'source', header: t('admin.irl.source'), cell: (v) => (v.source === 'insee' ? t('admin.irl.source_insee') : t('admin.irl.source_manual')) },
            ]}
            mobile={(v) => (
              <>
                <p className="font-medium">{quarterText(v.quarter)} · <span className="tabular-nums">{v.value}</span></p>
                <p className="text-sm text-muted-foreground">{v.source === 'insee' ? t('admin.irl.source_insee') : t('admin.irl.source_manual')} · {formatLocalDate(v.fetchedOn)}</p>
              </>
            )} />
          <Pager page={page} total={irl.data.total} onPage={toPage} />
        </>
      ) : null}
    </Section>
  );
}

/** The collection journal, newest first, a page at a time. */
function IrlRuns() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const [page, toPage] = useListPage();
  const runs = $api.useQuery('get', RUNS, { params: { query: { page } } });
  useStepBack(page, runs.data, toPage);
  // The wire carries plain strings; an unknown value is shown as it came rather than hidden.
  const statusLabel = (status: string) => (status in STATUS ? t(STATUS[status as keyof typeof STATUS]) : status);
  return (
    <Section title={t('admin.irl.runs_title')}>
      {runs.error ? <ProblemMessage problem={present(toProblem(runs.error))} /> : null}
      {!runs.error && (!runs.data || (runs.data.items.length === 0 && runs.data.total > 0)) ? <Loading label={t('admin.loading')}><TableSkeleton columns={6} /></Loading> : null}
      {runs.data && runs.data.total === 0 ? <EmptyState module="/admin" title={t('admin.irl.no_runs')} body="" level={3} /> : null}
      {runs.data && runs.data.items.length > 0 ? (
        <>
          <SortableTable caption={t('admin.irl.runs_title')} rows={runs.data.items} rowKey={(r) => r.id}
            columns={[
              { id: 'started', header: t('admin.irl.started'), cell: (r) => formatInstant(r.startedAt) },
              { id: 'status', header: t('admin.irl.status'), cell: (r) => statusLabel(r.status) },
              { id: 'added', header: t('admin.irl.added'), cell: (r) => r.added },
              { id: 'replaced', header: t('admin.irl.replaced'), cell: (r) => r.replaced },
              { id: 'rejected', header: t('admin.irl.rejected'), cell: (r) => r.rejected },
              { id: 'message', header: t('admin.irl.message'), cell: (r) => r.message },
            ]}
            mobile={(r) => (
              <>
                <p className="font-medium">{formatInstant(r.startedAt)}</p>
                <p className="text-sm text-muted-foreground">{statusLabel(r.status)} · {r.added} · {r.replaced} · {r.rejected}{r.message ? ` · ${r.message}` : ''}</p>
              </>
            )} />
          <Pager page={page} total={runs.data.total} onPage={toPage} />
        </>
      ) : null}
    </Section>
  );
}

/** A quarter INSEE has published but Nid has not read, typed by hand with its Journal officiel date: the server refuses one INSEE already gave, on the value. */
function ManualQuarter() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const [year, setYear] = useState('');
  const [quarter, setQuarter] = useState('1');
  const [value, setValue] = useState('');
  const [publishedOn, setPublishedOn] = useState('');
  const [errors, setErrors] = useState<{ year?: string; quarter?: string; value?: string; publishedOn?: string }>({});
  const [problem, setProblem] = useState<Presentation | null>(null);
  const put = $api.useMutation('put', '/api/v1/realestate/irl/{year}/{quarter}');

  const submit = () => {
    // A French keyboard types a comma; the wire wants a decimal point.
    const wire = value.trim().replace(',', '.');
    const required = (ok: boolean) => (ok ? undefined : t('edit.required'));
    const next = { year: required(/^\d{4}$/.test(year.trim())), value: required(!!wire), publishedOn: required(!!publishedOn) };
    setErrors(next);
    setProblem(null);
    if (next.year || next.value || next.publishedOn) return;
    put.mutate({ params: { path: { year: Number(year), quarter: Number(quarter) } }, body: { value: wire, publishedOn } }, {
      onSuccess: () => { setValue(''); toast.success(t('admin.irl.saved')); void queryClient.invalidateQueries({ queryKey: ['get', IRL] }); },
      onError: (error) => {
        const fields = fieldErrorsOf(error);
        const message = (path: string) => { const f = fields.find((x) => x.path === path); return f ? messageFor(t, f) : undefined; };
        setErrors({ quarter: message('quarter'), value: message('value'), publishedOn: message('publishedOn') });
        if (!fields.some((f) => ['quarter', 'value', 'publishedOn'].includes(f.path))) setProblem(present(toProblem(error)));
      },
    });
  };

  return (
    <Section title={t('admin.irl.manual_title')}>
      <p className="max-w-prose text-sm text-muted-foreground">{t('admin.irl.manual_hint')}</p>
      <form aria-label={t('admin.irl.manual_title')} noValidate className="max-w-md space-y-3" onSubmit={(e) => { e.preventDefault(); submit(); }}>
        {problem ? <ProblemMessage problem={problem} /> : null}
        <Field label={t('admin.irl.year')} error={errors.year}>
          {(c) => <Input {...c} inputMode="numeric" maxLength={4} value={year} onChange={(e) => setYear(e.target.value)} />}
        </Field>
        <Field label={t('admin.irl.manual_quarter')} error={errors.quarter}>
          {(c) => <Segmented aria-label={t('admin.irl.manual_quarter')} value={quarter} onValueChange={setQuarter} options={QUARTERS} {...(c['aria-describedby'] ? { 'aria-describedby': c['aria-describedby'] } : {})} />}
        </Field>
        <Field label={t('admin.irl.manual_value')} error={errors.value}>
          {(c) => <Input {...c} inputMode="decimal" value={value} onChange={(e) => setValue(e.target.value)} />}
        </Field>
        <Field label={t('admin.irl.manual_published')} error={errors.publishedOn}>
          {(c) => <DateField {...c} max={todayIso()} value={publishedOn} onChange={setPublishedOn} />}
        </Field>
        <Button type="submit" disabled={put.isPending}>{t('admin.irl.manual_save')}</Button>
      </form>
    </Section>
  );
}
