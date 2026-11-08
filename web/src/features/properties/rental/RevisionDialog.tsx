import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { DateField } from '@/components/form/DateField';
import { Field } from '@/components/form/Field';
import { FormDialog } from '@/components/FormDialog';
import { monthText } from '@/components/LocalDate';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { money } from '../../loans/format';
import { presentRefusal } from '../../loans/refusal';
import { todayIso } from '@/lib/today';
import { fieldErrorsOf, messageFor } from '@/components/form/fieldErrors';
import { invalidateProperties, type PropertyDetail, type PropertyTenancy } from '@/api/properties';
import '@/locales/loans';
import '@/locales/properties';

export const REVISION = '/api/v1/realestate/properties/{id}/tenancies/{tenancyId}/revision' as const;

/** « 2026-Q2 » as the French write it. */
const quarterText = (quarter: string) => quarter.replace('-Q', '-T');

const REASONS = ['unavailable', 'not_published'] as const;
const signed = new Intl.NumberFormat('fr-FR', { signDisplay: 'exceptZero', minimumFractionDigits: 1, maximumFractionDigits: 1 });

/**
 * « Réviser le loyer »: the two IRL values and the rents as the server worked them out, and the date the new rent takes
 * effect (the server's default, never after today). The server refuses a date outside its window on the field. Without a revision
 * due, or with its rent changed, the rent typed is sent: a free revision.
 */
export function RevisionDialog({ property, tenancy, onClose, onDone }: {
  property: PropertyDetail; tenancy: PropertyTenancy; onClose: () => void; onDone: (message: string) => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const revision = tenancy.revision;
  const [effectiveOn, setEffectiveOn] = useState(revision?.defaultEffectiveOn ?? todayIso());
  const [rent, setRent] = useState(revision?.newRent ?? '');
  const [error, setError] = useState<string | undefined>();
  const [rentError, setRentError] = useState<string | undefined>();
  const [problem, setProblem] = useState(false);
  const accept = $api.useMutation('post', REVISION);
  if (!revision) return null;

  return (
    <FormDialog title={t('property_rental.revision_title')} onClose={onClose} onDone={onDone}>
      {({ titleId, done }) => (
        <form aria-labelledby={titleId} noValidate className="space-y-3" onSubmit={(ev) => {
          ev.preventDefault();
          ev.stopPropagation();
          setProblem(false);
          setError(undefined);
          setRentError(undefined);
          const body = rent === revision.newRent ? { effectiveOn } : { effectiveOn, newRent: rent };
          accept.mutate({ params: { path: { id: property.id, tenancyId: tenancy.id } }, body }, {
            onSuccess: () => { void invalidateProperties(queryClient); done(t('property_rental.revision_done')); },
            onError: (e) => {
              const fields = fieldErrorsOf(e);
              const date = fields.find((f) => f.path === 'effectiveOn');
              const amount = fields.find((f) => f.path === 'newRent');
              if (date) setError(messageFor(t, date));
              if (amount) setRentError(messageFor(t, amount));
              if (!date && !amount) setProblem(true);
            },
          });
        }}>
          {problem ? <ProblemMessage problem={presentRefusal(t, accept.error)} /> : null}
          {revision.fromQuarter && revision.toQuarter ? (
            <p>{t('property_rental.revision_irl', {
              from: quarterText(revision.fromQuarter), fromValue: revision.fromValue, to: quarterText(revision.toQuarter), toValue: revision.toValue,
            })}</p>
          ) : null}
          {revision.irlChangePct != null ? (
            <p className="text-sm text-muted-foreground">{revision.cpi.percent != null
              ? t('property_rental.revision_compare', {
                irl: signed.format(revision.irlChangePct), cpi: signed.format(revision.cpi.percent), month: revision.cpi.inflationTo ? monthText(revision.cpi.inflationTo) : '',
              })
              : t('property_rental.revision_compare_missing', {
                irl: signed.format(revision.irlChangePct), reason: t(`property_rental.revision_cpi_missing.${REASONS.find((r) => r === revision.cpi.missing) ?? 'unavailable'}`),
              })}</p>
          ) : null}
          {revision.currentRent && revision.newRent ? (
            <p className="font-medium">{t('property_rental.revision_rents', {
              current: money(property.currency, revision.currentRent), next: money(property.currency, revision.newRent),
            })}</p>
          ) : null}
          {revision.dpeUnknown ? <p role="note" className="text-sm text-muted-foreground">{t('property_rental.revision_dpe_unknown')}</p> : null}
          <Field label={t('property_rental.revision_new_rent')} error={rentError}
            hint={revision.newRent || !revision.currentRent ? undefined : t('property_rental.revision_current_rent', { current: money(property.currency, revision.currentRent) })}>
            {(c) => <Input inputMode="decimal" {...c} value={rent} onChange={(ev) => setRent(ev.target.value)} />}
          </Field>
          <Field label={t('property_rental.revision_date')} hint={t('property_rental.revision_date_hint')} error={error}>
            {(c) => <DateField {...c} max={todayIso()} value={effectiveOn} onChange={setEffectiveOn} />}
          </Field>
          <div className="flex justify-end gap-2">
            <Button type="button" variant="outline" onClick={() => done()}>{t('loan_detail.cancel')}</Button>
            <Button type="submit" disabled={accept.isPending}>{t('property_rental.revision_submit')}</Button>
          </div>
        </form>
      )}
    </FormDialog>
  );
}
