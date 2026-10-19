import { useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import { FormDialog } from '@/components/FormDialog';
import { LocalDate } from '@/components/LocalDate';
import { Money } from '@/components/Money';
import { Button } from '@/components/ui/button';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { useRowDialog } from '../../properties/useRowDialog';
import type { IncomeSourceDetail } from '../api';
import { Section } from '../Section';
import { OneOffForm } from './OneOffForm';
import { OneOffRowActions } from './OneOffRowActions';

/**
 * Spec §4: the last twelve months' one-off income, newest first. The section owns its dialog (adding or correcting), its
 * live region and where the focus lands (`useRowDialog`); without write access it is a read-only table.
 */
export function OneOffsSection({ source, writable }: { source: IncomeSourceDetail; writable: boolean }) {
  const { t } = useTranslation();
  const rows = [...source.oneOffs].sort((a, b) => (a.occurredOn < b.occurredOn ? 1 : a.occurredOn > b.occurredOn ? -1 : 0));
  const row = useRowDialog(rows, 'one-off-menu');
  const { dialog, cancel } = row;
  // A source that turns ended (or read-only) drops its open dialog, so it does not come back on reopening.
  useEffect(() => { if (!writable && dialog) cancel(); }, [writable, dialog, cancel]);
  const cols = writable ? 4 : 3;

  return (
    <Section title={t('income_detail.one_offs.title')} headingRef={row.headingRef} announcement={row.announcement}>
      {writable ? <Button ref={row.addButtonRef} type="button" variant="outline" onClick={() => row.open()}>{t('income_detail.one_offs.add')}</Button> : null}
      {writable && dialog ? (
        <FormDialog title={dialog.editing ? t('income_detail.one_offs.edit_title') : t('income_detail.one_offs.add_title')} onClose={row.cancel} onDone={row.saved}>
          {({ titleId, done }) => <OneOffForm source={source} editing={dialog.editing} titleId={titleId} onDone={done} />}
        </FormDialog>
      ) : null}
      <Table aria-label={t('income_detail.one_offs.table')}>
        <TableHeader>
          <TableRow>
            <TableHead>{t('income_detail.one_offs.date')}</TableHead>
            <TableHead className="text-right">{t('income_detail.one_offs.amount')}</TableHead>
            <TableHead>{t('income_detail.one_offs.note')}</TableHead>
            {writable ? <TableHead className="w-0"><span className="sr-only">{t('income_detail.col_actions')}</span></TableHead> : null}
          </TableRow>
        </TableHeader>
        <TableBody>
          {rows.length === 0 ? (
            <TableRow><TableCell colSpan={cols} className="text-muted-foreground">{t('income_detail.one_offs.empty')}</TableCell></TableRow>
          ) : rows.map((f) => (
            <TableRow key={f.id}>
              <TableCell><LocalDate value={f.occurredOn} /></TableCell>
              <TableCell className="text-right tabular-nums"><Money value={f.amount} tabular /></TableCell>
              <TableCell className="whitespace-normal">{f.note}</TableCell>
              {writable ? (
                <TableCell>
                  <OneOffRowActions source={source} oneOff={f} onEdit={() => row.open(f)} onDeleteStarted={() => row.setAnnouncement('')} onDeleted={row.announce} onReloaded={() => row.headingRef.current?.focus()} />
                </TableCell>
              ) : null}
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </Section>
  );
}
