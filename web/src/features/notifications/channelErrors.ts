import type { TFunction } from 'i18next';
import '@/locales/notifications';

/** A sentence for the code of a failed send (« notifications.channel_unreachable 421 »); the raw text for a code it does not know. */
export function channelErrorText(t: TFunction, error: string): string {
  const code = error.split(' ')[0];
  if (code === 'notifications.channel_unreachable') return t('channel_errors.unreachable');
  if (code === 'notifications.channel_rejected') return t('channel_errors.rejected');
  return error;
}
