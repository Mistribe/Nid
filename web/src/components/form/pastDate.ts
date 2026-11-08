import type { TFunction } from 'i18next';
import { z } from 'zod';
import { ISO_DATE } from '@/lib/amount';
import { todayIso } from '@/lib/today';
import '@/locales/forms';

/** A date no later than today: the native `max` of the old date inputs, now said where the form validates. */
export const pastDate = (t: TFunction) =>
  z.string().regex(ISO_DATE, t('edit.required')).refine((d) => d <= todayIso(), t('field_error.core_date_in_future'));
