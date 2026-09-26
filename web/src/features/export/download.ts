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
  // After the click is handled: a browser may read the URL once the event is over.
  setTimeout(() => URL.revokeObjectURL(url), 0);
}
