import { useId } from 'react';
import { useTranslation } from 'react-i18next';
import { Choice, DateField, type Form, type Person } from './offers/fields';
import { thenMode } from './offers';
import { RecurringOffer } from './steps';
import '@/locales/forms';

/**
 * 1E2b1 design §4.1 "Et ensuite ?": the phase that follows the offer; required after a launch price. The
 * following phase's start is filled (and kept in step with the purchase date) when leaving the offer step,
 * by `fillFollowingStart` (final review, finding 2), not here.
 */
export function ThenStep({ form, persons }: { form: Form; persons: Person[] }) {
  const { t } = useTranslation();
  const titleId = useId();
  const mode = thenMode({ offer: form.watch('offer'), trialVariant: form.watch('trialVariant') });
  const on = form.watch('then');

  if (mode === 'required') {
    return (
      <section aria-labelledby={titleId} className="space-y-4">
        <h2 id={titleId} className="text-lg font-semibold">{t('wizard.then.required_title')}</h2>
        <DateField form={form} name="nextStartsOn" label={t('wizard.then.next_start')} />
        <RecurringOffer form={form} persons={persons} withStart={false} />
      </section>
    );
  }
  return (
    <div className="space-y-4">
      <Choice label={t('wizard.then.title')} value={String(on)} onValueChange={(v) => form.setValue('then', v === 'true')}
        options={[{ value: 'false', label: t('wizard.then.none') }, { value: 'true', label: t('wizard.then.recurring') }]} />
      {on ? (
        <>
          <DateField form={form} name="startsOn" label={t('wizard.offer.start')} hint={t('wizard.then.hint')} />
          <RecurringOffer form={form} persons={persons} withStart={false} />
        </>
      ) : null}
    </div>
  );
}
