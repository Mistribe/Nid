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
    let start: { x: number; left: number } | null = null;
    let dragged = false;

    const down = (e: PointerEvent) => {
      if (e.pointerType !== 'mouse' || e.button !== 0) return;
      start = { x: e.clientX, left: el.scrollLeft };
      dragged = false;
    };
    const move = (e: PointerEvent) => {
      if (!start) return;
      const dx = e.clientX - start.x;
      if (!dragged && Math.abs(dx) > THRESHOLD) {
        dragged = true;
        el.dataset.dragging = '';
      }
      if (dragged) el.scrollLeft = start.left - dx;
    };
    const up = () => {
      start = null;
      delete el.dataset.dragging;
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
