import { useQueryClient } from '@tanstack/react-query';
import type { TFunction } from 'i18next';
import { useEffect, useId, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem, type Presentation } from '@/api/errors';
import { ConfirmAction } from '@/components/ConfirmAction';
import { FormDialog } from '@/components/FormDialog';
import { InfoTip } from '@/components/InfoTip';
import { ProblemMessage } from '@/components/ProblemMessage';
import { ComboboxField } from '@/components/form/ComboboxField';
import { Field } from '@/components/form/Field';
import { fieldErrorsOf } from '@/components/form/fieldErrors';
import { Folded } from '@/components/form/Folded';
import type { Option } from '@/components/form/options';
import { Segmented } from '@/components/form/Segmented';
import { RowMenu } from '@/components/page/RowMenu';
import { Section } from '@/components/page/Section';
import { SortableTable } from '@/components/page/SortableTable';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Skeleton } from '@/components/ui/skeleton';
import { bankErrorText, CATEGORIES, CATEGORY, CATEGORY_MERGE, TRANSACTIONS, useCategories, type BankCategory } from '@/api/banking';
import '@/locales/banks';

const KINDS = ['expense', 'income', 'transfer'] as const;
type Kind = (typeof KINDS)[number];
// Not a category id (those are uuids): « no parent », a top-level category.
const NONE = 'none';

const kindLabel = (t: TFunction, kind: string) => t(`banks.category_list.kind.${kind as Kind}`);

/** A refusal as people read it: a banking code in French, anything else as `present` words it. */
function presentError(t: TFunction, error: unknown): Presentation {
  const problem = toProblem(error);
  const shown = present(problem);
  return problem.code?.startsWith('banking.') ? { ...shown, detail: bankErrorText(t, problem.code, shown.detail ?? '') } : shown;
}

/** The writes on the list; a new name or a merge shows on the bank lines too. */
function useCategoryWrites() {
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const onSuccess = () => Promise.all([CATEGORIES, TRANSACTIONS].map((path) => queryClient.invalidateQueries({ queryKey: ['get', path] })));
  return {
    create: $api.useMutation('post', CATEGORIES, { onSuccess }),
    update: $api.useMutation('patch', CATEGORY, { onSuccess }),
    merge: $api.useMutation('post', CATEGORY_MERGE, { onSuccess }),
  };
}

type Dialog = { mode: 'rename' | 'merge'; category: BankCategory } | { mode: 'create' };

/**
 * Réglages › Banques › Catégories: the household's categories by kind, each parent followed by its children; archived ones
 * folded under « Archivées »; merged ones are history and not listed.
 */
