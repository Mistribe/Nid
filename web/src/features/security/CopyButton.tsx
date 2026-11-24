import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '@/components/ui/button';
import '@/locales/security';
import { useCopy } from '@/lib/clipboard';

/** Copies `value`; `label` names what it copies for a screen reader when several sit side by side. */
export function CopyButton({ value, label }: { value: string; label?: string }) {
  const { t } = useTranslation();
  const [copied, setCopied] = useState(false);
  const copy = useCopy();
  return (
    <Button type="button" variant="outline" size="sm" aria-label={label} onClick={() => copy(value, () => setCopied(true))}>
      {t(copied ? 'security.totp.copied' : 'security.totp.copy')}
    </Button>
  );
}
