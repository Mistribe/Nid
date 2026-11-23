import { useQueryClient } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { useState } from 'react';
import type { TFunction } from 'i18next';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem, type Presentation } from '@/api/errors';
import type { components } from '@/api/schema';
import { Field } from '@/components/form/Field';
import { SelectField } from '@/components/form/SelectField';
import { EmptyState } from '@/components/EmptyState';
import { InfoTip } from '@/components/InfoTip';
import { ProblemMessage } from '@/components/ProblemMessage';
import { SwitchField } from '@/components/page/SwitchField';
import { Badge } from '@/components/ui/badge';
import { buttonVariants } from '@/components/ui/button';
import { Loading, SectionSkeleton, StatLineSkeleton, TableSkeleton } from '@/components/page/skeletons';
import { Pager, useListPage, useStepBack } from '@/components/page/Pager';
import { Section } from '@/components/page/Section';
import { SortableTable } from '@/components/page/SortableTable';
import { StatLine } from '@/components/page/StatLine';
import { AdminFrame } from './AdminFrame';
import { formatInstant, formatInstantDate, formatInstantTime } from '@/lib/format';
import { cn } from '@/lib/utils';
import '@/locales/admin';

const HOUSEHOLD = '/api/v1/admin/household';
const RUNS = '/api/v1/admin/banking/runs';
const DIAGNOSTICS = '/api/v1/admin/banking/diagnostics';

type Diagnostics = components['schemas']['BankingDiagnosticsView'];
type Run = components['schemas']['BankSyncRunView'];
type State = 'disabled' | 'setup' | 'ready' | 'error';

/** « Conserver les opérations » (D3): the choices offered, in months; « Sans limite » clears the setting. */
const RETENTION = [12, 24, 36, 60, 120];
const TRIGGER = { schedule: 'admin.banking.trigger_schedule', manual: 'admin.banking.trigger_manual' } as const;
const OUTCOME = { running: 'admin.banking.outcome_running', succeeded: 'admin.banking.outcome_succeeded', failed: 'admin.banking.outcome_failed' } as const;
// Status colours are the fixed ones (dataviz.md); the label always says it too.
const OUTCOME_TONE: Record<string, string> = {
  running: 'bg-muted text-muted-foreground',
  succeeded: 'bg-status-good/15 text-status-good-text',
  failed: 'bg-status-critical/15 text-foreground',
};
const STATE = {
  disabled: 'admin.banking.state_disabled', setup: 'admin.banking.state_setup', ready: 'admin.banking.state_ready', error: 'admin.banking.state_error',
} as const;
/** The page's state (#344): off; no adult with an application of their own (#414) or no bank linked yet; the last run failed; else ready. */
function stateOf(enabled: boolean, diagnostics: Diagnostics): State {
  if (!enabled) return 'disabled';
  if (!diagnostics.ownApplications?.length) return 'setup';
  if (diagnostics.lastRun?.outcome === 'failed') return 'error';
  return diagnostics.bankLinked ? 'ready' : 'setup';
}

