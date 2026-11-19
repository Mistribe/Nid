import { useEffect, useId, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { present, toProblem } from '@/api/errors';
import { FormDialog } from '@/components/FormDialog';
import { ComboboxField } from '@/components/form/ComboboxField';
import { Field } from '@/components/form/Field';
import { fieldErrorsOf } from '@/components/form/fieldErrors';
import { formatCurrency } from '@/components/Money';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Checkbox } from '@/components/ui/checkbox';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { activeCategoryOptions, bankErrorText, presentBankError, useCategories, type BankTransaction } from '@/api/banking';
import { useLineActions } from './api';
import '@/locales/banks';

// Not category ids (those are uuids): the two choices that are no category.
const NONE = 'none';
const AUTO = 'auto';

/**
 * A line's category by hand: an active category (« Parent › Enfant », a parent alone too), « Sans catégorie », or back to Nid's own choice.
 * A category chosen for a line with a suggested pattern then offers a rule for the next ones.
 */
export default function CategoryDialog({ line, onClose, onDone }: { line: BankTransaction; onClose: () => void; onDone: (message: string) => void }) {
  const { t } = useTranslation();
  const { categorise, resetCategory } = useLineActions();
  const categories = useCategories();
  const [picked, setPicked] = useState(line.category?.id ?? (line.categorySource === 'hand' ? NONE : ''));
  // The chosen category's name once saved: the dialog then offers a rule instead of closing.
  const [offer, setOffer] = useState<string | null>(null);
  const options = [
    { value: NONE, label: t('banks.categories.uncategorised') },
    { value: AUTO, label: t('banks.categories.auto') },
    ...activeCategoryOptions(categories.data),
  ];
  // The line's own category may be archived by now: not on the list, so not sent back as is.
  const chosen = options.some((o) => o.value === picked);
  const gone = categories.data && line.category && picked === line.category.id && !chosen ? line.category.name : null;
  // Looking at a link's or a rule's category must not pin it as a hand choice; a hand « Sans catégorie » is already pinned.
  const unchanged = line.categorySource === 'hand' ? !line.category && picked === NONE : picked === line.category?.id;
  const problem = categories.error ?? categorise.error ?? resetCategory.error;

  return (
    <FormDialog title={t('banks.categories.title')} onClose={onClose} onDone={onDone}>
      {({ titleId, done }) => offer !== null ? <RuleOffer line={line} category={offer} categoryId={picked} done={done} /> : (
        <form aria-labelledby={titleId} noValidate className="space-y-4" onSubmit={(event) => {
          event.preventDefault();
          event.stopPropagation();
          const path = { params: { path: { id: line.id } } };
          const then = { onSuccess: () => done(t('banks.operations.done_category')) };
          if (picked === AUTO) resetCategory.mutate(path, then);
          else if (chosen) {
            const name = picked !== NONE && line.suggestedPattern ? options.find((o) => o.value === picked)?.label : undefined;
            categorise.mutate({ ...path, body: { categoryId: picked === NONE ? null : picked } },
              name ? { onSuccess: () => setOffer(name) } : then);
          }
        }}>
          <p className="text-sm text-muted-foreground">{line.label} · {formatCurrency(line.amount, line.currency)}</p>
          {problem ? <ProblemMessage problem={present(toProblem(problem))} /> : null}
          <Field label={t('banks.categories.field')} hint={gone ? t('banks.categories.current_gone', { name: gone }) : undefined}>
            {(c) => <ComboboxField {...c} value={picked} onValueChange={setPicked} options={options} placeholder={t('banks.categories.pick')} searchPlaceholder={t('banks.categories.search')} empty={t('banks.categories.none_found')} />}
          </Field>
          <div className="flex justify-end gap-2">
            <Button type="button" variant="outline" onClick={() => done()}>{t('common.cancel')}</Button>
            <Button type="submit" disabled={!chosen || unchanged || categorise.isPending || resetCategory.isPending}>{t('banks.categories.submit')}</Button>
          </div>
        </form>
      )}
    </FormDialog>
  );
}

/** « Toujours classer « CARREFOUR CITY » en Courses ? »: the line's suggested pattern, editable; its account alone when not every adult reads it. */
function RuleOffer({ line, category, categoryId, done }: { line: BankTransaction; category: string; categoryId: string; done: (message?: string) => void }) {
  const { t } = useTranslation();
  const { createRule } = useLineActions();
  const [pattern, setPattern] = useState(line.suggestedPattern ?? '');
  const [only, setOnly] = useState(!line.sharedAccount);
  const questionId = useId();
  const onlyId = useId();
  const question = useRef<HTMLParagraphElement>(null);
  // Filled after mount, so the live region is already there and announces it.
  const [saved, setSaved] = useState(false);
  // The form that had the focus is gone: the question takes it, so it is read first.
  useEffect(() => { question.current?.focus(); setSaved(true); }, []);
  const field = fieldErrorsOf(createRule.error).find((f) => f.path === 'pattern');
  const refused = createRule.error && !field
    ? toProblem(createRule.error).status === 403
      ? { ...present(toProblem(createRule.error)), detail: t('banks.categories.offer_forbidden') }
      : presentBankError(t, createRule.error)
    : null;

  return (
    <form aria-labelledby={questionId} noValidate className="space-y-4" onSubmit={(event) => {
      event.preventDefault();
      event.stopPropagation();
      createRule.mutate({ body: { pattern: pattern.trim(), categoryId, accountId: only ? line.accountId : null } },
        { onSuccess: () => done(t('banks.categories.offer_done')) });
    }}>
      <p role="status" className="text-sm text-muted-foreground">{saved ? t('banks.categories.offer_saved') : ''}</p>
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
      <div className="flex justify-end gap-2">
        <Button type="button" variant="outline" onClick={() => done(t('banks.operations.done_category'))}>{t('banks.categories.offer_decline')}</Button>
        <Button type="submit" disabled={!pattern.trim() || createRule.isPending}>{t('banks.categories.offer_create')}</Button>
      </div>
    </form>
  );
}
