import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { aiProblem, aiReason, USAGE, type LlmCall } from '@/api/ai';
import { EmptyState } from '@/components/EmptyState';
import { formatCurrency } from '@/components/Money';
import { Loading, TableSkeleton } from '@/components/page/skeletons';
import { Pager, useListPage, useStepBack } from '@/components/page/Pager';
import { SortableTable } from '@/components/page/SortableTable';
import { ProblemMessage } from '@/components/ProblemMessage';
import { formatInstant } from '@/lib/format';
import { SettingsFrame } from '../SettingsFrame';
import '@/locales/ai';

/**
 * Réglages › IA › Utilisation (#489, docs/ingestion/llm-providers.md §11, decision S): the reader's own model calls, newest
 * first, and the household's month for both adults. Never a call's content, never the other adult's calls.
 */
export function AiUsagePage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const [page, toPage] = useListPage();
  const usage = $api.useQuery('get', USAGE, { params: { query: { page } } });
  useStepBack(page, usage.data, toPage);

  const title = t('ai.usage.title');
  const eur = (amount: string) => formatCurrency(amount, 'EUR', { maximumFractionDigits: 4 });
  const labelOfUse = (value: string) => t(`ai.models.use.${value}` as 'ai.models.use.chat', { defaultValue: value });
  const destination = (call: LlmCall) => call.destination
    ? (call.destinationKind === 'cloud' ? t(`ai.cloud.provider.${call.destination}` as 'ai.cloud.provider.anthropic', { defaultValue: call.destination }) : call.destination)
    : '—';
  const outcome = (call: LlmCall) => call.status === 'succeeded' ? t('ai.usage.status_succeeded') : (aiReason(t, call.code) ?? t(`ai.usage.status_${call.status}` as 'ai.usage.status_error', { defaultValue: call.status }));

  return (
    <SettingsFrame title={title}>
      <p className="max-w-prose text-sm text-muted-foreground">{t('ai.usage.intro')}</p>
      {usage.error ? <ProblemMessage problem={aiProblem(t, usage.error)} /> : !usage.data ? (
        <Loading label={t('ai.loading')}><TableSkeleton columns={6} /></Loading>
      ) : (
        <>
          <p className="text-sm">
            {t('ai.usage.month', { count: usage.data.totals.calls, cost: eur(usage.data.totals.costEur), tokens: (usage.data.totals.tokensIn + usage.data.totals.tokensOut).toLocaleString('fr-FR') })}
          </p>
          {usage.data.total === 0 ? <EmptyState module="/settings/appearance" title={t('ai.usage.empty')} body={t('ai.usage.empty_body')} /> : (
            <>
              <SortableTable<LlmCall>
                caption={title}
                rows={usage.data.items}
                rowKey={(call) => call.id}
                columns={[
                  { id: 'when', header: t('ai.usage.when'), cell: (call) => formatInstant(call.createdAt) },
                  { id: 'use', header: t('ai.usage.use'), cell: (call) => labelOfUse(call.use) },
                  { id: 'where', header: t('ai.usage.where'), cell: destination },
                  { id: 'model', header: t('ai.usage.model'), cell: (call) => call.model ?? '—' },
                  { id: 'tokens', header: t('ai.usage.tokens'), align: 'end', cell: (call) => t('ai.usage.tokens_value', { in: call.tokensIn, out: call.tokensOut }) },
                  { id: 'cost', header: t('ai.usage.cost'), align: 'end', cell: (call) => (call.costEur ? eur(call.costEur) : '—') },
                  { id: 'outcome', header: t('ai.usage.outcome'), wrap: true, cell: outcome },
                ]}
                mobile={(call) => (
                  <div className="space-y-1">
                    <div className="flex items-center justify-between gap-2"><span>{labelOfUse(call.use)}</span><span className="text-sm text-muted-foreground">{formatInstant(call.createdAt)}</span></div>
                    <p className="text-sm text-muted-foreground">{[destination(call), call.costEur ? eur(call.costEur) : null].filter(Boolean).join(' · ')}</p>
                    <p className="text-sm">{outcome(call)}</p>
                  </div>
                )}
              />
              <Pager page={page} total={usage.data.total} onPage={toPage} />
            </>
          )}
        </>
      )}
    </SettingsFrame>
  );
}
