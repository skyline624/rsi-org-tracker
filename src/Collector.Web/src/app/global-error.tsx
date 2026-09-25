"use client";

import "./globals.css";

/** The root layout itself failed: this replaces it, so it brings its own html and body. */
export default function GlobalError({
  error,
  reset,
}: {
  error: Error & { digest?: string };
  reset: () => void;
}) {
  return (
    <html lang="en" className="dark">
      <body className="min-h-screen antialiased">
        <main className="mx-auto max-w-[1440px] px-6 py-8 font-mono text-sm">
          <h1 className="text-xl uppercase tracking-wider text-hud-red">Citizen Intel is unavailable</h1>
          <p className="mt-2 text-hud-text-dim">The application could not start this page.</p>
          {error.digest && <p className="mt-2 text-xs text-hud-text-dim">REF {error.digest}</p>}
          <button
            onClick={reset}
            className="mt-4 border border-hud-cyan px-3 py-1.5 text-xs uppercase text-hud-cyan"
          >
            Retry
          </button>
        </main>
      </body>
    </html>
  );
}
