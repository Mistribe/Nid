import { useTranslation } from 'react-i18next';
import { LocalDate } from '@/components/LocalDate';
import { Money } from '@/components/Money';
import { Section } from '@/components/page/Section';
import { SortableTable } from '@/components/page/SortableTable';
import { amountKey } from '@/components/page/sortKey';
import { Button } from '@/components/ui/button';
import { useRowDialog } from '../../properties/useRowDialog';
import { type GoalEntry } from '../api';
import { EntryDialog } from './EntryDialog';
import { EntryRowActions } from './EntryRowActions';
import '@/locales/goals';

/** A free goal's readings, newest first; adding and deleting only while the goal is open. */
export function EntriesTab({ goalId, entries, writable }: { goalId: string; entries: GoalEntry[]; writable: boolean }) {
  const { t } = useTranslation();
  const rows = [...entries].sort((a, b) => (a.on < b.on ? 1 : a.on > b.on ? -1 : 0)).map((e) => ({ ...e, id: e.on }));
  const row = useRowDialog(rows, 'entry-menu');
  const actions = (e: GoalEntry) => (
    <EntryRowActions goalId={goalId} entry={e} onDeleted={row.announce}
      onReloaded={() => row.headingRef.current?.focus()} />
  );
  return (
    <Section title={t('goals_detail.tab_entries')} headingRef={row.headingRef} srTitle>
      {writable ? <Button ref={row.addButtonRef} type="button" variant="outline" onClick={() => row.open()}>{t('goals_detail.entries.add')}</Button> : null}
      {writable && row.dialog ? <EntryDialog goalId={goalId} onClose={row.cancel} onDone={row.saved} /> : null}
      {rows.length === 0 ? <p className="text-muted-foreground">{t('goals_detail.entries.empty')}</p> : (
        <SortableTable<GoalEntry> caption={t('goals_detail.tab_entries')} rows={rows} rowKey={(e) => e.on}
          columns={[
            { id: 'on', header: t('goals_detail.entries.date'), sort: (e) => e.on, cell: (e) => <LocalDate value={e.on} /> },
            { id: 'amount', header: t('goals_detail.entries.amount'), align: 'end', sort: (e) => amountKey(e.amount.amount), cell: (e) => <Money value={e.amount} tabular /> },
            ...(writable ? [{ id: 'menu', header: t('goals_list.col_actions'), align: 'end' as const, cell: actions }] : []),
          ]}
          mobile={(e) => (
            <div className="flex items-center justify-between gap-2"><span><LocalDate value={e.on} /> · <Money value={e.amount} tabular /></span>{writable ? actions(e) : null}</div>
          )} />
      )}
    </Section>
  );
}
