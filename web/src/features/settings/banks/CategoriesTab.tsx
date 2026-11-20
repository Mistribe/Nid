import { Link, useNavigate, useSearch } from '@tanstack/react-router';
import type { TFunction } from 'i18next';
import { useEffect, useId, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { present, toProblem } from '@/api/errors';
import type { BanksSearch } from '@/app/router';
import { InfoTip } from '@/components/InfoTip';
import { LocalDate } from '@/components/LocalDate';
import { ProblemMessage } from '@/components/ProblemMessage';
import { ComboboxField } from '@/components/form/ComboboxField';
import { Field } from '@/components/form/Field';
import { Folded } from '@/components/form/Folded';
import type { Option } from '@/components/form/options';
import { Segmented } from '@/components/form/Segmented';
import { FormDialog } from '@/components/FormDialog';
import { RowMenu } from '@/components/page/RowMenu';
import { Section } from '@/components/page/Section';
import { SortableTable } from '@/components/page/SortableTable';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Skeleton } from '@/components/ui/skeleton';
import { bankErrorText, useCategories, type BankCategory } from '@/api/banking';
import { shiftMonth } from '@/features/calendar/calendarDates';
import { CategoryLinesDialog } from '@/features/spending/CategoryLinesDialog';
import { todayIso } from '@/lib/today';
import '@/locales/banks';
import { Buttons, MergeDialog, RenameDialog, useCategoryWrites, useNameError } from '@/features/spending/categoryEdits';
import { fullName as nameIn, mergeTargets } from '@/features/spending/categoryTargets';
import { CategoryRules } from './CategoryRules';

const KINDS = ['expense', 'income', 'transfer'] as const;
type Kind = (typeof KINDS)[number];
// Not a category id (those are uuids): « no parent », a top-level category.
const NONE = 'none';

const kindLabel = (t: TFunction, kind: string) => t(`banks.category_list.kind.${kind as Kind}`);

// The server's order is kept among equals (top-level first, then by name); the most used come first.
const byUse = (list: BankCategory[]) => [...list].sort((a, b) => b.lines - a.lines);

type Dialog = { mode: 'rename' | 'merge'; category: BankCategory } | { mode: 'create' };

/**
 * Réglages › Banques › Catégories (#415): what categories are for, then one kind at a time (`?kind=`, Dépenses first), its top-level
 * categories by use over 12 months (the server's counts), each unfolding its children; the unused and the archived folded; merged ones
 * are history and not listed. Then the rules that pick them.
 */
