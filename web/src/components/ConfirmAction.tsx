import { lazy, Suspense, useEffect, useRef, useState, type RefObject } from 'react';
import { Button } from '@/components/ui/button';

const ConfirmDialog = lazy(() => import('./ConfirmDialog'));

/**
 * The same dialog for an action started elsewhere (a `⋯` menu item): the page keeps `asking`, so a destructive menu item still asks.
 * With no trigger of its own, the dialog hands the focus back to `returnFocus` (the `⋯`) when it closes, confirmed or not; a
 * success that lands elsewhere moves it on from there.
 */
export function ConfirmFromMenu({ label, confirmation, pending, returnFocus, onConfirm, onClose }: {
  label: string; confirmation: string; pending?: boolean; returnFocus: RefObject<HTMLElement | null>; onConfirm: () => void; onClose: () => void;
}) {
  return (
    <Suspense fallback={null}>
      <ConfirmDialog label={label} confirmation={confirmation} pending={pending} returnFocus={returnFocus} onClose={onClose} onConfirm={() => { onClose(); onConfirm(); }} />
    </Suspense>
  );
}

/**
 * A write is confirmed before it happens (screens.md §12): the button opens an alert dialog that states the effect and
 * asks. Focus lands on Confirm when it opens and comes back to the trigger once the dialog is dismissed (Cancel, Escape
 * or Confirm). The dialog is loaded on the first click: Radix's dialog stays out of the initial bundle.
 */
export function ConfirmAction(props: {
  label: string;
  /** The dialog's title when it is not the button's label. */
  title?: string;
  confirmation: string;
  onConfirm: () => void;
  disabled?: boolean;
  /** Disables the trigger and the Confirm button while the mutation this action starts is in flight. */
  pending?: boolean;
}) {
  const [asking, setAsking] = useState(false);
  const triggerRef = useRef<HTMLButtonElement>(null);
  const wasAsking = useRef(false);

  useEffect(() => {
    if (wasAsking.current && !asking) triggerRef.current?.focus();
    wasAsking.current = asking;
  }, [asking]);

  return (
    <>
      <Button ref={triggerRef} variant="outline" size="sm" disabled={props.disabled || props.pending} onClick={() => setAsking(true)}>{props.label}</Button>
      {asking ? (
        <Suspense fallback={null}>
          <ConfirmDialog label={props.label} title={props.title} confirmation={props.confirmation} pending={props.pending} onClose={() => setAsking(false)}
            onConfirm={() => { setAsking(false); props.onConfirm(); }} />
        </Suspense>
      ) : null}
    </>
  );
}
