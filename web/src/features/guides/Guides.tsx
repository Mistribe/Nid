import { useRouterState } from '@tanstack/react-router';
import { CircleHelp } from 'lucide-react';
import { lazy, Suspense, useEffect, useRef, useState } from 'react';
import { flushSync } from 'react-dom';
import { useTranslation } from 'react-i18next';
import { section } from '@/app/nav';
import { useSession } from '@/app/Providers';
import { Button } from '@/components/ui/button';
import { guideFor, isUnseen, type Guide } from './guides';
import { useMarkSeen } from './useMarkSeen';

const GuideIntro = lazy(() => import('./GuideIntro').then((m) => ({ default: m.GuideIntro })));
const SpotTour = lazy(() => import('./SpotTour').then((m) => ({ default: m.SpotTour })));

/** Spec §4.2 and §4.5: plays a module's introduction once, and « ? » replays it. Sits in the Shell's header. */
export function Guides() {
  const { t } = useTranslation();
  const { session } = useSession();
  const markSeen = useMarkSeen();
  const pathname = useRouterState({ select: (s) => s.location.pathname });
  const current = section(pathname);
  const guide = guideFor(current);
  const [state, setState] = useState<{ open: 'intro' | 'tour' | null; guide?: Guide }>({ open: null });
  const help = useRef<HTMLButtonElement>(null);
  // The section effect runs on a section change only; it reads the rest as it stands then.
  const latest = useRef({ state, seen: session.preferences.guidesSeen, markSeen });
  latest.current = { state, seen: session.preferences.guidesSeen, markSeen };

  useEffect(() => {
    const { state, seen, markSeen } = latest.current;
    // Leaving a section closes its guide, which counts as seen (spec §4.2).
    if (state.open === 'intro' && state.guide && state.guide.section !== current) markSeen(state.guide);
    const next = guideFor(current);
    setState(next && isUnseen(next, seen) ? { open: 'intro', guide: next } : { open: null });
  }, [current]);

  // Spec §4.4: any route change ends the tour, within the section too (/loans/a to /loans/b).
  useEffect(() => setState((s) => (s.open === 'tour' ? { open: null } : s)), [pathname]);

  const intro = state.open === 'intro' ? state.guide : undefined;
  const tour = state.open === 'tour' ? state.guide : undefined;
  const end = () => {
    // The dialog must be gone before « ? » can take the focus back.
    flushSync(() => setState({ open: null }));
    help.current?.focus();
  };
  const close = (closed: Guide) => { markSeen(closed); end(); };
  // B itself never marks a guide: A, which leads to it, counts as seen once B starts.
  const show = (shown: Guide) => { markSeen(shown); setState({ open: 'tour', guide: shown }); };

  return (
    <>
      {guide ? (
        <Button ref={help} variant="ghost" size="icon" aria-label={t('guides.common.help')} onClick={() => setState({ open: 'intro', guide })}>
          <CircleHelp aria-hidden="true" className="size-5" />
        </Button>
      ) : null}
      {intro ? (
        <Suspense fallback={null}>
          <GuideIntro key={intro.id} guide={intro} onClose={() => close(intro)} onShow={() => show(intro)} />
        </Suspense>
      ) : null}
      {tour ? (
        <Suspense fallback={null}>
          <SpotTour key={tour.id} guide={tour} onEnd={end} />
        </Suspense>
      ) : null}
    </>
  );
}
