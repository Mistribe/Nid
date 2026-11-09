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
import { ProblemMessage } from '@/components/ProblemMessage';
import { SeriesLink } from '@/components/page/SeriesLink';
import { SwitchField } from '@/components/page/SwitchField';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { fieldErrorsOf, messageFor } from '@/components/form/fieldErrors';
import { todayIso } from '@/lib/today';
import { AdminFrame, AdminSkeleton } from './AdminFrame';
import { formatInstant } from '@/lib/format';
import '@/locales/admin';
import '@/locales/forms';

const HOUSEHOLD = '/api/v1/admin/household';
const IRL = '/api/v1/realestate/irl';

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
  const household = $api.useQuery('get', HOUSEHOLD);
  const irl = $api.useQuery('get', IRL);

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
    },
    onError: (error) => {
      // Not a failure to explain: the update the admin asked for is already under way.
      if (toProblem(error).code === 'realestate.irl_sync_running') {
        setProblem(null);
        toast.info(t('admin.irl.already_running'));
      } else fail(error);
    },
  });
  // The wire carries plain strings; an unknown value is shown as it came rather than hidden.
  const statusLabel = (status: string) => (status in STATUS ? t(STATUS[status as keyof typeof STATUS]) : status);

  const title = t('admin.irl.title');
  if (household.error) return <AdminFrame title={title}><ProblemMessage problem={present(toProblem(household.error))} /></AdminFrame>;
  if (!household.data) return <AdminFrame title={title}><AdminSkeleton /></AdminFrame>;
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

      {irl.error ? <ProblemMessage problem={present(toProblem(irl.error))} /> : null}
      {!irl.data && !irl.error ? <AdminSkeleton /> : null}

      <section className="space-y-2">
        <div className="flex items-center justify-between">
          <h2 className="text-lg font-semibold">{t('admin.irl.values_title')}</h2>
          <SeriesLink keys={['irl']} />
        </div>
        {irl.data && irl.data.values.length === 0 ? <p>{t('admin.irl.no_values')}</p> : null}
        {irl.data && irl.data.values.length > 0 ? (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>{t('admin.irl.quarter')}</TableHead>
                <TableHead>{t('admin.irl.value')}</TableHead>
                <TableHead>{t('admin.irl.published')}</TableHead>
                <TableHead>{t('admin.irl.fetched')}</TableHead>
                <TableHead>{t('admin.irl.source')}</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {irl.data.values.map((v) => (
                <TableRow key={v.quarter}>
                  <TableCell>{quarterText(v.quarter)}</TableCell>
                  <TableCell className="tabular-nums">{v.value}</TableCell>
                  <TableCell>{v.publishedOn ? formatLocalDate(v.publishedOn) : null}</TableCell>
                  <TableCell>{formatLocalDate(v.fetchedOn)}</TableCell>
                  <TableCell>{v.source === 'insee' ? t('admin.irl.source_insee') : t('admin.irl.source_manual')}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        ) : null}
      </section>

      <ManualQuarter />

      <section className="space-y-2">
        <h2 className="text-lg font-semibold">{t('admin.irl.runs_title')}</h2>
        {irl.data && irl.data.runs.length === 0 ? <p>{t('admin.irl.no_runs')}</p> : null}
        {irl.data && irl.data.runs.length > 0 ? (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>{t('admin.irl.started')}</TableHead>
                <TableHead>{t('admin.irl.status')}</TableHead>
                <TableHead>{t('admin.irl.added')}</TableHead>
                <TableHead>{t('admin.irl.replaced')}</TableHead>
                <TableHead>{t('admin.irl.rejected')}</TableHead>
                <TableHead>{t('admin.irl.message')}</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {irl.data.runs.map((r) => (
                <TableRow key={r.id}>
                  <TableCell>{formatInstant(r.startedAt)}</TableCell>
                  <TableCell>{statusLabel(r.status)}</TableCell>
                  <TableCell>{r.added}</TableCell>
                  <TableCell>{r.replaced}</TableCell>
                  <TableCell>{r.rejected}</TableCell>
                  <TableCell>{r.message}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        ) : null}
      </section>
    </AdminFrame>
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
    <section className="space-y-2">
      <h2 className="text-lg font-semibold">{t('admin.irl.manual_title')}</h2>
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
    </section>
  );
}
