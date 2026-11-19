import { useEffect, useState } from 'react';

/**
 * Issue 215 (local-accounts.md §10): the `otpauth://` address as a QR code the authenticator app scans, drawn in the browser
 * so the secret never leaves it. The encoder loads with the activation dialog only (initial JS budget). Black on white
 * whatever the theme, as a printed letter is: some scanners do not read a light code on a dark ground.
 */
export function TotpQrCode({ uri, label }: { uri: string; label: string }) {
  const [modules, setModules] = useState<boolean[][] | null>(null);

  useEffect(() => {
    let live = true;
    void import('qrcode-generator').then(({ default: qrcode }) => {
      // Type 0: the smallest version that holds the address; level M, the apps' usual choice.
      const code = qrcode(0, 'M');
      code.addData(uri);
      code.make();
      const size = code.getModuleCount();
      const rows = Array.from({ length: size }, (_, row) => Array.from({ length: size }, (_, col) => code.isDark(row, col)));
      if (live) setModules(rows);
    }, () => { if (live) setModules(null); });
    return () => { live = false; };
  }, [uri]);

  if (!modules) return <div className="size-44 rounded-md bg-muted" aria-hidden="true" />;
  const quiet = 4;
  const size = modules.length + quiet * 2;
  const path = modules.flatMap((row, y) => row.map((dark, x) => (dark ? `M${x + quiet} ${y + quiet}h1v1h-1z` : ''))).join('');
  return (
    <svg role="img" aria-label={label} viewBox={`0 0 ${size} ${size}`} shapeRendering="crispEdges" className="size-44 rounded-md bg-white fill-black">
      <path d={path} />
    </svg>
  );
}
