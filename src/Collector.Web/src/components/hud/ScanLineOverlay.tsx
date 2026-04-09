/**
 * Overlay CSS scan-lines discret sur toute la page. Monté une seule fois
 * dans le root layout. Invisible si prefers-reduced-motion.
 */
export function ScanLineOverlay() {
  return (
    <div
      aria-hidden
      className="scan-lines-overlay pointer-events-none fixed inset-0 z-[60]"
      style={{
        backgroundImage:
          "repeating-linear-gradient(0deg, rgba(201,230,242,0.04) 0px, rgba(201,230,242,0.04) 1px, transparent 1px, transparent 3px)",
        mixBlendMode: "overlay",
      }}
    />
  );
}
