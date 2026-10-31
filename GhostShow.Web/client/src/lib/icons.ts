// Small inline icons (SVG path data, 24×24), so the page needs no icon font.
export const icons = {
  play: 'M8 5v14l11-7z',
  pause: 'M6 5h4v14H6zm8 0h4v14h-4z',
  stop: 'M6 6h12v12H6z',
  restart: 'M6 6h2v12H6zm3.5 6L18 18V6z',
  open: 'M14 3h7v7h-2V6.41l-8.29 8.3-1.42-1.42L17.59 5H14zM5 5h6v2H6v11h11v-5h2v6a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V6a1 1 0 0 1 1-1z',
} as const;

/** A show button's key as the desktop shows it ("D1" → "1", "F5" stays). */
export function keyLabel(key: string | null | undefined): string {
  if (!key) return '';
  return /^D\d$/.test(key) ? key.slice(1) : key;
}
