import { redirect } from "next/navigation";
import type { ReactNode } from "react";
import Link from "next/link";
import { getSession } from "@/lib/auth/session";
import { cn } from "@/lib/utils/cn";

/**
 * Layout des pages authentifiées. Double garde : middleware.ts bloque déjà
 * l'accès sans cookie, mais on recheck ici pour avoir la session exacte.
 */
export default async function UserLayout({ children }: { children: ReactNode }) {
  const session = await getSession();
  if (!session) redirect("/login");

  return (
    <div className="flex flex-col gap-6">
      <header className="flex items-center justify-between border-b border-hud-cyan/20 pb-4">
        <div>
          <div className="hud-label">— CITIZEN::{session.username.toUpperCase()}</div>
          <h1 className="mt-1 font-display text-2xl">Private Terminal</h1>
        </div>
        <nav className="flex items-center gap-1 font-mono text-xs">
          {[
            { href: "/dashboard", label: "DASHBOARD" },
            { href: "/favorites", label: "FAVORITES" },
            { href: "/settings", label: "SETTINGS" },
          ].map((it) => (
            <Link
              key={it.href}
              href={it.href}
              className={cn(
                "border border-hud-cyan-dim px-3 py-1.5 uppercase tracking-[0.15em] text-hud-text-dim",
                "hover:border-hud-cyan hover:text-hud-cyan",
              )}
            >
              {it.label}
            </Link>
          ))}
        </nav>
      </header>
      {children}
    </div>
  );
}
