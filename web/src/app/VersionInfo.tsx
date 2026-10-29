import { lazy, Suspense, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { Tooltip, TooltipContent, TooltipProvider, TooltipTrigger } from '@/components/ui/tooltip';

const Changelog = lazy(() => import('./Changelog').then((m) => ({ default: m.Changelog })));

/** The web app's version; hovering or focusing it asks the API for its own, a click lists what changed in each version. */
export function VersionInfo({ version }: { version: string }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const [open, setOpen] = useState(false);
  const [changelog, setChangelog] = useState(false);
  const api = $api.useQuery('get', '/api/v1/platform/version', {}, { enabled: open, staleTime: Infinity });
  return (
    <>
      <TooltipProvider>
        <Tooltip open={open && !changelog} onOpenChange={setOpen}>
          <TooltipTrigger asChild>
            <button type="button" aria-haspopup="dialog" onClick={() => setChangelog(true)}
              className="px-3 pt-1 text-left text-xs text-muted-foreground hover:text-foreground hover:underline">{t('app.version', { version })}</button>
          </TooltipTrigger>
          <TooltipContent side="right">
            {t('app.api_version', { version: api.data?.version ?? (api.isError ? t('app.api_version_unknown') : '…') })}
          </TooltipContent>
        </Tooltip>
      </TooltipProvider>
      {changelog ? <Suspense fallback={null}><Changelog onClose={() => setChangelog(false)} /></Suspense> : null}
    </>
  );
}