/** Administration › Banque: where bank sync stands, what is left to set it up, its settings, and the journal of the syncs (counts only). */
export function BankingPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const [problem, setProblem] = useState<Presentation | null>(null);
  const household = $api.useQuery('get', HOUSEHOLD);
  const diagnostics = $api.useQuery('get', DIAGNOSTICS);
  const [page, toPage] = useListPage();
  const runs = $api.useQuery('get', RUNS, { params: { query: { page } } });
  useStepBack(page, runs.data, toPage);
  const save = $api.useMutation('patch', HOUSEHOLD, {
    onSuccess: () => {
      setProblem(null);
      toast.success(t('admin.banking.saved'));
      void queryClient.invalidateQueries({ queryKey: ['get', HOUSEHOLD] });
      void queryClient.invalidateQueries({ queryKey: ['get', DIAGNOSTICS] });
      // The settings tabs and the envelope pages ask whether bank sync is on.
      void queryClient.invalidateQueries({ queryKey: ['get', '/api/v1/banking/status'] });
    },
    onError: (error) => { setProblem(present(toProblem(error))); },
  });
  const title = t('admin.banking.title');
  if (household.error ?? diagnostics.error) {
    return <AdminFrame title={title}><ProblemMessage problem={present(toProblem(household.error ?? diagnostics.error))} /></AdminFrame>;
  }
  if (!household.data || !diagnostics.data) {
    return <AdminFrame title={title}><Loading label={t('admin.loading')}><StatLineSkeleton items={3} /><SectionSkeleton /></Loading></AdminFrame>;
  }
  const settings = household.data;
  const d = diagnostics.data;
  const state = stateOf(settings.bankingEnabled, d);
  const retention = settings.bankRetentionMonths;

  return (
    <AdminFrame title={title}
      header={(
        <section aria-label={t('admin.banking.figures')} className="space-y-2">
          <StatLine items={[
            { label: t('admin.banking.state'), value: t(STATE[state]), tip: t('admin.banking.state_tip') },
            d.lastRun
              ? { label: t('admin.banking.last_run'), value: formatInstantDate(d.lastRun.startedAt),
                hint: t('admin.banking.run_hint', { time: formatInstantTime(d.lastRun.startedAt), outcome: outcomeText(t, d.lastRun.outcome) }) }
              : { label: t('admin.banking.last_run'), value: t('admin.banking.last_run_none') },
            d.nextRunAt
              ? { label: t('admin.banking.next_run'), value: formatInstantDate(d.nextRunAt),
                hint: settings.bankingEnabled ? t('admin.banking.next_run_hint', { time: formatInstantTime(d.nextRunAt) }) : t('admin.banking.next_run_off_hint') }
              : { label: t('admin.banking.next_run'), value: t('admin.banking.next_run_none'), hint: t('admin.banking.next_run_none_hint') },
            // #365: Nid's own count, never the bank's quota; counts only, no bank or account named.
            ...(d.reads ? [{
              label: t('admin.banking.reads'), tip: t('admin.banking.reads_tip'),
              value: t('admin.banking.reads_value', { count: d.reads.unattended, limit: d.reads.limit }),
              hint: d.reads.limitedAt ? t('admin.banking.reads_limited', { date: formatInstant(d.reads.limitedAt) })
                : d.reads.freesAt ? t('admin.banking.reads_frees', { time: formatInstantTime(d.reads.freesAt) }) : t('admin.banking.reads_estimate'),
            }] : []),
          ]} />
        </section>
      )}>
      {problem ? <ProblemMessage problem={problem} /> : null}

      <Section title={t('admin.banking.own_apps')}>
        <p className="text-sm">{d.ownApplications?.length ? d.ownApplications.map((a) => a.name).join(', ') : t('admin.banking.own_apps_none')}</p>
      </Section>

      {state === 'setup' ? (
        <Section title={t('admin.banking.setup_title')}>
          <p className="max-w-prose text-sm">
            {t('admin.banking.setup_own')}{' '}
            <Link to="/settings/banks" search={{ tab: 'application' }} className="underline underline-offset-4">{t('admin.banking.setup_own_open')}</Link>
          </p>
        </Section>
      ) : null}

      <Section title={t('admin.banking.settings_title')}>
        <div className="max-w-prose space-y-4">
          <SwitchField label={t('admin.banking.enabled')} hint={t('admin.banking.hint')} checked={settings.bankingEnabled}
            onCheckedChange={(bankingEnabled) => { save.mutate({ body: { bankingEnabled } }); }} />
          <p className="text-sm text-muted-foreground">{t('admin.banking.provider')}</p>
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
        </div>
      </Section>

      <Section title={t('admin.banking.runs_title')}>
        {runs.error ? <ProblemMessage problem={present(toProblem(runs.error))} /> : null}
        {!runs.error && (!runs.data || (runs.data.items.length === 0 && runs.data.total > 0)) ? <Loading label={t('admin.loading')}><TableSkeleton columns={11} /></Loading> : null}
        {runs.data && runs.data.total === 0 ? (
          <EmptyState module="/admin" level={3} title={t('admin.banking.no_runs')}
            body={d.nextRunAt ? t('admin.banking.no_runs_next', { date: formatInstant(d.nextRunAt) }) : t('admin.banking.no_runs_unscheduled')}
            action={<Link to="/settings/banks" className={buttonVariants({ variant: 'outline' })}>{t('admin.banking.step_bank_open')}</Link>} />
        ) : null}
        {runs.data && runs.data.items.length > 0 ? (
          <>
            <SortableTable caption={t('admin.banking.runs_title')} rows={runs.data.items} rowKey={(r) => r.id}
              columns={[
                { id: 'started', header: t('admin.banking.started'), cell: (r) => formatInstant(r.startedAt) },
                { id: 'trigger', header: t('admin.banking.trigger'), cell: (r) => triggerText(t, r.trigger) },
                { id: 'outcome', header: t('admin.banking.outcome'), cell: (r) => <Outcome outcome={r.outcome} /> },
                { id: 'connections', header: t('admin.banking.connections'), cell: (r) => r.connections },
                { id: 'accounts', header: t('admin.banking.accounts'), cell: (r) => r.accounts },
                { id: 'balances', header: t('admin.banking.balances'), cell: (r) => r.balances },
                { id: 'proposals', header: t('admin.banking.proposals'), cell: (r) => r.proposals },
                { id: 'written', header: t('admin.banking.written'), cell: (r) => r.written },
                { id: 'skipped', header: t('admin.banking.skipped'), cell: (r) => r.skipped },
                { id: 'rate_limited', header: t('admin.banking.rate_limited'), cell: (r) => r.rateLimited ?? 0 },
                { id: 'failure', header: t('admin.banking.failure'), wrap: true, className: 'min-w-56', cell: (r) => <Failure run={r} /> },
              ]}
              mobile={(r) => (
                <>
                  <p className="flex flex-wrap items-center gap-2 font-medium">{formatInstant(r.startedAt)} · {triggerText(t, r.trigger)} <Outcome outcome={r.outcome} /></p>
                  <p className="text-sm text-muted-foreground">
                    {t('admin.banking.connections')} {r.connections} · {t('admin.banking.accounts')} {r.accounts} · {t('admin.banking.balances')} {r.balances} · {t('admin.banking.proposals')} {r.proposals} · {t('admin.banking.written')} {r.written} · {t('admin.banking.skipped')} {r.skipped} · {t('admin.banking.rate_limited')} {r.rateLimited ?? 0}
                  </p>
                  {r.failureCode ? <p className="text-sm"><Failure run={r} /></p> : null}
                </>
              )} />
            <Pager page={page} total={runs.data.total} onPage={toPage} />
          </>
        ) : null}
      </Section>
    </AdminFrame>
  );
}

