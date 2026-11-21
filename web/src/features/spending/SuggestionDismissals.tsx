import { useQueryClient } from '@tanstack/react-query';
import { useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { useApi } from '@/api/ApiProvider';
import { CATEGORY_SUGGESTIONS, presentBankError, SUGGESTION_DISMISSAL, SUGGESTION_DISMISSALS, type SuggestionDismissal } from '@/api/banking';
import { Section } from '@/components/page/Section';
import { SortableTable } from '@/components/page/SortableTable';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { formatInstantDay } from '@/lib/format';
import '@/locales/banks';

/** Dépenses › Catégories › « Suggestions ignorées » (#449): the dismissed suggestions, each brought back with one click; nothing when there are none. */
export function SuggestionDismissals() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const dismissals = $api.useQuery('get', SUGGESTION_DISMISSALS);
  const withdraw = $api.useMutation('delete', SUGGESTION_DISMISSAL, {
    onSuccess: () => { void queryClient.invalidateQueries({ predicate: (q) => typeof q.queryKey[1] === 'string' && q.queryKey[1].startsWith(CATEGORY_SUGGESTIONS) }); },
  });
  const heading = useRef<HTMLHeadingElement>(null);
  if (!dismissals.data?.length) return null;

  const pattern = (d: SuggestionDismissal) => (
    <span className="font-medium">{d.pattern}{d.direction === 'credit' ? <span className="font-normal text-muted-foreground"> · {t('banks.suggestions.credits')}</span> : null}</span>
  );
  const restore = (d: SuggestionDismissal) => (
    <Button type="button" size="sm" variant="outline" disabled={withdraw.isPending} aria-label={t('banks.suggestions.restore_of', { pattern: d.pattern, category: d.categoryName })}
      onClick={() => withdraw.mutate({ params: { path: { id: d.id } } }, { onSuccess: () => { toast.success(t('banks.suggestions.restored')); heading.current?.focus(); } })}>
      {t('banks.suggestions.restore')}
    </Button>
  );
  return (
    <Section title={t('banks.suggestions.dismissed_title')} headingRef={heading}>
      <p className="text-sm text-muted-foreground">{t('banks.suggestions.dismissed_hint')}</p>
      {withdraw.error ? <ProblemMessage problem={presentBankError(t, withdraw.error)} /> : null}
      <SortableTable<SuggestionDismissal> caption={t('banks.suggestions.dismissed_title')} rows={dismissals.data} rowKey={(d) => d.id}
        initialSort={{ id: 'pattern', desc: false }}
        columns={[
          { id: 'pattern', header: t('banks.category_rules.pattern'), sort: (d) => d.pattern, cell: pattern },
          { id: 'category', header: t('banks.category_rules.category'), sort: (d) => d.categoryName, cell: (d) => d.categoryName },
          { id: 'at', header: t('banks.suggestions.dismissed_at'), sort: (d) => d.createdAt, cell: (d) => formatInstantDay(d.createdAt) },
          { id: 'actions', header: t('banks.actions'), align: 'end', cell: restore },
        ]}
        mobile={(d) => (
          <div className="flex items-start justify-between gap-2">
            <div>{pattern(d)}<p className="text-sm text-muted-foreground">{d.categoryName} · {formatInstantDay(d.createdAt)}</p></div>
            {restore(d)}
          </div>
        )} />
    </Section>
  );
}
