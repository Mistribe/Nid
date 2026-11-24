import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';

/**
 * Copies `text`. Over plain HTTP (`AllowInsecureHttp`) the browser has no `navigator.clipboard`: the text goes through a
 * hidden field and the browser's copy command, run in the click itself. Rejects when the browser refuses both.
 */
export async function copyText(text: string): Promise<void> {
  if (navigator.clipboard) {
    try {
      await navigator.clipboard.writeText(text);
      return;
    } catch { /* refused: the copy command may still work */ }
  }
  if (!copyCommand(text)) throw new Error('copy refused');
}

function copyCommand(text: string): boolean {
  const field = document.createElement('textarea');
  field.value = text;
  field.readOnly = true;
  field.style.position = 'fixed';
  field.style.opacity = '0';
  // Beside the focused control, so an open dialog's focus trap keeps the selection.
  (document.activeElement?.parentElement ?? document.body).append(field);
  const back = document.activeElement as HTMLElement | null;
  try {
    field.focus();
    field.select();
    return document.execCommand('copy');
  } catch {
    return false;
  } finally {
    field.remove();
    back?.focus();
  }
}

/** #641: every copy button's click: `onCopied` once copied, otherwise a message to copy the text by hand. */
export function useCopy(): (text: string, onCopied: () => void) => void {
  const { t } = useTranslation();
  return (text, onCopied) => {
    void copyText(text).then(onCopied, () => toast.error(t('common.copy_failed')));
  };
}