export default function CategoriesTab() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const search: BanksSearch = useSearch({ strict: false });
  const kind: Kind = search.kind ?? 'expense';
  const categories = useCategories();
  const { update } = useCategoryWrites();
  const [dialog, setDialog] = useState<Dialog | null>(null);
  const [lines, setLines] = useState<BankCategory | null>(null);
  // After a write or « Annuler »: the row's menu when it is still there, else the list's heading.
  const [focus, setFocus] = useState<{ id?: string } | null>(null);
  const heading = useRef<HTMLHeadingElement>(null);
  const addButton = useRef<HTMLButtonElement>(null);
  useEffect(() => {
    if (!focus) return;
    const menu = [`category-${focus.id}`, `category-${focus.id}-mobile`].map((id) => document.getElementById(id))
      .find((el) => el && el.checkVisibility?.() !== false);
    (menu ?? heading.current)?.focus();
    setFocus(null);
  }, [focus]);

  const all = categories.data ?? [];
  const kept = all.filter((c) => !c.mergedIntoId);
  const active = kept.filter((c) => !c.archived);
  // A child whose parent is archived stands at the top, named with its parent.
  const tops = active.filter((c) => !c.parentId || !active.some((p) => p.id === c.parentId));
  const fullName = (c: BankCategory) => nameIn(kept, c);
  const childrenOf = (c: BankCategory) => (tops.includes(c) ? byUse(active.filter((x) => x.parentId === c.id)) : undefined);
  const shown = byUse(tops.filter((c) => c.kind === kind));
  const used = shown.filter((c) => c.lines > 0);
  const unused = shown.filter((c) => c.lines === 0);

  const patch = (c: BankCategory, body: { archived?: boolean; resetName?: boolean }, message: string) => update.mutate(
    { params: { path: { id: c.id } }, body },
    {
      onSuccess: () => { toast.success(message); setFocus({ id: c.id }); },
      onError: (error) => { toast.error(bankErrorText(t, toProblem(error).code ?? '', t('banks.category_list.failed'))); },
    },
  );
  const items = (c: BankCategory) => (c.archived
    ? [{ label: t('banks.category_list.unarchive'), onSelect: () => patch(c, { archived: false }, t('banks.category_list.done_unarchive')) }]
    : [
      { label: t('banks.category_list.rename'), onSelect: () => setDialog({ mode: 'rename', category: c }) },
      ...(c.key && c.defaultName && c.defaultName !== c.name
        ? [{ label: t('banks.category_list.reset_name'), onSelect: () => patch(c, { resetName: true }, t('banks.category_list.done_reset')) }] : []),
      ...(mergeTargets(all, c).length > 0 ? [{ label: t('banks.category_list.merge'), onSelect: () => setDialog({ mode: 'merge', category: c }) }] : []),
      { label: t('banks.category_list.archive'), onSelect: () => patch(c, { archived: true }, t('banks.category_list.done_archive')) },
    ]);

  const table = (caption: string, rows: BankCategory[], tree = true) => {
    const label = (c: BankCategory) => (tops.includes(c) || c.archived ? fullName(c) : c.name);
    // Only an expense's lines have a list to open (the spending one); the others name themselves.
    const name = (c: BankCategory) => {
      const kids = tree ? childrenOf(c)?.length ?? 0 : 0;
      return (
        <span className="whitespace-normal">
          {c.kind === 'expense' && !c.archived && c.lines > 0 ? (
            <button type="button" aria-label={t('banks.category_list.open_lines', { name: fullName(c) })} onClick={() => setLines(c)}
              className="rounded-sm text-left font-medium underline-offset-4 hover:underline focus-visible:ring-[3px] focus-visible:ring-ring/50 focus-visible:outline-none">
              {label(c)}
            </button>
          ) : <span className="font-medium">{label(c)}</span>}
          {kids > 0 ? <span className="text-sm text-muted-foreground"> · {t('banks.category_list.children', { count: kids })}</span> : null}
        </span>
      );
    };
    const usage = (c: BankCategory) => t('banks.category_list.lines_count', { count: c.lines });
    const last = (c: BankCategory) => (c.lastLineOn ? <LocalDate value={c.lastLineOn} style="short" /> : '—');
    const menu = (c: BankCategory, suffix = '') => (
      <RowMenu id={`category-${c.id}${suffix}`} label={t('banks.category_list.actions_of', { name: fullName(c) })} items={items(c)} />
    );
    const common = {
      caption, rows, rowKey: (c: BankCategory) => c.id,
      columns: [
        { id: 'name', header: t('banks.category_list.name'), cell: name },
        { id: 'lines', header: t('banks.category_list.lines'), align: 'end' as const, cell: usage },
        { id: 'last', header: t('banks.category_list.last'), align: 'end' as const, cell: last },
        { id: 'actions', header: t('banks.actions'), align: 'end' as const, cell: (c: BankCategory) => menu(c) },
      ],
      mobile: (c: BankCategory) => (
        <div className="flex items-start justify-between gap-2">
          <div className="min-w-0 space-y-0.5">
            {name(c)}
            <p className="text-sm text-muted-foreground">{usage(c)}{c.lastLineOn ? <> · <LocalDate value={c.lastLineOn} style="short" /></> : null}</p>
          </div>
          {menu(c, '-mobile')}
        </div>
      ),
    };
    return tree
      ? <SortableTable<BankCategory> {...common} subRows={childrenOf} expandSingle expandLabel={(c) => t('banks.category_list.expand', { name: c.name })} />
      : <SortableTable<BankCategory> {...common} />;
  };

  const close = () => { if (dialog && 'category' in dialog) setFocus({ id: dialog.category.id }); setDialog(null); };
  const done = (message: string) => {
    toast.success(message);
    if (dialog && 'category' in dialog) setFocus({ id: dialog.category.id }); else addButton.current?.focus();
    setDialog(null);
  };
  const archived = kept.filter((c) => c.archived && c.kind === kind);
  const current = todayIso().slice(0, 7);

  return (
    <>
      <p className="max-w-prose text-sm text-muted-foreground">
        {t('banks.category_list.about')}{' '}
        <Link to="/spending" className="font-medium text-foreground underline underline-offset-4">{t('banks.category_list.about_link')}</Link>
      </p>
      <div className="flex flex-wrap items-center justify-between gap-2">
        <Segmented aria-label={t('banks.category_list.kind_label')} value={kind}
          onValueChange={(v) => void navigate({ to: '/settings/banks', replace: true, resetScroll: false,
            search: { ...search, kind: v === 'expense' ? undefined : v as Exclude<Kind, 'expense'> } })}
          options={KINDS.map((k) => ({ value: k, label: kindLabel(t, k) }))} />
        <Button ref={addButton} type="button" onClick={() => setDialog({ mode: 'create' })}>{t('banks.category_list.create')}</Button>
      </div>
      {categories.error ? <ProblemMessage problem={present(toProblem(categories.error))} />
        : !categories.data ? <Skeleton aria-busy="true" className="h-32"><span className="sr-only">{t('banks.category_list.loading')}</span></Skeleton>
        : (
          <Section title={kindLabel(t, kind)} headingRef={heading}
            tip={kind === 'transfer' ? <InfoTip label={kindLabel(t, kind)}>{t('banks.category_list.transfer_about')}</InfoTip> : undefined}>
            {used.length > 0 ? table(kindLabel(t, kind), used)
              : <p className="text-sm text-muted-foreground">{t(shown.length > 0 ? 'banks.category_list.none_used' : 'banks.category_list.empty')}</p>}
            {unused.length > 0 ? (
              <Folded label={t('banks.category_list.unused', { count: unused.length })} hasError={false}>
                {table(t('banks.category_list.unused', { count: unused.length }), unused)}
              </Folded>
            ) : null}
            {archived.length > 0 ? (
              <Folded label={t('banks.category_list.archived')} hasError={false}>{table(t('banks.category_list.archived'), archived, false)}</Folded>
            ) : null}
          </Section>
        )}
      <CategoryRules />
      {dialog?.mode === 'create' ? <CreateDialog parents={tops.filter((c) => !c.parentId)} kind={kind} onClose={close} onDone={done} /> : null}
      {dialog?.mode === 'rename' ? <RenameDialog category={dialog.category} onClose={close} onDone={done} /> : null}
      {dialog?.mode === 'merge' ? <MergeDialog category={dialog.category} targets={mergeTargets(all, dialog.category)} onClose={close} onDone={done} /> : null}
      {lines ? (
        <CategoryLinesDialog target={{ categoryId: lines.id, name: fullName(lines) }} query={{ scope: 'me', from: shiftMonth(current, -11), to: current }}
          period={t('banks.category_list.lines_period')} onClose={() => { setFocus({ id: lines.id }); setLines(null); }} />
      ) : null}
    </>
  );
}

function CreateDialog({ parents, kind: initial, onClose, onDone }: { parents: BankCategory[]; kind: Kind; onClose: () => void; onDone: (message: string) => void }) {
  const { t } = useTranslation();
  const { create } = useCategoryWrites();
  const [name, setName] = useState('');
  const [parent, setParent] = useState(NONE);
  const [kind, setKind] = useState<string>(initial);
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
