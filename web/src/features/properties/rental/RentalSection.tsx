import type { TFunction } from 'i18next';
import { CircleDot } from 'lucide-react';
import { useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { formatLocalDate } from '@/components/LocalDate';
import { Button } from '@/components/ui/button';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { Section } from '../../income/Section';
import { money } from '../../loans/format';
import { invalidateProperties, TENANCY, type PropertyDetail, type PropertyTenancy } from '../api';
import { RowActions } from '@/components/page/RowActions';
import { presentRefusal } from '../../loans/refusal';
import { useRowDialog } from '../useRowDialog';
import { RentalFigures } from './RentalFigures';
import { TenancyDialog } from './TenancyForm';
import '@/locales/loans';
import '@/locales/properties';

/** « depuis le … », or « du … au … » once it has an end. */
const periodText = (t: TFunction, x: PropertyTenancy) => (x.endsOn
  ? t('property_rental.between', { start: formatLocalDate(x.startsOn), end: formatLocalDate(x.endsOn) })
  : t('property_rental.since', { start: formatLocalDate(x.startsOn) }));

/**
 * Spec 3D2 §3: « Location » — the rental figures, then the tenancies newest first (the server lists them by start); adding
 * and correcting open a dialog, the section owns its live region and where the focus lands.
 */
export function RentalSection({ property }: { property: PropertyDetail }) {
  const { t } = useTranslation();
  const writable = property.canWrite;
  const s = useRowDialog(property.tenancies, 'tenancy-menu');
  const newestFirst = [...property.tenancies].reverse();
  return (
    <Section title={t('property_rental.title')} headingRef={s.headingRef} announcement={s.announcement}>
      <RentalFigures property={property} />
      {writable ? <Button ref={s.addButtonRef} type="button" variant="outline" size="sm" onClick={() => s.open()}>{t('property_rental.add')}</Button> : null}
      {writable && s.dialog ? <TenancyDialog property={property} editing={s.dialog.editing} onClose={s.cancel} onDone={s.saved} /> : null}
      {newestFirst.length === 0 ? null : (
        <Table aria-label={t('property_rental.table')}>
          <TableHeader>
            <TableRow>
              <TableHead>{t('property_rental.label')}</TableHead>
              <TableHead>{t('property_rental.period')}</TableHead>
              <TableHead className="text-right">{t('property_rental.rent')}</TableHead>
              <TableHead className="text-right">{t('property_rental.charges')}</TableHead>
              <TableHead className="text-right">{t('property_rental.deposit')}</TableHead>
              <TableHead>{t('loan_detail.col_note')}</TableHead>
              {writable ? <TableHead className="w-0"><span className="sr-only">{t('loan_detail.col_actions')}</span></TableHead> : null}
            </TableRow>
          </TableHeader>
          <TableBody>
            {newestFirst.map((x) => (
              <TableRow key={x.id}>
                <TableCell>
                  <span className="flex flex-wrap items-center gap-2">
                    {x.label}
                    {/* Colour never alone: an icon and the word, on the tenancy the server says covers today. */}
                    {x.current ? (
                      <span className="inline-flex items-center gap-1"><CircleDot aria-hidden="true" className="size-4" />{t('property_rental.current')}</span>
                    ) : null}
                  </span>
                </TableCell>
                <TableCell>{periodText(t, x)}</TableCell>
                <TableCell className="text-right tabular-nums">{money(property.currency, x.rentMonthly)}</TableCell>
                <TableCell className="text-right tabular-nums">{money(property.currency, x.chargesMonthly)}</TableCell>
                <TableCell className="text-right tabular-nums">{x.deposit ? money(property.currency, x.deposit) : null}</TableCell>
                <TableCell className="whitespace-normal">{x.note}</TableCell>
                {writable ? (
                  <TableCell>
                    <TenancyRowActions property={property} tenancy={x} onEdit={() => s.open(x)} onDeleteStarted={() => s.setAnnouncement('')}
                      onDeleted={s.announce} onReloaded={() => s.headingRef.current?.focus()} />
                  </TableCell>
                ) : null}
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
    </Section>
  );
}

/** « Corriger » opens the dialog; « Supprimer » asks first and sends the version read. */
function TenancyRowActions({ property, tenancy, onEdit, onDeleteStarted, onDeleted, onReloaded }: {
  property: PropertyDetail; tenancy: PropertyTenancy; onEdit: () => void; onDeleteStarted: () => void; onDeleted: (message: string) => void;
  onReloaded: () => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const remove = $api.useMutation('delete', TENANCY, {
    onSuccess: () => { void invalidateProperties(queryClient); onDeleted(t('property_rental.deleted')); },
  });
  return (
    <RowActions menuId={`tenancy-menu-${tenancy.id}`} menuLabel={t('loan_detail.row_menu_what', { date: formatLocalDate(tenancy.startsOn), what: tenancy.label })}
      items={[{ label: t('loan_detail.correct'), onSelect: onEdit }]} confirmation={t('property_rental.delete_confirm')} remove={remove} onDeleteStarted={onDeleteStarted} onReloaded={onReloaded}
      present={presentRefusal} invalidate={invalidateProperties}
      onConfirm={() => remove.mutate({ params: { path: { id: property.id, tenancyId: tenancy.id }, query: { version: tenancy.version } } })} />
  );
}
