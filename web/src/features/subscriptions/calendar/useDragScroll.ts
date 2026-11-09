import { useEffect, type RefObject } from 'react';

/** Below this many pixels a press is a click, past it a drag. */
const THRESHOLD = 5;

/**
 * Grab-and-slide scrolling for a horizontal band, with the mouse only (touch scrolls natively). A drag that ends on a
 * link must not follow it: the click after a drag is swallowed before it reaches the link.
 */
export function useDragScroll(ref: RefObject<HTMLElement | null>): void {
  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    // Moves are applied one by one (never from the press's position): months added on the left mid-drag shift the band.
    let drag: { from: number; last: number } | null = null;
    let dragged = false;

    const down = (e: PointerEvent) => {
      // The band's own scrollbar (below its content box) scrolls natively.
      if (e.pointerType !== 'mouse' || e.button !== 0 || e.offsetY > el.clientHeight) return;
      drag = { from: e.clientX, last: e.clientX };
      dragged = false;
    };
    const move = (e: PointerEvent) => {
      if (!drag) return;
      // The release happened where the page could not see it (on the scrollbar, outside the window).
      if (!(e.buttons & 1)) return up();
      if (!dragged && Math.abs(e.clientX - drag.from) > THRESHOLD) {
        dragged = true;
        el.dataset.dragging = '';
      }
      if (dragged) el.scrollLeft -= e.clientX - drag.last;
      drag.last = e.clientX;
    };
    const up = () => {
      drag = null;
      delete el.dataset.dragging;
      // The click that ends a drag comes right after this; a drag ending elsewhere must not eat a later click.
      setTimeout(() => { dragged = false; });
    };
    const click = (e: MouseEvent) => {
      if (!dragged) return;
      dragged = false;
      e.preventDefault();
      e.stopPropagation();
    };
    // A link would start the browser's own drag (and cancel the pointer events).
    const noNativeDrag = (e: DragEvent) => e.preventDefault();

    el.addEventListener('pointerdown', down);
    window.addEventListener('pointermove', move);
    window.addEventListener('pointerup', up);
    el.addEventListener('click', click, true);
    el.addEventListener('dragstart', noNativeDrag);
    return () => {
      el.removeEventListener('pointerdown', down);
      window.removeEventListener('pointermove', move);
      window.removeEventListener('pointerup', up);
      el.removeEventListener('click', click, true);
      el.removeEventListener('dragstart', noNativeDrag);
    };
  }, [ref]);
}
