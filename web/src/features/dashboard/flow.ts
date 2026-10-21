/** A band's top and bottom in drawing units. */
export type Span = { y0: number; y1: number };

/** Stacks marks by their `size` (a plotting value, see `toPlot`) over `height`, `gap` between them. */
export function stack<T extends { size: number }>(marks: T[], height: number, gap: number): (T & Span)[] {
  const total = marks.reduce((a, m) => a + m.size, 0);
  const k = total > 0 ? (height - gap * (marks.length - 1)) / total : 0;
  let y = 0;
  return marks.map((m) => {
    const span = { ...m, y0: y, y1: y + m.size * k };
    y = span.y1 + gap;
    return span;
  });
}

/** Label centres as close to `wanted` as they can be, at least `line` apart and inside [line / 2, height − line / 2]. */
export function spread(wanted: number[], height: number, line: number): number[] {
  const down: number[] = [];
  wanted.forEach((w, i) => down.push(Math.max(w, i === 0 ? line / 2 : down[i - 1]! + line)));
  const up: number[] = [];
  for (let i = down.length - 1; i >= 0; i--) up.unshift(Math.min(down[i]!, up.length === 0 ? height - line / 2 : up[0]! - line));
  return up;
}

/** A horizontal band from `a` at x0 to `b` at x1, its edges as two cubic curves. */
export function band(x0: number, a: Span, x1: number, b: Span): string {
  const m = (x0 + x1) / 2;
  return `M${x0} ${a.y0}C${m} ${a.y0},${m} ${b.y0},${x1} ${b.y0}L${x1} ${b.y1}C${m} ${b.y1},${m} ${a.y1},${x0} ${a.y1}Z`;
}
