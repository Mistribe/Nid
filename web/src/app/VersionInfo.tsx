import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { Tooltip, TooltipContent, TooltipProvider, TooltipTrigger } from '@/components/ui/tooltip';

/** The web app's version; hovering or focusing it asks the API for its own. */
export function VersionInfo({ version }: { version: string }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const [open, setOpen] = useState(false);
  const api = $api.useQuery('get', '/api/v1/platform/version', {}, { enabled: open, staleTime: Infinity });
  return (
    <TooltipProvider>
      <Tooltip open={open} onOpenChange={setOpen}>
        <TooltipTrigger asChild>
          <button type="button" className="px-3 pt-1 text-left text-xs text-muted-foreground">{t('app.version', { version })}</button>
        </TooltipTrigger>
        <TooltipContent side="right">
          {t('app.api_version', { version: api.data?.version ?? (api.isError ? t('app.api_version_unknown') : '…') })}
        </TooltipContent>
      </Tooltip>
    </TooltipProvider>
  );
}
