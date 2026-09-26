/** Saves a Blob under `fileName` through a temporary `download` link (1E4 design §1: no file-writing API). */
export function downloadBlob(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = fileName;
  link.rel = 'noopener';
  document.body.append(link);
  link.click();
  link.remove();
  // Long after the click: some Safari and Firefox versions cancel a download whose URL goes on the next tick. A few
  // hundred kilobytes held 40 s longer cost nothing.
  setTimeout(() => URL.revokeObjectURL(url), 40_000);
}
