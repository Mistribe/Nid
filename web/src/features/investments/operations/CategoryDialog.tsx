import { useEffect, useId, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { present, toProblem } from '@/api/errors';
import { FormDialog } from '@/components/FormDialog';
import { ComboboxField } from '@/components/form/ComboboxField';
import { Field } from '@/components/form/Field';
import { fieldErrorsOf } from '@/components/form/fieldErrors';
import { LocalDate } from '@/components/LocalDate';
import { formatCurrency } from '@/components/Money';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Checkbox } from '@/components/ui/checkbox';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { useApi } from '@/api/ApiProvider';
import {
  activeCategoryOptions, bankErrorText, CATEGORY_RULE_DRAFT_PREVIEW, presentBankError, useCategories, type BankTransaction, type CategoryRuleDraftPreview,
} from '@/api/banking';
import { useLineActions } from './api';
import '@/locales/banks';

// Not category ids (those are uuids): the two choices that are no category.
const NONE = 'none';
const AUTO = 'auto';

/**
 * A line's category by hand: an active category (« Parent › Enfant », a parent alone too), « Sans catégorie », or back to Nid's own choice.
 * A category chosen for a line with a suggested pattern then offers a rule for the next ones.
 * #426: several lines take one category at once (no « Laisser Nid décider »); the lines the server refused are named in the message, and the
 * rule is offered when every line it changed suggests the same pattern.
 */
export default function CategoryDialog({ lines, onClose, onDone }: { lines: BankTransaction[]; onClose: () => void; onDone: (message: string) => void }) {
  const { t } = useTranslation();
  const { categorise, categoriseMany, resetCategory } = useLineActions();
  const categories = useCategories();
  const many = lines.length > 1;
  const first = lines[0]!;
  const [picked, setPicked] = useState(many ? '' : first.category?.id ?? (first.categorySource === 'hand' ? NONE : ''));
  // Once saved, the dialog offers a rule instead of closing: the chosen category's name, the line it is built from, what was refused.
  const [offer, setOffer] = useState<{ category: string; line: BankTransaction; skipped?: string } | null>(null);
  const options = [
    { value: NONE, label: t('banks.categories.uncategorised') },
    ...(many ? [] : [{ value: AUTO, label: t('banks.categories.auto') }]),
    ...activeCategoryOptions(categories.data),
  ];
  // The line's own category may be archived by now: not on the list, so not sent back as is.
  const chosen = options.some((o) => o.value === picked);
  const gone = !many && categories.data && first.category && picked === first.category.id && !chosen ? first.category.name : null;
  // Looking at a link's or a rule's category must not pin it as a hand choice; a hand « Sans catégorie » is already pinned.
  const unchanged = !many && (first.categorySource === 'hand' ? !first.category && picked === NONE : picked === first.category?.id);
  const problem = categories.error ?? categorise.error ?? categoriseMany.error ?? resetCategory.error;
  const name = picked !== NONE ? options.find((o) => o.value === picked)?.label : undefined;

  return (
    <FormDialog title={many ? t('banks.categories.title_many', { count: lines.length }) : t('banks.categories.title')} onClose={onClose} onDone={onDone}>
      {({ titleId, done }) => offer !== null ? <RuleOffer line={offer.line} category={offer.category} categoryId={picked} skipped={offer.skipped} done={done} /> : (
        <form aria-labelledby={titleId} noValidate className="space-y-4" onSubmit={(event) => {
          event.preventDefault();
          event.stopPropagation();
          if (!chosen) return;
          const categoryId = picked === NONE ? null : picked;
          if (many) {
            categoriseMany.mutate({ body: { transactionIds: lines.map((l) => l.id), categoryId } }, { onSuccess: (result) => {
              const changed = lines.filter((l) => result.changed.includes(l.id));
              const refused = result.refused.length > 0 ? t('banks.operations.bulk_refused', {
                count: result.refused.length,
                lines: result.refused.map((r) => `« ${lines.find((l) => l.id === r.id)?.label ?? r.id} » : ${bankErrorText(t, r.code, t('banks.operations.bulk_refused_reason'))}`).join(' '),
              }) : undefined;
              const pattern = changed[0]?.suggestedPattern;
              if (name && pattern && changed.every((l) => l.suggestedPattern === pattern)) setOffer({ category: name, line: changed[0]!, skipped: refused });
              else done([changed.length > 0 ? t('banks.operations.done_category_many', { count: changed.length }) : null, refused].filter(Boolean).join(' '));
            } });
            return;
          }
          const path = { params: { path: { id: first.id } } };
          const then = { onSuccess: () => done(t('banks.operations.done_category')) };
          if (picked === AUTO) resetCategory.mutate(path, then);
          else categorise.mutate({ ...path, body: { categoryId } }, name && first.suggestedPattern ? { onSuccess: () => setOffer({ category: name, line: first }) } : then);
        }}>
          {many ? null : <p className="text-sm text-muted-foreground">{first.label} · {formatCurrency(first.amount, first.currency)}</p>}
          {problem ? <ProblemMessage problem={present(toProblem(problem))} /> : null}
          <Field label={t('banks.categories.field')} hint={gone ? t('banks.categories.current_gone', { name: gone }) : undefined}>
            {(c) => <ComboboxField {...c} value={picked} onValueChange={setPicked} options={options} placeholder={t('banks.categories.pick')} searchPlaceholder={t('banks.categories.search')} empty={t('banks.categories.none_found')} />}
          </Field>
          <div className="flex justify-end gap-2">
            <Button type="button" variant="outline" onClick={() => done()}>{t('common.cancel')}</Button>
            <Button type="submit" disabled={!chosen || unchanged || categorise.isPending || categoriseMany.isPending || resetCategory.isPending}>{t('banks.categories.submit')}</Button>
          </div>
        </form>
      )}
    </FormDialog>
  );
}

