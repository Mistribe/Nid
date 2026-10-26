import type { TFunction } from 'i18next';
import type { FieldError } from '@/api/errors';
import '@/locales/notifications';

/** A sentence for the code of a failed send (« notifications.channel_unreachable 421 »); the raw text for a code it does not know. */
export function channelErrorText(t: TFunction, error: string): string {
  const code = error.split(' ')[0];
  if (code === 'notifications.channel_unreachable') return t('channel_errors.unreachable');
  if (code === 'notifications.channel_rejected') return t('channel_errors.rejected');
  if (code === 'notifications.channel_broken') return t('channel_errors.broken');
  return error;
}

/** The sentence for a Discord field error (`notifications.invalid_webhook`…); the server's text for a code it does not know. */
export const discordFieldText = (t: TFunction, f: FieldError): string =>
  t(`discord.${f.code.replace('notifications.', '')}`, { defaultValue: f.message });