export default function CategoriesTab() {
  const { t } = useTranslation();
  const categories = useCategories();
  const { update } = useCategoryWrites();
  const [dialog, setDialog] = useState<Dialog | null>(null);
  // After a write or « Annuler »: the row's menu when it is still there, else its kind's heading.
  const [focus, setFocus] = useState<{ id?: string; kind: string } | null>(null);
  const expense = useRef<HTMLHeadingElement>(null);
  const income = useRef<HTMLHeadingElement>(null);
  const transfer = useRef<HTMLHeadingElement>(null);
  const addButton = useRef<HTMLButtonElement>(null);
  useEffect(() => {
    if (!focus) return;
    const menu = [`category-${focus.id}`, `category-${focus.id}-mobile`].map((id) => document.getElementById(id))
      .find((el) => el && el.checkVisibility?.() !== false);
    (menu ?? { expense, income, transfer }[focus.kind as Kind]?.current)?.focus();
    setFocus(null);
  }, [focus]);

  const kept = (categories.data ?? []).filter((c) => !c.mergedIntoId);
  const active = kept.filter((c) => !c.archived);
  // A child whose parent is archived stands at the top, named with its parent.
  const tops = active.filter((c) => !c.parentId || !active.some((p) => p.id === c.parentId));
  const fullName = (c: BankCategory) => { const p = kept.find((x) => x.id === c.parentId); return p ? `${p.name} › ${c.name}` : c.name; };
  const rowsOf = (kind: Kind) => tops.filter((c) => c.kind === kind).flatMap((p) => [p, ...active.filter((c) => c.parentId === p.id)]);
  // Same kind, in use, neither itself nor its own children; one with children merges only into a top-level one (`banking.category_depth`).
  const targetsOf = (c: BankCategory): Option[] => {
    const parent = kept.some((x) => x.parentId === c.id);
    return active.filter((x) => x.kind === c.kind && x.id !== c.id && x.parentId !== c.id && (!parent || !x.parentId))
      .map((x) => ({ value: x.id, label: fullName(x) }));
  };

  const patch = (c: BankCategory, body: { archived?: boolean; resetName?: boolean }, message: string) => update.mutate(
    { params: { path: { id: c.id } }, body },
    {
      onSuccess: () => { toast.success(message); setFocus({ id: c.id, kind: c.kind }); },
      onError: (error) => { toast.error(bankErrorText(t, toProblem(error).code ?? '', t('banks.category_list.failed'))); },
    },
  );
  const items = (c: BankCategory) => (c.archived
    ? [{ label: t('banks.category_list.unarchive'), onSelect: () => patch(c, { archived: false }, t('banks.category_list.done_unarchive')) }]
    : [
      { label: t('banks.category_list.rename'), onSelect: () => setDialog({ mode: 'rename', category: c }) },
      ...(c.key && c.defaultName && c.defaultName !== c.name
        ? [{ label: t('banks.category_list.reset_name'), onSelect: () => patch(c, { resetName: true }, t('banks.category_list.done_reset')) }] : []),
      ...(targetsOf(c).length > 0 ? [{ label: t('banks.category_list.merge'), onSelect: () => setDialog({ mode: 'merge', category: c }) }] : []),
      { label: t('banks.category_list.archive'), onSelect: () => patch(c, { archived: true }, t('banks.category_list.done_archive')) },
    ]);

  const table = (caption: string, rows: BankCategory[]) => {
    const name = (c: BankCategory) => (tops.includes(c) || c.archived
      ? <span className="whitespace-normal font-medium">{fullName(c)}</span>
      : <span className="block whitespace-normal pl-6">{c.name}</span>);
    const menu = (c: BankCategory, suffix = '') => (
      <RowMenu id={`category-${c.id}${suffix}`} label={t('banks.category_list.actions_of', { name: fullName(c) })} items={items(c)} />
    );
    return (
      <SortableTable<BankCategory> caption={caption} rows={rows} rowKey={(c) => c.id}
        columns={[
          { id: 'name', header: t('banks.category_list.name'), cell: name },
          { id: 'actions', header: t('banks.actions'), align: 'end', cell: (c) => menu(c) },
        ]}
        mobile={(c) => <div className="flex items-center justify-between gap-2">{name(c)}{menu(c, '-mobile')}</div>} />
    );
  };

  const close = () => { if (dialog && 'category' in dialog) setFocus({ id: dialog.category.id, kind: dialog.category.kind }); setDialog(null); };
  const done = (message: string) => {
    toast.success(message);
    if (dialog && 'category' in dialog) setFocus({ id: dialog.category.id, kind: dialog.category.kind }); else addButton.current?.focus();
    setDialog(null);
  };
  const archived = kept.filter((c) => c.archived);

  return (
    <>
      <div><Button ref={addButton} type="button" onClick={() => setDialog({ mode: 'create' })}>{t('banks.category_list.create')}</Button></div>
      {categories.error ? <ProblemMessage problem={present(toProblem(categories.error))} />
        : !categories.data ? <Skeleton aria-busy="true" className="h-32"><span className="sr-only">{t('banks.category_list.loading')}</span></Skeleton>
        : (
          <>
            {KINDS.map((kind) => (
              <Section key={kind} title={kindLabel(t, kind)} headingRef={{ expense, income, transfer }[kind]}
                tip={kind === 'transfer' ? <InfoTip label={kindLabel(t, kind)}>{t('banks.category_list.transfer_about')}</InfoTip> : undefined}>
                {rowsOf(kind).length > 0 ? table(kindLabel(t, kind), rowsOf(kind)) : <p className="text-sm text-muted-foreground">{t('banks.category_list.empty')}</p>}
              </Section>
            ))}
            {archived.length > 0 ? (
              <Folded label={t('banks.category_list.archived')} hasError={false}>{table(t('banks.category_list.archived'), archived)}</Folded>
            ) : null}
          </>
        )}
      {dialog?.mode === 'create' ? <CreateDialog parents={tops.filter((c) => !c.parentId)} onClose={close} onDone={done} /> : null}
      {dialog?.mode === 'rename' ? <RenameDialog category={dialog.category} onClose={close} onDone={done} /> : null}
      {dialog?.mode === 'merge' ? <MergeDialog category={dialog.category} targets={targetsOf(dialog.category)} onClose={close} onDone={done} /> : null}
    </>
  );
}

/** The `name` field's refusal under the field, any other above the form. */
function useNameError(error: unknown) {
  const { t } = useTranslation();
  const field = fieldErrorsOf(error).find((f) => f.path === 'name');
  return {
    name: field ? bankErrorText(t, field.code, field.message) : undefined,
    problem: error && !field ? <ProblemMessage problem={presentError(t, error)} /> : null,
  };
}

function Buttons({ cancel, submit, pending }: { cancel: () => void; submit: string; pending: boolean }) {
  const { t } = useTranslation();
  return (
    <div className="flex justify-end gap-2">
      <Button type="button" variant="outline" onClick={cancel}>{t('common.cancel')}</Button>
      <Button type="submit" disabled={pending}>{submit}</Button>
    </div>
  );
}