/**
 * « Toujours classer « CARREFOUR CITY » en Courses ? »: the line's suggested pattern, editable; its account alone when not every adult reads it.
 * As the pattern is typed, the server previews the rule (#418): the past lines it would change, those it matches but would not, a too broad
 * pattern; the rule can then be applied to the past lines as it is created. #449: `suggested`, it is offered from a suggestion, no category
 * just saved: the past lines are taken by default, and « Non merci » just closes.
 */
function RuleOffer({ line, category, categoryId, skipped, suggested = false, done: close }: {
  line: BankTransaction; category: string; categoryId: string; skipped?: string; suggested?: boolean; done: (message?: string) => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { createRule, applyRule } = useLineActions();
  // The lines a bulk categorisation refused stay named, whatever the offer ends with.
  const done = (message: string) => close(skipped ? `${message} ${skipped}` : message);
  const [pattern, setPattern] = useState(line.suggestedPattern ?? '');
  const [only, setOnly] = useState(!line.sharedAccount);
  const [applyPast, setApplyPast] = useState(suggested);
  // The pattern the preview was asked for, a moment after the last key.
  const [asked, setAsked] = useState(pattern.trim());
  useEffect(() => { const timer = setTimeout(() => setAsked(pattern.trim()), 300); return () => clearTimeout(timer); }, [pattern]);
  const accountId = only ? line.accountId : null;
  const preview = $api.useQuery('post', CATEGORY_RULE_DRAFT_PREVIEW, { body: { pattern: asked, categoryId, accountId } }, { enabled: asked.length > 0, retry: false });
  const total = preview.data?.total ?? 0;
  const questionId = useId();
  const onlyId = useId();
  const applyId = useId();
  const question = useRef<HTMLParagraphElement>(null);
  // Filled after mount, so the live region is already there and announces it.
  const [saved, setSaved] = useState(false);
  // The form that had the focus is gone: the question takes it, so it is read first.
  useEffect(() => { question.current?.focus(); setSaved(true); }, []);
  const error = createRule.error ?? applyRule.error;
  const field = fieldErrorsOf(createRule.error).find((f) => f.path === 'pattern');
  const refused = error && !field
    ? toProblem(error).status === 403
      ? { ...present(toProblem(error)), detail: t('banks.categories.offer_forbidden') }
      : presentBankError(t, error)
    : null;

  return (
    <form aria-labelledby={questionId} noValidate className="space-y-4" onSubmit={(event) => {
      event.preventDefault();
      event.stopPropagation();
      createRule.mutate({ body: { pattern: pattern.trim(), categoryId, accountId } }, {
        onSuccess: (rule) => applyPast && total > 0
          ? applyRule.mutate({ params: { path: { id: rule.id } } }, { onSuccess: (r) => done(t('banks.categories.offer_done_apply', { count: r.changed })) })
          : done(t(suggested ? 'banks.category_rules.done_create' : 'banks.categories.offer_done')),
      });
    }}>
      <p role="status" className="text-sm text-muted-foreground">{saved && !suggested ? t('banks.categories.offer_saved') : ''}</p>
      <p id={questionId} ref={question} tabIndex={-1} className="font-medium outline-none">
        {t('banks.categories.offer', { pattern: pattern.trim() || line.suggestedPattern, category })}
      </p>
      {refused ? <ProblemMessage problem={refused} /> : null}
      <Field label={t('banks.category_rules.pattern')} hint={t('banks.categories.offer_hint')} error={field ? bankErrorText(t, field.code, field.message) : undefined}>
        {(c) => <Input {...c} value={pattern} maxLength={60} onChange={(event) => setPattern(event.target.value)} />}
      </Field>
      <div className="flex items-center gap-2">
        <Checkbox id={onlyId} checked={only} onCheckedChange={(checked) => setOnly(checked === true)} />
        <Label htmlFor={onlyId}>{t('banks.categories.offer_only_account', { account: line.accountName })}</Label>
      </div>
      {/* A pattern refused as typed shows nothing here: the field says why once the rule is asked for. */}
      {preview.isFetching && !preview.data ? <div role="status" aria-busy="true" aria-label={t('banks.categories.offer_preview_loading')} className="h-16 shimmer rounded-md border bg-muted" /> : null}
      {preview.data && asked ? <DraftPreview preview={preview.data} category={category} /> : null}
      {total > 0 && asked ? (
        <div className="flex items-center gap-2">
          <Checkbox id={applyId} checked={applyPast} onCheckedChange={(checked) => setApplyPast(checked === true)} />
          <Label htmlFor={applyId}>{t('banks.categories.offer_apply', { count: total })}</Label>
        </div>
      ) : null}
      <div className="flex justify-end gap-2">
        <Button type="button" variant="outline" onClick={() => (suggested ? close() : done(t('banks.operations.done_category')))}>{t('banks.categories.offer_decline')}</Button>
        <Button type="submit" disabled={!pattern.trim() || createRule.isPending || applyRule.isPending}>{t('banks.categories.offer_create')}</Button>
      </div>
    </form>
  );
}

/** #449: the rule offer of a suggestion, its pattern and category already chosen, in a dialog of its own. */
export function RuleOfferDialog({ line, category, categoryId, onClose, onDone }: {
  line: BankTransaction; category: string; categoryId: string; onClose: () => void; onDone: (message: string) => void;
}) {
  const { t } = useTranslation();
  return (
    <FormDialog title={t('banks.suggestions.rule_title')} onClose={onClose} onDone={onDone}>
      {({ done }) => <RuleOffer line={line} category={category} categoryId={categoryId} suggested done={done} />}
    </FormDialog>
  );
}

/** What the server says the draft would do: how many past lines it changes, the most recent, the matched lines it leaves by reason. */
function DraftPreview({ preview, category }: { preview: CategoryRuleDraftPreview; category: string }) {
  const { t } = useTranslation();
  const name = (value: string | null | undefined) => value ?? t('banks.categories.uncategorised');
  return (
    <div className="space-y-2 text-sm" aria-live="polite">
      {preview.tooBroad ? <p className="font-medium text-status-warning-text">{t('banks.categories.offer_too_broad')}</p> : null}
      <p>{preview.total > 0 ? t('banks.categories.offer_preview_count', { count: preview.total, category }) : t('banks.categories.offer_preview_none')}</p>
      {preview.items.length > 0 ? (
        <ul aria-label={t('banks.categories.offer_preview_recent')} className="space-y-1 text-muted-foreground">
          {preview.items.map((l) => (
            <li key={l.id} className="flex justify-between gap-2">
              <span className="min-w-0 truncate"><LocalDate value={l.bookedOn} style="short" /> · {l.label} · {name(l.category?.name)}</span>
              <span className="tabular-nums whitespace-nowrap">{formatCurrency(l.amount, l.currency)}</span>
            </li>
          ))}
        </ul>
      ) : null}
      {preview.conflicts.length > 0 ? (
        <>
          <p>{t('banks.categories.offer_conflicts')}</p>
          <ul className="list-disc space-y-1 pl-5 text-muted-foreground">
            {preview.conflicts.map((c) => (
              <li key={`${c.reason}-${c.rulePattern ?? ''}-${c.categoryId ?? ''}`}>
                {t(c.reason === 'hand' ? 'banks.categories.offer_conflict_hand' : c.reason === 'link' ? 'banks.categories.offer_conflict_link' : 'banks.categories.offer_conflict_rule', { count: c.lines, category: name(c.categoryName), pattern: c.rulePattern })}
              </li>
            ))}
          </ul>
        </>
      ) : null}
    </div>
  );
}
