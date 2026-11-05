import { Link, useRouterState } from '@tanstack/react-router';
import { CircleAlert, CircleHelp, Lock } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import type { Presentation } from '@/api/errors';
import { moduleOf } from '@/app/nav';
import { NestArcs } from '@/components/Logo';
import { ModuleTile } from '@/components/ModuleTile';
import { usePageTrail, type Crumb } from '@/components/page/trail';
import { Button, buttonVariants } from '@/components/ui/button';

const KIND = { not_found: CircleHelp, forbidden: Lock, unexpected: CircleAlert } as const;

/**
 * A page that could not be shown, built like `EmptyState` (issue 362): the module's tile over the nest (from the address),
 * an `h1`, one sentence, the ways out (« Retour » stays the top bar's, as on every page). A 404 never says whether the thing is absent or hidden (architecture.md §5); the
 * trail names the module, never the error. Anything else than 404 and 403 offers to try again, with the request id.
 */
export function ErrorPage({ problem, crumbs, onRetry }: {
  problem: Presentation; /** The page's own trail (a detail page's), else the module's. */ crumbs?: Crumb[]; onRetry?: () => void;
}) {
  const { t } = useTranslation();
  const pathname = useRouterState({ select: (s) => s.location.pathname });
  const module = moduleOf(pathname);
  const kind = problem.kind === 'not_found' || problem.kind === 'forbidden' ? problem.kind : 'unexpected';
  // The module's list is a way back only from below it: on the module's own page it would lead to the same error.
  const back = module && module.to !== pathname ? module : undefined;
  usePageTrail({ crumbs: crumbs ?? (back ? [{ label: t(back.label), to: back.to }] : undefined) });
  const home = <Link to="/" className={buttonVariants({ variant: back || kind === 'unexpected' ? 'outline' : 'default' })}>{t('error_page.home')}</Link>;
  return (
    <section className="relative isolate mx-auto flex max-w-md flex-col items-center gap-3 overflow-hidden px-6 py-12 text-center">
      <div aria-hidden="true" className="relative mb-6">
        <NestArcs className="absolute -top-6 left-1/2 -z-10 w-32 -translate-x-1/2 text-muted-foreground opacity-20" />
        <ModuleTile icon={module?.icon ?? KIND[kind]} tile={module?.tile ?? 'neutral'} size="lg" />
      </div>
      <h1 className="text-title">{t(`error_page.${kind}.title`)}</h1>
      <p className="text-muted-foreground">{t(`error_page.${kind}.body`)}</p>
      {kind === 'unexpected' && problem.requestId ? <p className="text-sm text-muted-foreground">{t('problem.request_id', { id: problem.requestId })}</p> : null}
      <div className="mt-2 flex flex-wrap items-center justify-center gap-2">
        {kind === 'unexpected' ? <Button type="button" onClick={onRetry ?? (() => window.location.reload())}>{t('error_page.retry')}</Button> : null}
        {kind !== 'unexpected' && back ? <Link to={back.to} className={buttonVariants()}>{t('error_page.back', { module: t(back.label) })}</Link> : null}
        {home}
      </div>
    </section>
  );
}
