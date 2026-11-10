import type { TFunction } from 'i18next';
import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem, type Presentation } from '@/api/errors';
import { ConfirmAction } from '@/components/ConfirmAction';
import { Field } from '@/components/form/Field';
import { SelectField } from '@/components/form/SelectField';
import { formatLocalDate, monthText } from '@/components/LocalDate';
import { EmptyState } from '@/components/EmptyState';
import { ProblemMessage } from '@/components/ProblemMessage';
import { SeriesLink } from '@/components/page/SeriesLink';
import { SwitchField } from '@/components/page/SwitchField';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { fieldErrorsOf, knownConflictMessage, messageFor } from '@/components/form/fieldErrors';
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
const INFLATION = '/api/v1/core/inflation/admin';
const RUNS = '/api/v1/core/inflation/runs';

const STATUS = { running: 'admin.fx.status_running', succeeded: 'admin.fx.status_succeeded', failed: 'admin.fx.status_failed' } as const;
const monthName = new Intl.DateTimeFormat('fr-FR', { month: 'long' });
// The first collected month is January 1996; the form offers every year since.
const YEARS = Array.from({ length: new Date().getFullYear() - 1995 }, (_, i) => String(new Date().getFullYear() - i)).map((y) => ({ value: y, label: y }));
const MONTHS = Array.from({ length: 12 }, (_, i) => ({ value: String(i + 1), label: monthName.format(new Date(2026, i, 1)) }));

/** A refusal no field owns, in French: a 422 reads by its code's sentence (`core.cpi_from_insee`…). */
const refusal = (t: TFunction, error: unknown): Presentation => {
  const base = present(toProblem(error));
  return base.kind === 'validation' ? { ...base, detail: knownConflictMessage(t, toProblem(error).code) } : base;
};

/** Administration › Inflation: the weekly collection on or off, a collection now, the known months, a month by hand, and the journal. */
export function InflationPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const [problem, setProblem] = useState<Presentation | null>(null);
  const [tab, setTab] = useDetailTab(['series', 'journal'] as const);
  const household = $api.useQuery('get', HOUSEHOLD);

  const fail = (error: unknown) => setProblem(refusal(t, error));
  const save = $api.useMutation('patch', HOUSEHOLD, {
    onSuccess: () => {
      setProblem(null);
      toast.success(t('admin.inflation.saved'));
      void queryClient.invalidateQueries({ queryKey: ['get', HOUSEHOLD] });
    },
    onError: fail,
  });
  const sync = $api.useMutation('post', '/api/v1/core/inflation/sync', {
    onSuccess: () => {
      setProblem(null);
      toast.success(t('admin.inflation.sync_requested'));
      void queryClient.invalidateQueries({ queryKey: ['get', INFLATION] });
      void queryClient.invalidateQueries({ queryKey: ['get', RUNS] });
    },
    onError: (error) => {
      // Not a failure to explain: the update the admin asked for is already under way.
      if (toProblem(error).code === 'core.cpi_sync_running') {
        setProblem(null);
        toast.info(t('admin.inflation.already_running'));
      } else fail(error);
    },
  });
  const title = t('admin.inflation.title');
  if (household.error) return <AdminFrame title={title}><ProblemMessage problem={present(toProblem(household.error))} /></AdminFrame>;
  if (!household.data) return <AdminFrame title={title}><Loading label={t('admin.loading')}><SectionSkeleton /></Loading></AdminFrame>;
  const enabled = household.data.inflationEnabled;

  return (
    <AdminFrame title={title}>
      {problem ? <ProblemMessage problem={problem} /> : null}

      <section className="space-y-3">
        <SwitchField label={t('admin.inflation.enabled')} checked={enabled} onCheckedChange={(inflationEnabled) => { save.mutate({ body: { inflationEnabled } }); }} />
        <p className="max-w-prose text-sm text-muted-foreground">{t('admin.inflation.hint')}</p>
        <ConfirmAction label={t('admin.inflation.sync')} confirmation={t('admin.inflation.sync_confirm')} disabled={!enabled} pending={sync.isPending}
          onConfirm={() => sync.mutate({})} />
      </section>

      <ManualMonth />

      <DetailTabs value={tab} onChange={setTab} tabs={[
        { id: 'series', label: t('admin.tab_series'), content: (
          <>
            <AdminSeries seriesKey="cpi" />
            <InflationValues />
          </>
        ) },
        { id: 'journal', label: t('admin.tab_journal'), content: (
          <InflationRuns />
        ) },
      ]} />
    </AdminFrame>
  );
}

