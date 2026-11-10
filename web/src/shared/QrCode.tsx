// A QR code drawn in the page (no image service): for the authenticator setup's otpauth:// address.
import { useMemo } from 'react';
import qrcode from 'qrcode-generator';

export function QrCode({ value, size = 184, label }: { value: string; size?: number; label: string }) {
  const { count, path } = useMemo(() => {
    const qr = qrcode(0, 'M');   // 0: the smallest version that fits
    qr.addData(value);
    qr.make();
    const n = qr.getModuleCount();
    let d = '';
    for (let row = 0; row < n; row++)
      for (let col = 0; col < n; col++)
        if (qr.isDark(row, col)) d += `M${col + 4} ${row + 4}h1v1h-1z`;
    return { count: n + 8, path: d };   // 4 modules of quiet zone on each side
  }, [value]);
  // Always dark on white, whatever the theme: phone cameras read that best.
  return (
    <svg className="qr-code" role="img" aria-label={label} width={size} height={size} viewBox={`0 0 ${count} ${count}`} shapeRendering="crispEdges">
      <rect width={count} height={count} fill="#fff" />
      <path d={path} fill="#000" />
    </svg>
  );
}
