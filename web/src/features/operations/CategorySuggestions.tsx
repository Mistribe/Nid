import { useQueryClient } from '@tanstack/react-query';
import { lazy, Suspense, useEffect, useRef, useState, type RefObject } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { useApi } from '@/api/ApiProvider';
import { CATEGORY_SUGGESTIONS, presentBankError, SUGGESTION_DISMISSALS, type CategorySuggestion } from '@/api/banking';
import { Folded } from '@/components/form/Folded';
import { InfoTip } from '@/components/InfoTip';
import { LocalDate } from '@/components/LocalDate';
import { formatCurrency } from '@/components/Money';
import { Pager } from '@/components/page/Pager';
import { Section } from '@/components/page/Section';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { useLineActions } from '../investments/operations/api';
import '@/locales/banks';

const RuleOfferDialog = lazy(() => import('../investments/operations/CategoryDialog').then((m) => ({ default: m.RuleOfferDialog })));

/** The server's page size of suggestions. */
const PAGE_SIZE = 10;

/**
 * #449: above the operations, the categories the household's own hand choices suggest for its uncategorised lines, the largest group first,
 * folded; nothing at all when there is nothing to suggest. Each one makes a rule, categorises its lines by hand, or is ignored for the household.
 * After a write, the focus goes to the block's heading, or to `emptied` (the page's own) once the write left nothing to suggest.
 */
export function CategorySuggestions({ emptied }: { emptied?: RefObject<HTMLElement | null> }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const [page, setPage] = useState(1);
  const answer = $api.useQuery('get', CATEGORY_SUGGESTIONS, { params: { query: { page } } });
  const { categoriseMany } = useLineActions();
  const dismiss = $api.useMutation('post', SUGGESTION_DISMISSALS, {
    onSuccess: () => { void queryClient.invalidateQueries({ predicate: (q) => typeof q.queryKey[1] === 'string' && q.queryKey[1].startsWith(CATEGORY_SUGGESTIONS) }); },
  });
  const [offer, setOffer] = useState<CategorySuggestion | null>(null);
  const heading = useRef<HTMLHeadingElement>(null);
  const data = answer.data;
  // The group a write removes takes its buttons with it: once the refetch the write started has answered, the focus goes to the
  // heading while groups remain, else to the page's own target.
  const [refocus, setRefocus] = useState(false);
  const settled = refocus && !answer.isFetching;
  const left = data?.total ?? 0;
  useEffect(() => {
    if (!settled) return;
    (left > 0 ? heading.current : emptied?.current)?.focus();
    setRefocus(false);
  }, [settled, left, emptied]);
  const done = (message: string) => { toast.success(message); setRefocus(true); };
  // Beside the block, at the same place whether it shows or not: a new rule takes the group's lines, so the block empties while the
  // offer still applies the rule to them, and the offer must stay mounted to finish and say so.
  const dialog = offer ? (
    <Suspense fallback={null}>
      <RuleOfferDialog line={offer.lines[0]!} category={offer.categoryName} categoryId={offer.categoryId}
        onClose={() => setOffer(null)} onDone={(message) => { setOffer(null); done(message); }} />
    </Suspense>
  ) : null;
  if (!data || data.total === 0) return <>{null}{dialog}</>;
  // A page emptied by the last write steps back.
  if (data.items.length === 0 && page > 1) setPage(Math.max(1, Math.ceil(data.total / PAGE_SIZE)));
  const error = categoriseMany.error ?? dismiss.error;

  return (
    <>
      <Section title={t('banks.suggestions.title', { count: data.total })} headingRef={heading}
        tip={<InfoTip label={t('banks.suggestions.title', { count: data.total })}>{t('banks.suggestions.about')}</InfoTip>}>
        {error ? <ProblemMessage problem={presentBankError(t, error)} /> : null}
        <Folded label={t('banks.suggestions.show', { count: data.total })} hasError={false}>
          <ul className="divide-y">
            {data.items.map((s) => (
              <Suggestion key={`${s.pattern}-${s.direction}`} suggestion={s} busy={categoriseMany.isPending || dismiss.isPending}
                onRule={() => setOffer(s)}
                onCategorise={() => categoriseMany.mutate({ body: { transactionIds: s.lines.map((l) => l.id), categoryId: s.categoryId } }, {
                  onSuccess: (result) => done(t('banks.suggestions.done_categorise', { count: result.changed.length, category: s.categoryName })),
                })}
                onIgnore={() => dismiss.mutate({ body: { pattern: s.pattern, direction: s.direction, categoryId: s.categoryId } }, {
                  onSuccess: () => done(t('banks.suggestions.done_ignore')),
                })} />
            ))}
          </ul>
          <Pager page={page} total={data.total} pageSize={PAGE_SIZE} label={t('banks.suggestions.pages')} onPage={setPage} />
        </Folded>
      </Section>
      {dialog}
    </>
  );
}

function Suggestion({ suggestion: s, busy, onRule, onCategorise, onIgnore }: {
  suggestion: CategorySuggestion; busy: boolean; onRule: () => void; onCategorise: () => void; onIgnore: () => void;
}) {
  const { t } = useTranslation();
  const more = s.lineCount - s.lines.length;
  // #450: evidence of patterns near this one by meaning, the pattern having none of its own.
  const evidence = s.byMeaning ? 'banks.suggestions.by_meaning' : 'banks.suggestions.evidence';
  return (
    <li className="space-y-2 py-3">
      <div className="flex flex-wrap items-baseline justify-between gap-x-4 gap-y-1">
        <p>
          <span className="font-medium">{s.pattern}</span> → {s.categoryName}
          {s.direction === 'credit' ? <span className="text-sm text-muted-foreground"> · {t('banks.suggestions.credits')}</span> : null}
        </p>
        <p className="flex items-center gap-1.5 text-sm text-muted-foreground">
          {t('banks.suggestions.lines', { count: s.lineCount })} · {t(evidence, { count: s.evidenceCount })}
          <InfoTip label={t(evidence, { count: s.evidenceCount })}>
            {t(s.byMeaning ? 'banks.suggestions.by_meaning_about' : 'banks.suggestions.evidence_about', { agreement: s.agreement, category: s.categoryName })}
          </InfoTip>
        </p>
      </div>
      <div className="flex flex-wrap gap-2">
        <Button type="button" size="sm" disabled={busy} onClick={onRule}>{t('banks.suggestions.create_rule')}</Button>
        <Button type="button" size="sm" variant="outline" disabled={busy} onClick={onCategorise}>
          {t('banks.suggestions.categorise', { count: s.lines.length })}
        </Button>
        <Button type="button" size="sm" variant="ghost" disabled={busy} onClick={onIgnore}>{t('banks.suggestions.ignore')}</Button>
      </div>
      <Folded label={t('banks.suggestions.show_lines')} hasError={false}>
        <ul aria-label={t('banks.suggestions.lines_of', { pattern: s.pattern })} className="space-y-1 text-sm text-muted-foreground">
          {s.lines.map((l) => (
            <li key={l.id} className="flex justify-between gap-2">
              <span className="min-w-0 truncate"><LocalDate value={l.bookedOn} style="short" /> · {l.label} · {l.accountName}</span>
              <span className="tabular-nums whitespace-nowrap">{formatCurrency(l.amount, l.currency)}</span>
            </li>
          ))}
          {more > 0 ? <li>{t('banks.suggestions.more_lines', { count: more })}</li> : null}
        </ul>
      </Folded>
    </li>
  );
}