/** The stored months, newest first, a page at a time. */
function InflationValues() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const [page, toPage] = useListPage();
  const inflation = $api.useQuery('get', INFLATION, { params: { query: { page } } });
  useStepBack(page, inflation.data, toPage);
  return (
    <Section title={t('admin.inflation.values_title')} actions={<SeriesLink keys={['cpi']} />}>
      {inflation.error ? <ProblemMessage problem={present(toProblem(inflation.error))} /> : null}
      {!inflation.error && (!inflation.data || (inflation.data.items.length === 0 && inflation.data.total > 0)) ? <Loading label={t('admin.loading')}><TableSkeleton columns={4} /></Loading> : null}
      {inflation.data && inflation.data.total === 0 ? <EmptyState module="/admin" title={t('admin.inflation.no_values')} body="" level={3} /> : null}
      {inflation.data && inflation.data.items.length > 0 ? (
        <>
          <SortableTable caption={t('admin.inflation.values_title')} rows={inflation.data.items} rowKey={(v) => v.month}
            columns={[
              { id: 'month', header: t('admin.inflation.month'), cell: (v) => monthText(v.month) },
              { id: 'value', header: t('admin.inflation.value'), cell: (v) => <span className="tabular-nums">{v.value}</span> },
              { id: 'source', header: t('admin.inflation.source'), cell: (v) => (v.source === 'insee' ? t('admin.inflation.source_insee') : t('admin.inflation.source_manual')) },
              { id: 'fetched', header: t('admin.inflation.fetched'), cell: (v) => formatLocalDate(v.fetchedOn) },
            ]}
            mobile={(v) => (
              <>
                <p className="font-medium">{monthText(v.month)} · <span className="tabular-nums">{v.value}</span></p>
                <p className="text-sm text-muted-foreground">{v.source === 'insee' ? t('admin.inflation.source_insee') : t('admin.inflation.source_manual')} · {formatLocalDate(v.fetchedOn)}</p>
              </>
            )} />
          <Pager page={page} total={inflation.data.total} onPage={toPage} />
        </>
      ) : null}
    </Section>
  );
}

/** The collection journal, newest first, a page at a time. */
function InflationRuns() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const [page, toPage] = useListPage();
  const runs = $api.useQuery('get', RUNS, { params: { query: { page } } });
  useStepBack(page, runs.data, toPage);
  // The wire carries plain strings; an unknown value is shown as it came rather than hidden.
  const statusLabel = (status: string) => (status in STATUS ? t(STATUS[status as keyof typeof STATUS]) : status);
  return (
    <Section title={t('admin.inflation.runs_title')}>
      {runs.error ? <ProblemMessage problem={present(toProblem(runs.error))} /> : null}
      {!runs.error && (!runs.data || (runs.data.items.length === 0 && runs.data.total > 0)) ? <Loading label={t('admin.loading')}><TableSkeleton columns={6} /></Loading> : null}
      {runs.data && runs.data.total === 0 ? <EmptyState module="/admin" title={t('admin.inflation.no_runs')} body="" level={3} /> : null}
      {runs.data && runs.data.items.length > 0 ? (
        <>
          <SortableTable caption={t('admin.inflation.runs_title')} rows={runs.data.items} rowKey={(r) => r.id}
            columns={[
              { id: 'started', header: t('admin.inflation.started'), cell: (r) => formatInstant(r.startedAt) },
              { id: 'status', header: t('admin.inflation.status'), cell: (r) => statusLabel(r.status) },
              { id: 'added', header: t('admin.inflation.added'), cell: (r) => r.added },
              { id: 'replaced', header: t('admin.inflation.replaced'), cell: (r) => r.replaced },
              { id: 'rejected', header: t('admin.inflation.rejected'), cell: (r) => r.rejected },
              { id: 'message', header: t('admin.inflation.message'), cell: (r) => r.message },
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

/** A month INSEE has published but Nid has not read, typed by hand: the server refuses one INSEE already gave, on the value. */
function ManualMonth() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const [year, setYear] = useState('');
  const [month, setMonth] = useState('');
  const [value, setValue] = useState('');
  const [errors, setErrors] = useState<{ year?: string; month?: string; value?: string }>({});
  const [problem, setProblem] = useState<Presentation | null>(null);
  const put = $api.useMutation('put', '/api/v1/core/inflation/{year}/{month}');

  const submit = () => {
    // A French keyboard types a comma; the wire wants a decimal point.
    const wire = value.trim().replaceAll(',', '.');
    const required = (ok: boolean) => (ok ? undefined : t('edit.required'));
    const next = { year: required(!!year), month: required(!!month), value: required(!!wire) };
    setErrors(next);
    setProblem(null);
    if (next.year || next.month || next.value) return;
    put.mutate({ params: { path: { year: Number(year), month: Number(month) } }, body: { value: wire } }, {
      onSuccess: () => { setValue(''); toast.success(t('admin.inflation.saved')); void queryClient.invalidateQueries({ queryKey: ['get', INFLATION] }); },
      onError: (error) => {
        const fields = fieldErrorsOf(error);
        const message = (path: string) => { const f = fields.find((x) => x.path === path); return f ? messageFor(t, f) : undefined; };
        setErrors({ month: fields.some((f) => f.path === 'month') ? t('admin.inflation.month_not_over') : undefined, value: message('value') });
        if (!fields.some((f) => f.path === 'month' || f.path === 'value')) setProblem(refusal(t, error));
      },
    });
  };

  return (
    <Section title={t('admin.inflation.manual_title')}>
      <p className="max-w-prose text-sm text-muted-foreground">{t('admin.inflation.manual_hint')}</p>
      <form aria-label={t('admin.inflation.manual_title')} noValidate className="max-w-md space-y-3" onSubmit={(e) => { e.preventDefault(); submit(); }}>
        {problem ? <ProblemMessage problem={problem} /> : null}
        <Field label={t('admin.inflation.year')} error={errors.year}>
          {(c) => <SelectField {...c} value={year} onValueChange={setYear} options={YEARS} />}
        </Field>
        <Field label={t('admin.inflation.manual_month')} error={errors.month}>
          {(c) => <SelectField {...c} value={month} onValueChange={setMonth} options={MONTHS} />}
        </Field>
        <Field label={t('admin.inflation.manual_value')} error={errors.value}>
          {(c) => <Input {...c} inputMode="decimal" value={value} onChange={(e) => setValue(e.target.value)} />}
        </Field>
        <Button type="submit" disabled={put.isPending}>{t('admin.inflation.manual_save')}</Button>
      </form>
    </Section>
  );
}
