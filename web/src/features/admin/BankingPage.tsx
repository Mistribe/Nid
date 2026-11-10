import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem, type Presentation } from '@/api/errors';
import { Field } from '@/components/form/Field';
import { SelectField } from '@/components/form/SelectField';
import { EmptyState } from '@/components/EmptyState';
import { ProblemMessage } from '@/components/ProblemMessage';
import { SwitchField } from '@/components/page/SwitchField';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Loading, SectionSkeleton, TableSkeleton } from '@/components/page/skeletons';
import { Pager, useListPage, useStepBack } from '@/components/page/Pager';
import { Section } from '@/components/page/Section';
import { SortableTable } from '@/components/page/SortableTable';
import { AdminFrame } from './AdminFrame';
import { formatInstant } from '@/lib/format';
import '@/locales/admin';

const HOUSEHOLD = '/api/v1/admin/household';
const RUNS = '/api/v1/admin/banking/runs';

const STATUS = { running: 'admin.fx.status_running', succeeded: 'admin.fx.status_succeeded', failed: 'admin.fx.status_failed' } as const;
/** « Conserver les opérations » (D3): the choices offered, in months; « Sans limite » clears the setting. */
const RETENTION = [12, 24, 36, 60, 120];
const TRIGGER = { schedule: 'admin.banking.trigger_schedule', manual: 'admin.banking.trigger_manual' } as const;

/** Administration › Banque: bank sync on or off, the Enable Banking application, how long the operations are kept, and the journal of the syncs (counts only). */
export function BankingPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const [problem, setProblem] = useState<Presentation | null>(null);
  const [appId, setAppId] = useState<string | null>(null);
  const household = $api.useQuery('get', HOUSEHOLD);
  const [page, toPage] = useListPage();
  const runs = $api.useQuery('get', RUNS, { params: { query: { page } } });
  useStepBack(page, runs.data, toPage);
  const save = $api.useMutation('patch', HOUSEHOLD, {
    onSuccess: () => {
      setProblem(null);
      toast.success(t('admin.banking.saved'));
      setAppId(null);
      void queryClient.invalidateQueries({ queryKey: ['get', HOUSEHOLD] });
      // The settings tabs and the envelope pages ask whether bank sync is on.
      void queryClient.invalidateQueries({ queryKey: ['get', '/api/v1/banking/status'] });
    },
    onError: (error) => { setProblem(present(toProblem(error))); },
  });
  // The wire carries plain strings; an unknown value is shown as it came rather than hidden.
  const outcomeLabel = (outcome: string) => (outcome in STATUS ? t(STATUS[outcome as keyof typeof STATUS]) : outcome);
  const triggerLabel = (trigger: string) => (trigger in TRIGGER ? t(TRIGGER[trigger as keyof typeof TRIGGER]) : trigger);

  const title = t('admin.banking.title');
  const retention = household.data?.bankRetentionMonths;
  if (household.error) return <AdminFrame title={title}><ProblemMessage problem={present(toProblem(household.error))} /></AdminFrame>;
  if (!household.data) return <AdminFrame title={title}><Loading label={t('admin.loading')}><SectionSkeleton /></Loading></AdminFrame>;

  return (
    <AdminFrame title={title}>
      {problem ? <ProblemMessage problem={problem} /> : null}

      <section className="max-w-prose space-y-3">
        <SwitchField label={t('admin.banking.enabled')} checked={household.data.bankingEnabled}
          onCheckedChange={(bankingEnabled) => { save.mutate({ body: { bankingEnabled } }); }} />
        <p className="text-sm text-muted-foreground">{t('admin.banking.hint')}</p>
        <p className="text-sm">{t('admin.banking.provider', { publicUrl: window.location.origin })}</p>
        <form noValidate className="flex flex-wrap items-end gap-2" onSubmit={(event) => {
          event.preventDefault();
          save.mutate({ body: { enableBankingAppId: appId ?? household.data.enableBankingAppId ?? '' } });
        }}>
          <div className="min-w-64 flex-1">
            <Field label={t('admin.banking.app_id')} hint={t('admin.banking.app_id_hint')}>
              {(c) => <Input {...c} value={appId ?? household.data.enableBankingAppId ?? ''} onChange={(e) => setAppId(e.target.value)} />}
            </Field>
          </div>
          <Button type="submit" variant="outline" disabled={save.isPending}>{t('admin.banking.save_app_id')}</Button>
        </form>
        <div className="max-w-64">
          <Field label={t('admin.banking.retention')} hint={t('admin.banking.retention_hint')}>
            {(c) => (
              <SelectField {...c} value={retention?.toString() ?? ''} disabled={save.isPending}
                onValueChange={(v) => { save.mutate({ body: v ? { bankRetentionMonths: Number(v) } : { clearBankRetention: true } }); }}
                options={[
                  { value: '', label: t('admin.banking.retention_none') },
                  // A value set through the API outside these choices is shown as it is.
                  ...[...RETENTION, ...(retention && !RETENTION.includes(retention) ? [retention] : [])].sort((a, b) => a - b)
                    .map((months) => ({ value: String(months), label: t('admin.banking.retention_months', { months }) })),
                ]} />
            )}
          </Field>
        </div>
      </section>

      <Section title={t('admin.banking.runs_title')}>
        {runs.error ? <ProblemMessage problem={present(toProblem(runs.error))} /> : null}
        {!runs.data && !runs.error ? <Loading label={t('admin.loading')}><TableSkeleton columns={10} /></Loading> : null}
        {runs.data && runs.data.items.length === 0 ? <EmptyState module="/admin" title={t('admin.banking.no_runs')} body="" level={3} /> : null}
        {runs.data && runs.data.items.length > 0 ? (
          <>
            <SortableTable caption={t('admin.banking.runs_title')} rows={runs.data.items} rowKey={(r) => r.id}
              columns={[
                { id: 'started', header: t('admin.banking.started'), cell: (r) => formatInstant(r.startedAt) },
                { id: 'trigger', header: t('admin.banking.trigger'), cell: (r) => triggerLabel(r.trigger) },
                { id: 'outcome', header: t('admin.banking.outcome'), cell: (r) => outcomeLabel(r.outcome) },
                { id: 'connections', header: t('admin.banking.connections'), cell: (r) => r.connections },
                { id: 'accounts', header: t('admin.banking.accounts'), cell: (r) => r.accounts },
                { id: 'balances', header: t('admin.banking.balances'), cell: (r) => r.balances },
                { id: 'proposals', header: t('admin.banking.proposals'), cell: (r) => r.proposals },
                { id: 'written', header: t('admin.banking.written'), cell: (r) => r.written },
                { id: 'skipped', header: t('admin.banking.skipped'), cell: (r) => r.skipped },
                { id: 'failure', header: t('admin.banking.failure'), cell: (r) => r.failureCode },
              ]}
              mobile={(r) => (
                <>
                  <p className="font-medium">{formatInstant(r.startedAt)} · {triggerLabel(r.trigger)} · {outcomeLabel(r.outcome)}</p>
                  <p className="text-sm text-muted-foreground">
                    {t('admin.banking.connections')} {r.connections} · {t('admin.banking.accounts')} {r.accounts} · {t('admin.banking.balances')} {r.balances} · {t('admin.banking.proposals')} {r.proposals} · {t('admin.banking.written')} {r.written} · {t('admin.banking.skipped')} {r.skipped}{r.failureCode ? ` · ${r.failureCode}` : ''}
                  </p>
                </>
              )} />
            <Pager page={page} total={runs.data.total} onPage={toPage} />
          </>
        ) : null}
      </Section>
    </AdminFrame>
  );
}
