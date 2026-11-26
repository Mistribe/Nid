import { useQueryClient } from '@tanstack/react-query';
import type { TFunction } from 'i18next';
import { useEffect, useId, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { FormDialog } from '@/components/FormDialog';
import { InfoTip } from '@/components/InfoTip';
import { LocalDate } from '@/components/LocalDate';
import { formatCurrency } from '@/components/Money';
import { ProblemMessage } from '@/components/ProblemMessage';
import { ComboboxField } from '@/components/form/ComboboxField';
import { Field } from '@/components/form/Field';
import { fieldErrorsOf } from '@/components/form/fieldErrors';
import { Folded } from '@/components/form/Folded';
import { Segmented } from '@/components/form/Segmented';
import { SelectField } from '@/components/form/SelectField';
import { RowActions } from '@/components/page/RowActions';
import { RowMenu } from '@/components/page/RowMenu';
import { Section } from '@/components/page/Section';
import { SortableTable } from '@/components/page/SortableTable';
import { amountKey } from '@/components/page/sortKey';
import { Button } from '@/components/ui/button';
import { Checkbox } from '@/components/ui/checkbox';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { formatInstantDay } from '@/lib/format';
import { toFieldAmount, toWireAmount } from '@/lib/amount';
import {
  accountLabel, activeCategoryOptions, bankErrorText, CATEGORY_RULE, CATEGORY_RULE_APPLY, CATEGORY_RULE_DRAFT_PREVIEW, CATEGORY_RULE_PREVIEW, CATEGORY_RULES,
  presentBankError, useBankAccounts, useCategories, type BankTransaction, type CategoryRule,
} from '@/api/banking';
import { DraftPreview } from '../investments/operations/CategoryDialog';
import '@/locales/banks';

const ALL = 'all';

/** Every banking list: a rule's write changes the rules, and an applied one the lines and their counts. */
const invalidateBanking = (queryClient: ReturnType<typeof useQueryClient>) =>
  queryClient.invalidateQueries({ predicate: (q) => typeof q.queryKey[1] === 'string' && q.queryKey[1].startsWith('/api/v1/banking/') });

function useRuleWrites() {
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const onSuccess = () => { void invalidateBanking(queryClient); };
  return {
    create: $api.useMutation('post', CATEGORY_RULES, { onSuccess }),
    update: $api.useMutation('patch', CATEGORY_RULE, { onSuccess }),
    remove: $api.useMutation('delete', CATEGORY_RULE, { onSuccess }),
    apply: $api.useMutation('post', CATEGORY_RULE_APPLY, { onSuccess }),
  };
}

/** « Débits · dès 10,00 · jusqu'à 50,00 »: what narrows a rule besides its pattern and account; the server's amounts, as text. */
function conditions(t: TFunction, r: CategoryRule) {
  return [
    r.direction ? t(`banks.category_rules.direction_${r.direction as 'debit' | 'credit'}`) : null,
    r.minAmount ? t('banks.category_rules.conditions_min', { amount: toFieldAmount(r.minAmount) }) : null,
    r.maxAmount ? t('banks.category_rules.conditions_max', { amount: toFieldAmount(r.maxAmount) }) : null,
  ].filter(Boolean).join(' · ');
}

type Dialog = { mode: 'create' } | { mode: 'edit' | 'apply'; rule: CategoryRule };

/** Dépenses › Catégories › « Règles de catégorie »: the rules the actor sees, each with what it did on the lines they read. */
export function CategoryRules() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const rules = $api.useQuery('get', CATEGORY_RULES);
  const { remove } = useRuleWrites();
  const [dialog, setDialog] = useState<Dialog | null>(null);
  const heading = useRef<HTMLHeadingElement>(null);
  // After a dialog: the row's menu when it is still there, else the section's heading.
  const [focus, setFocus] = useState<string | null>(null);
  useEffect(() => {
    if (focus === null) return;
    const menu = [`rule-${focus}`, `rule-${focus}-mobile`].map((id) => document.getElementById(id)).find((el) => el && el.checkVisibility?.() !== false);
    (menu ?? heading.current)?.focus();
    setFocus(null);
  }, [focus]);

  const account = (r: CategoryRule) => (r.accountId ? r.accountName ?? '—' : t('banks.category_rules.all_accounts'));
  const category = (r: CategoryRule) => r.categoryArchived
    ? <span>{r.categoryName} <span className="text-muted-foreground">{t('banks.category_rules.archived')}</span> <InfoTip label={r.categoryName}>{t('banks.category_rules.archived_about')}</InfoTip></span>
    : r.categoryName;
  const lastUsed = (r: CategoryRule) => (r.lastUsedAt ? formatInstantDay(r.lastUsedAt) : t('banks.category_rules.never'));
  const pattern = (r: CategoryRule) => {
    const more = conditions(t, r);
    return <span className="whitespace-normal"><span className="font-medium">{r.pattern}</span>{more ? <span className="block text-sm text-muted-foreground">{more}</span> : null}</span>;
  };
  const actions = (r: CategoryRule, menuId: string) => {
    const label = t('banks.category_rules.actions_of', { pattern: r.pattern });
    const apply = { label: t('banks.category_rules.apply'), onSelect: () => setDialog({ mode: 'apply', rule: r }) };
    return r.canEdit ? (
      <RowActions menuId={menuId} menuLabel={label}
        items={[{ label: t('banks.category_rules.edit'), onSelect: () => setDialog({ mode: 'edit', rule: r }) }, apply]}
        confirmation={t('banks.category_rules.delete_confirm', { pattern: r.pattern })} remove={remove}
        onConfirm={() => remove.mutate({ params: { path: { id: r.id } } }, { onSuccess: () => { toast.success(t('banks.category_rules.deleted')); setFocus(''); } })}
        present={presentBankError} invalidate={invalidateBanking} />
    ) : <RowMenu id={menuId} label={label} items={[apply]} />;
  };
  const close = () => { setFocus(dialog && 'rule' in dialog ? dialog.rule.id : ''); setDialog(null); };
  const done = (message: string) => { toast.success(message); close(); };

  return (
    <Section title={t('banks.category_rules.title')} headingRef={heading}
      actions={<Button type="button" onClick={() => setDialog({ mode: 'create' })}>{t('banks.category_rules.create')}</Button>}>
      <p className="text-sm text-muted-foreground">{t('banks.category_rules.hint')}</p>
      {rules.error ? <ProblemMessage problem={present(toProblem(rules.error))} /> : null}
      {rules.data?.length === 0 ? <p>{t('banks.category_rules.empty')}</p> : null}
      {rules.data && rules.data.length > 0 ? (
        <SortableTable<CategoryRule> caption={t('banks.category_rules.title')} rows={rules.data} rowKey={(r) => r.id} initialSort={{ id: 'pattern', desc: false }}
          columns={[
            { id: 'pattern', header: t('banks.category_rules.pattern'), sort: (r) => r.pattern, cell: pattern },
            { id: 'account', header: t('banks.category_rules.account'), sort: account, cell: account },
            { id: 'category', header: t('banks.category_rules.category'), sort: (r) => r.categoryName, cell: category },
            { id: 'lines', header: t('banks.category_rules.lines'), align: 'end', sort: (r) => r.lines, cell: (r) => <span className="tabular-nums">{r.lines}</span> },
            { id: 'used', header: t('banks.category_rules.last_used'), sort: (r) => r.lastUsedAt ?? '', cell: lastUsed },
            { id: 'actions', header: t('banks.actions'), align: 'end', cell: (r) => actions(r, `rule-${r.id}`) },
          ]}
          mobile={(r) => (
            <div className="space-y-1">
              <div className="flex items-start justify-between gap-2">{pattern(r)}{actions(r, `rule-${r.id}-mobile`)}</div>
              <p className="text-sm text-muted-foreground">{category(r)} · {account(r)} · {t('banks.category_rules.lines_count', { count: r.lines })} · {lastUsed(r)}</p>
            </div>
          )} />
      ) : null}
      {dialog?.mode === 'create' ? <RuleDialog onClose={close} onDone={done} /> : null}
      {dialog?.mode === 'edit' ? <RuleDialog rule={dialog.rule} onClose={close} onDone={done} /> : null}
      {dialog?.mode === 'apply' ? <RulePreviewDialog rule={dialog.rule} onClose={close} onDone={done} /> : null}
    </Section>
  );
}

