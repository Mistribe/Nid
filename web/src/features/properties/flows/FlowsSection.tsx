import { useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { formatLocalDate, LocalDate } from '@/components/LocalDate';
import { Button } from '@/components/ui/button';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { Section } from '@/components/page/Section';
import { money } from '../../loans/format';
import { FLOW, invalidateProperties, type PropertyDetail, type PropertyFlow } from '../api';
import { categoryText, kindText } from '../format';
import { RowActions } from '@/components/page/RowActions';
import { presentRefusal } from '../../loans/refusal';
import { useRowDialog } from '../useRowDialog';
import { FlowDialog } from './FlowForm';
import '@/locales/loans';

/**
 * 3D1 design §6: the property's expenses and income, newest first (the server lists them oldest first). Adding and
 * correcting open a dialog; the section owns its live region and where the focus lands.
 */
export function FlowsSection({ property }: { property: PropertyDetail }) {
  const { t } = useTranslation();
  const writable = property.canWrite;
  const s = useRowDialog(property.flows, 'flow-menu');
  const newestFirst = [...property.flows].reverse();
  return (
    <Section title={t('property_flows.title')} headingRef={s.headingRef} announcement={s.announcement}>
      {writable ? <Button ref={s.addButtonRef} type="button" variant="outline" size="sm" onClick={() => s.open()}>{t('property_flows.add')}</Button> : null}
      {writable && s.dialog ? <FlowDialog property={property} editing={s.dialog.editing} onClose={s.cancel} onDone={s.saved} /> : null}
      <Table aria-label={t('property_flows.table')}>
        <TableHeader>
          <TableRow>
            <TableHead>{t('loan_detail.col_date')}</TableHead>
            <TableHead>{t('property_flows.kind')}</TableHead>
            <TableHead>{t('property_flows.category')}</TableHead>
            <TableHead className="text-right">{t('property_flows.amount')}</TableHead>
            <TableHead>{t('loan_detail.col_note')}</TableHead>
            {writable ? <TableHead className="w-0"><span className="sr-only">{t('loan_detail.col_actions')}</span></TableHead> : null}
          </TableRow>
        </TableHeader>
        <TableBody>
          {newestFirst.length === 0 ? (
            <TableRow><TableCell colSpan={writable ? 6 : 5} className="text-muted-foreground">{t('property_flows.empty')}</TableCell></TableRow>
          ) : newestFirst.map((f) => (
            <TableRow key={f.id}>
              <TableCell><LocalDate value={f.occurredOn} /></TableCell>
              <TableCell>{kindText(t, f.kind)}</TableCell>
              <TableCell>{categoryText(t, f.category)}</TableCell>
              <TableCell className="text-right tabular-nums">{money(property.currency, f.amount)}</TableCell>
              <TableCell className="whitespace-normal">{f.note}</TableCell>
              {writable ? (
                <TableCell>
                  <FlowRowActions property={property} flow={f} onEdit={() => s.open(f)} onDeleteStarted={() => s.setAnnouncement('')}
                    onDeleted={s.announce} onReloaded={() => s.headingRef.current?.focus()} />
                </TableCell>
              ) : null}
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </Section>
  );
}

/** « Corriger » opens the dialog; « Supprimer » asks first. */
function FlowRowActions({ property, flow, onEdit, onDeleteStarted, onDeleted, onReloaded }: {
  property: PropertyDetail; flow: PropertyFlow; onEdit: () => void; onDeleteStarted: () => void; onDeleted: (message: string) => void; onReloaded: () => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const remove = $api.useMutation('delete', FLOW, {
    onSuccess: () => { void invalidateProperties(queryClient); onDeleted(t('property_flows.deleted')); },
  });
  return (
    <RowActions menuId={`flow-menu-${flow.id}`} menuLabel={t('loan_detail.row_menu_what', { date: formatLocalDate(flow.occurredOn), what: `${kindText(t, flow.kind)} ${categoryText(t, flow.category)}` })}
      items={[{ label: t('loan_detail.correct'), onSelect: onEdit }]} confirmation={t('property_flows.delete_confirm')} remove={remove} onDeleteStarted={onDeleteStarted} onReloaded={onReloaded}
      present={presentRefusal} invalidate={invalidateProperties}
      onConfirm={() => remove.mutate({ params: { path: { id: property.id, flowId: flow.id } } })} />
  );
}
