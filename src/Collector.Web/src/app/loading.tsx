/** Shown while a page's server data loads (the API can take a second on large orgs). */
export default function Loading() {
  return (
    <div className="flex min-h-[40vh] items-center justify-center font-mono text-xs uppercase tracking-[0.3em] text-hud-cyan/70">
      — acquiring signal —
    </div>
  );
}
