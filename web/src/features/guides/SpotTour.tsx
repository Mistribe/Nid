import { X } from 'lucide-react';
import { useEffect, useId, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '@/components/ui/button';
import { findTarget, type Guide } from './guides';

/**
 * Spec §4.4: B, the on-screen tour. Each step rings its target, scrolled to the top of the viewport, and explains it in a
 * non-modal card fixed at the bottom, above the phone's tab bar. Escape, the close button and « Terminer » are `onEnd`.
 */
export function SpotTour({ guide, onEnd }: { guide: Guide; onEnd: () => void }) {
  const { t } = useTranslation();
  // The count is the steps whose target is in the page when the tour starts.
  const [steps] = useState(() => guide.tour.filter((s) => findTarget(guide, s)));
  const [i, setI] = useState(0);
  const card = useRef<HTMLDivElement>(null);
  const titleId = useId();
  const countId = useId();
  const bodyId = useId();
  const step = steps[i];

  useEffect(() => {
    const target = step === undefined ? null : findTarget(guide, step);
    if (!target) return;
    target.setAttribute('data-guide-active', '');
    const reduce = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    target.scrollIntoView({ block: 'start', behavior: reduce ? 'auto' : 'smooth' });
    // The card is always in view; scrolling to it would cut the smooth scroll short.
    card.current?.focus({ preventScroll: true });
    return () => target.removeAttribute('data-guide-active');
  }, [guide, step]);

  // The card is not modal: the focus may be anywhere in the page, and Escape still ends the tour.
  useEffect(() => {
    const escape = (event: KeyboardEvent) => { if (event.key === 'Escape') onEnd(); };
    document.addEventListener('keydown', escape);
    return () => document.removeEventListener('keydown', escape);
  }, [onEnd]);

  if (step === undefined) return null;
  // A step whose target has left the page since the start is skipped.
  const move = (by: 1 | -1) => {
    let j = i + by;
    while (steps[j] !== undefined && !findTarget(guide, steps[j]!)) j += by;
    if (j >= steps.length) onEnd();
    else if (j >= 0) setI(j);
  };
  const last = i === steps.length - 1;
  // i18next types its keys; every tour step shares loans.figures' shape (guides.test.ts checks they exist).
  const key = `guides.${guide.id}.tour.${step}` as 'guides.loans.tour.figures';

  return (
    <div ref={card} role="dialog" aria-labelledby={titleId} aria-describedby={`${countId} ${bodyId}`} tabIndex={-1}
      className="fixed inset-x-4 bottom-20 z-40 mx-auto max-w-md space-y-3 rounded-md border bg-background p-4 text-foreground shadow-lg md:bottom-4">
      <div className="flex items-start justify-between gap-2">
        <h2 id={titleId} className="text-lg font-semibold">{t(`${key}.title`)}</h2>
        <Button variant="ghost" size="icon" aria-label={t('guides.common.close')} onClick={onEnd}><X aria-hidden="true" className="size-5" /></Button>
      </div>
      <p id={countId} className="text-sm text-muted-foreground">{t('guides.common.step', { n: i + 1, total: steps.length })}</p>
      <p id={bodyId} className="text-sm md:text-base">{t(`${key}.body`)}</p>
      <div className="flex flex-wrap justify-end gap-2">
        {i > 0 ? <Button variant="outline" onClick={() => move(-1)}>{t('guides.common.previous')}</Button> : null}
        <Button onClick={last ? onEnd : () => move(1)}>{t(last ? 'guides.common.finish' : 'guides.common.next')}</Button>
      </div>
    </div>
  );
}