// The wire carries plain strings; an unknown value is shown as it came rather than hidden.
const triggerText = (t: TFunction, trigger: string): string => (trigger in TRIGGER ? t(TRIGGER[trigger as keyof typeof TRIGGER]) : trigger);
const outcomeText = (t: TFunction, outcome: string): string => (outcome in OUTCOME ? t(OUTCOME[outcome as keyof typeof OUTCOME]) : outcome);
/** A journal code as a `failures` key (its dots would read as nesting); an unknown one reads as a provider's or a plain failure. */
const failureText = (t: TFunction, code: string): string => t(`admin.banking.failures.${code.replaceAll('.', '_')}`, {
  defaultValue: code.startsWith('enablebanking.') ? t('admin.banking.failure_provider') : t('admin.banking.failure_generic'),
});

function Outcome({ outcome }: { outcome: string }) {
  const { t } = useTranslation();
  return <Badge variant="outline" className={cn('border-transparent', OUTCOME_TONE[outcome] ?? 'bg-muted text-muted-foreground')}>{outcomeText(t, outcome)}</Badge>;
}

/** A failed run's code as a sentence, the code itself in an InfoTip for whoever reads the logs. */
function Failure({ run }: { run: Run }) {
  const { t } = useTranslation();
  if (!run.failureCode) return null;
  return (
    <span>
      {failureText(t, run.failureCode)}{' '}
      <InfoTip label={t('admin.banking.failure')}>{t('admin.banking.failure_code', { code: run.failureCode })}</InfoTip>
    </span>
  );
}