/**
 * A new rule, or one changed as a whole (the server replaces every field): pattern and category first, the rest under « Personnaliser ».
 * #721: a new one says how many past lines it would classify (never one classified by hand) and classifies them as it is created.
 */
function RuleDialog({ rule, onClose, onDone }: { rule?: CategoryRule; onClose: () => void; onDone: (message: string) => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { create, update, apply } = useRuleWrites();
  const categories = useCategories();
  const accounts = useBankAccounts();
  const [pattern, setPattern] = useState(rule?.pattern ?? '');
  const [category, setCategory] = useState(rule?.categoryId ?? '');
  const [account, setAccount] = useState(rule?.accountId ?? '');
  const [direction, setDirection] = useState(rule?.direction ?? ALL);
  const [min, setMin] = useState(rule?.minAmount ? toFieldAmount(rule.minAmount) : '');
  const [max, setMax] = useState(rule?.maxAmount ? toFieldAmount(rule.maxAmount) : '');
  const custom = account !== '' || direction !== ALL || min !== '' || max !== '';
  const [more, setMore] = useState(custom);
  const directionId = useId();
  const applyId = useId();
  const body = () => ({
    pattern: pattern.trim(), categoryId: category, accountId: account || null, direction: direction === ALL ? null : direction,
    minAmount: min.trim() ? toWireAmount(min) : null, maxAmount: max.trim() ? toWireAmount(max) : null,
  });
  // The draft the preview was asked for, a moment after the last change.
  const [asked, setAsked] = useState(body());
  const key = JSON.stringify(body());
  useEffect(() => { const timer = setTimeout(() => setAsked(JSON.parse(key) as typeof asked), 300); return () => clearTimeout(timer); }, [key]);
  const preview = $api.useQuery('post', CATEGORY_RULE_DRAFT_PREVIEW, { body: asked }, { enabled: !rule && !!asked.pattern && !!asked.categoryId, retry: false });
  const past = preview.data && asked.pattern ? preview.data.total : 0;
  const [applyPast, setApplyPast] = useState(true);

  const write = rule ? update : create;
  const fields = fieldErrorsOf(write.error);
  const fieldError = (...paths: string[]) => {
    const found = fields.find((f) => paths.includes(f.path));
    return found ? bankErrorText(t, found.code, found.message) : undefined;
  };
  const errors = { pattern: fieldError('pattern'), category: fieldError('categoryId'), min: fieldError('minAmount'), max: fieldError('maxAmount'), account: fieldError('accountId') };
  const problem = toProblem(write.error);
  const shown = write.error && !fields.length
    // The server's 403 carries no code: only a changed account is the author-only refusal.
    ? problem.status === 403
      ? { ...present(problem), detail: t(account !== (rule?.accountId ?? '') ? 'banks.category_rules.account_forbidden' : 'banks.category_rules.edit_forbidden') }
      : presentBankError(t, write.error)
    : null;

  const options = activeCategoryOptions(categories.data);
  // The rule's own category may be archived by now: still shown, so a save says why it is refused.
  if (rule && category === rule.categoryId && !options.some((o) => o.value === category)) options.unshift({ value: rule.categoryId, label: rule.categoryName });
  const categoryName = options.find((o) => o.value === category)?.label ?? '';
  const accountOptions = [
    { value: '', label: t('banks.category_rules.all_accounts') },
    ...(accounts ?? []).filter((a) => a.canWrite || a.id === rule?.accountId).map((a) => ({ value: a.id, label: accountLabel(t, a) })),
  ];
  if (rule?.accountId && !accountOptions.some((o) => o.value === rule.accountId)) accountOptions.push({ value: rule.accountId, label: rule.accountName ?? '—' });

  return (
    <FormDialog title={rule ? t('banks.category_rules.edit_title', { pattern: rule.pattern }) : t('banks.category_rules.create')} onClose={onClose} onDone={onDone}>
      {({ titleId, done }) => (
        <form aria-labelledby={titleId} noValidate className="space-y-4" onSubmit={(event) => {
          event.preventDefault();
          event.stopPropagation();
          if (rule) update.mutate({ params: { path: { id: rule.id } }, body: body() }, { onSuccess: () => done(t('banks.category_rules.done_edit')) });
          else create.mutate({ body: body() }, { onSuccess: (created) => applyPast && past > 0
            // The rule exists either way: a failed apply closes too, so it is not created twice.
            ? apply.mutate({ params: { path: { id: created.id } } }, {
              onSuccess: (r) => done(t('banks.categories.offer_done_apply', { count: r.changed })),
              onError: () => done(t('banks.category_rules.done_create_not_applied')),
            })
            : done(t('banks.category_rules.done_create')) });
        }}>
          {shown ? <ProblemMessage problem={shown} /> : null}
          <Field label={t('banks.category_rules.pattern')} hint={t('banks.category_rules.pattern_hint')} error={errors.pattern}
            tip={<InfoTip label={t('banks.category_rules.pattern')}>{t('banks.category_rules.pattern_about')}</InfoTip>}>
            {(c) => <Input {...c} value={pattern} maxLength={60} onChange={(event) => setPattern(event.target.value)} />}
          </Field>
          <Field label={t('banks.category_rules.category')} error={errors.category}>
            {(c) => <ComboboxField {...c} value={category} onValueChange={setCategory} options={options} placeholder={t('banks.categories.pick')}
              searchPlaceholder={t('banks.categories.search')} empty={t('banks.categories.none_found')} />}
          </Field>
          <Folded label={t('banks.category_rules.more')} open={more} onOpenChange={setMore} hasError={!!(errors.min ?? errors.max ?? errors.account)}>
            <Field label={t('banks.category_rules.account')} error={errors.account}>
              {(c) => <SelectField {...c} value={account} onValueChange={setAccount} options={accountOptions} />}
            </Field>
            <div className="space-y-1">
              <p id={directionId} className="text-sm">{t('banks.category_rules.direction')}</p>
              <Segmented aria-labelledby={directionId} value={direction} onValueChange={setDirection} options={[
                { value: ALL, label: t('banks.category_rules.direction_all') },
                { value: 'debit', label: t('banks.category_rules.direction_debit') },
                { value: 'credit', label: t('banks.category_rules.direction_credit') },
              ]} />
            </div>
            <div className="grid gap-3 sm:grid-cols-2">
              <Field label={t('banks.category_rules.min')} hint={t('banks.category_rules.amount_hint')} error={errors.min}>
                {(c) => <Input inputMode="decimal" {...c} value={min} onChange={(event) => setMin(event.target.value)} />}
              </Field>
              <Field label={t('banks.category_rules.max')} hint={t('banks.category_rules.amount_hint')} error={errors.max}>
                {(c) => <Input inputMode="decimal" {...c} value={max} onChange={(event) => setMax(event.target.value)} />}
              </Field>
            </div>
            {custom ? (
              <Button type="button" variant="ghost" size="sm" className="-ml-3 h-auto whitespace-normal text-left"
                onClick={() => { setAccount(''); setDirection(ALL); setMin(''); setMax(''); }}>
                {t('banks.category_rules.more_reset')}
              </Button>
            ) : null}
          </Folded>
          {!rule && preview.data && asked.pattern ? <DraftPreview preview={preview.data} category={categoryName} /> : null}
          {past > 0 ? (
            <div className="flex items-center gap-2">
              <Checkbox id={applyId} checked={applyPast} onCheckedChange={(checked) => setApplyPast(checked === true)} />
              <Label htmlFor={applyId}>{t('banks.categories.offer_apply', { count: past })}</Label>
            </div>
          ) : null}
          <div className="flex justify-end gap-2">
            <Button type="button" variant="outline" onClick={() => done()}>{t('common.cancel')}</Button>
            <Button type="submit" disabled={!pattern.trim() || !category || write.isPending || apply.isPending}>
              {rule ? t('banks.category_rules.save') : t('banks.category_rules.create_submit')}
            </Button>
          </div>
        </form>
      )}
    </FormDialog>
  );
}

/** « Appliquer aux opérations passées »: how many lines would change, the newest of them, then the change itself. */
function RulePreviewDialog({ rule, onClose, onDone }: { rule: CategoryRule; onClose: () => void; onDone: (message: string) => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const preview = $api.useQuery('get', CATEGORY_RULE_PREVIEW, { params: { path: { id: rule.id }, query: { page: 1 } } });
  const { apply } = useRuleWrites();
  const total = preview.data?.total ?? 0;
  const items = preview.data?.items ?? [];
  const current = (l: BankTransaction) => l.category?.name ?? t('banks.categories.uncategorised');
  const amount = (l: BankTransaction) => <span className="tabular-nums whitespace-nowrap">{formatCurrency(l.amount, l.currency)}</span>;

  return (
    <FormDialog title={t('banks.category_rules.preview_title', { pattern: rule.pattern })} className="sm:max-w-2xl" onClose={onClose} onDone={onDone}>
      {({ done }) => (
        <div className="space-y-4">
          {preview.error ? <ProblemMessage problem={presentBankError(t, preview.error)} /> : null}
          {apply.error ? <ProblemMessage problem={presentBankError(t, apply.error)} /> : null}
          {preview.isPending ? <div role="status" aria-busy="true" aria-label={t('banks.category_rules.preview_loading')} className="h-24 shimmer rounded-md border bg-muted" /> : null}
          {preview.data && total === 0 ? <p>{t('banks.category_rules.preview_none')}</p> : null}
          {total > 0 ? (
            <>
              <p>{t('banks.category_rules.preview_count', { count: total, category: rule.categoryName })}</p>
              <SortableTable<BankTransaction> caption={t('banks.category_rules.preview_title', { pattern: rule.pattern })} rows={items} rowKey={(l) => l.id}
                columns={[
                  { id: 'date', header: t('banks.operations.date'), sort: (l) => l.bookedOn, cell: (l) => <LocalDate value={l.bookedOn} style="short" /> },
                  { id: 'label', header: t('banks.operations.label'), wrap: true, cell: (l) => <span className="whitespace-normal">{l.label}</span> },
                  { id: 'amount', header: t('banks.operations.amount'), align: 'end', sort: (l) => amountKey(l.amount), cell: amount },
                  { id: 'current', header: t('banks.category_rules.preview_current'), sort: current, cell: current },
                ]}
                mobile={(l) => (
                  <div className="space-y-1">
                    <div className="flex items-start justify-between gap-2"><span className="whitespace-normal">{l.label}</span>{amount(l)}</div>
                    <p className="text-sm text-muted-foreground"><LocalDate value={l.bookedOn} style="short" /> · {current(l)}</p>
                  </div>
                )} />
              {total > items.length ? <p className="text-sm text-muted-foreground">{t('banks.category_rules.preview_more', { count: items.length })}</p> : null}
            </>
          ) : null}
          <div className="flex justify-end gap-2">
            <Button type="button" variant="outline" onClick={() => done()}>{t('common.cancel')}</Button>
            {total > 0 ? (
              <Button type="button" disabled={apply.isPending} onClick={() => apply.mutate({ params: { path: { id: rule.id } } },
                { onSuccess: (r) => done(t('banks.category_rules.done_apply', { count: r.changed })) })}>
                {t('banks.category_rules.preview_submit', { count: total })}
              </Button>
            ) : null}
          </div>
        </div>
      )}
    </FormDialog>
  );
}
