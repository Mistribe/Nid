let refocus = false;

export const resetFocus = () => { refocus = false; };
/** A decision moved to another proposal: the next page heading takes focus (the form it came from is gone). */
export const focusNextHeading = () => { refocus = true; };
export const focusHeading = (el: HTMLElement | null) => { if (el && refocus) { refocus = false; el.focus(); } };