function RenameDialog({ category, onClose, onDone }: { category: BankCategory; onClose: () => void; onDone: (message: string) => void }) {
  const { t } = useTranslation();
  const { update } = useCategoryWrites();
  const [name, setName] = useState(category.name);
  const errors = useNameError(update.error);
  return (
    <FormDialog title={t('banks.category_list.rename_title', { name: category.name })} onClose={onClose} onDone={onDone}>
      {({ titleId, done }) => (
        <form aria-labelledby={titleId} noValidate className="space-y-4" onSubmit={(event) => {
          event.preventDefault();
          event.stopPropagation();
          update.mutate({ params: { path: { id: category.id } }, body: { name: name.trim() } }, { onSuccess: () => done(t('banks.category_list.done_rename')) });
        }}>
          {errors.problem}
          <Field label={t('banks.category_list.name')} error={errors.name}>
            {(c) => <Input {...c} value={name} maxLength={60} onChange={(event) => setName(event.target.value)} />}
          </Field>
          <Buttons cancel={() => done()} submit={t('banks.category_list.save')} pending={update.isPending} />
        </form>
      )}
    </FormDialog>
  );
}

function MergeDialog({ category, targets, onClose, onDone }: {
  category: BankCategory; targets: Option[]; onClose: () => void; onDone: (message: string) => void;
}) {
  const { t } = useTranslation();
  const { merge } = useCategoryWrites();
  const [into, setInto] = useState('');
  const target = targets.find((o) => o.value === into);
  return (
    <FormDialog title={t('banks.category_list.merge_title', { name: category.name })} onClose={onClose} onDone={onDone}>
      {({ done }) => (
        <div className="space-y-4">
          {merge.error ? <ProblemMessage problem={presentError(t, merge.error)} /> : null}
          <Field label={t('banks.category_list.merge_into')}>
            {(c) => <ComboboxField {...c} value={into} onValueChange={setInto} options={targets} placeholder={t('banks.categories.pick')}
              searchPlaceholder={t('banks.categories.search')} empty={t('banks.categories.none_found')} />}
          </Field>
          <div className="flex justify-end gap-2">
            <Button type="button" variant="outline" onClick={() => done()}>{t('common.cancel')}</Button>
            <ConfirmAction label={t('banks.category_list.merge_submit')} disabled={!target} pending={merge.isPending}
              confirmation={t('banks.category_list.merge_confirm', { name: category.name, into: target?.label ?? '' })}
              onConfirm={() => merge.mutate({ params: { path: { id: category.id } }, body: { into } }, { onSuccess: () => done(t('banks.category_list.done_merge')) })} />
          </div>
        </div>
      )}
    </FormDialog>
  );
}

function CreateDialog({ parents, onClose, onDone }: { parents: BankCategory[]; onClose: () => void; onDone: (message: string) => void }) {
  const { t } = useTranslation();
  const { create } = useCategoryWrites();
  const [name, setName] = useState('');
  const [parent, setParent] = useState(NONE);
  const [kind, setKind] = useState<string>('expense');
  const kindId = useId();
  const errors = useNameError(create.error);
  const options: Option[] = [
    { value: NONE, label: t('banks.category_list.parent_none') },
    ...parents.map((p) => ({ value: p.id, label: p.name, group: kindLabel(t, p.kind) })),
  ];
  return (
    <FormDialog title={t('banks.category_list.create')} onClose={onClose} onDone={onDone}>
      {({ titleId, done }) => (
        <form aria-labelledby={titleId} noValidate className="space-y-4" onSubmit={(event) => {
          event.preventDefault();
          event.stopPropagation();
          create.mutate({ body: { name: name.trim(), ...(parent === NONE ? { kind } : { parentId: parent }) } },
            { onSuccess: (made) => done(t('banks.category_list.done_create', { name: made.name })) });
        }}>
          {errors.problem}
          <Field label={t('banks.category_list.name')} error={errors.name}>
            {(c) => <Input {...c} value={name} maxLength={60} onChange={(event) => setName(event.target.value)} />}
          </Field>
          <Field label={t('banks.category_list.parent')}>
            {(c) => <ComboboxField {...c} value={parent} onValueChange={setParent} options={options}
              searchPlaceholder={t('banks.categories.search')} empty={t('banks.categories.none_found')} />}
          </Field>
          {parent === NONE ? (
            <div className="space-y-1">
              <p id={kindId} className="text-sm">{t('banks.category_list.kind_label')}</p>
              <Segmented aria-labelledby={kindId} value={kind} onValueChange={setKind} options={KINDS.map((k) => ({ value: k, label: kindLabel(t, k) }))} />
            </div>
          ) : null}
          <Buttons cancel={() => done()} submit={t('banks.category_list.create_submit')} pending={create.isPending} />
        </form>
      )}
    </FormDialog>
  );
}
