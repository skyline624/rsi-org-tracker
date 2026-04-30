"use client";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { cn } from "@/lib/utils/cn";

const NAV_ITEMS = [
  { href: "/orgs", label: "ORGS" },
  { href: "/users", label: "USERS" },
  { href: "/stats", label: "STATS" },
  { href: "/changes", label: "CHANGELOG" },
];

export function TopNav({ authenticated }: { authenticated: boolean }) {
  const pathname = usePathname();

  return (
    <header className="sticky top-0 z-50 border-b border-hud-cyan/20 bg-hud-bg/80 backdrop-blur-sm">
      <div className="mx-auto flex h-14 max-w-[1440px] items-center gap-6 px-6">
        {/* Logo / brand */}
        <Link
          href="/"
          className="group flex items-center gap-2 font-display text-sm font-bold uppercase tracking-[0.2em]"
        >
          <span className="inline-block h-3 w-3 border border-hud-cyan bg-hud-cyan/20 group-hover:animate-glow-pulse" />
          <span className="text-hud-cyan">CITIZEN_INTEL</span>
          <span className="text-hud-text-dim">/ v0.1</span>
        </Link>

        {/* Primary nav */}
        <nav className="flex items-center gap-1 font-mono text-xs">
          {NAV_ITEMS.map((item) => {
            const active =
              pathname === item.href || pathname.startsWith(`${item.href}/`);
            return (
              <Link
                key={item.href}
                href={item.href}
                className={cn(
                  "relative px-3 py-1 uppercase tracking-[0.2em] transition-colors",
                  active
                    ? "text-hud-cyan"
                    : "text-hud-text-dim hover:text-hud-text",
                )}
              >
                {item.label}
                {active && (
                  <span className="absolute inset-x-1 -bottom-0.5 h-px bg-hud-cyan shadow-[0_0_8px_var(--hud-cyan)]" />
                )}
              </Link>
            );
          })}
        </nav>

        {/* Auth area */}
        <div className="ml-auto flex items-center gap-2">
          {authenticated ? (
            <>
              <Link
                href="/dashboard"
                className="font-mono text-xs uppercase tracking-[0.2em] text-hud-text-dim hover:text-hud-cyan"
              >
                DASHBOARD
              </Link>
              <form action="/api/auth/logout" method="post">
                <button
                  type="submit"
                  className="font-mono text-xs uppercase tracking-[0.2em] text-hud-orange hover:text-hud-red"
                >
                  DISCONNECT
                </button>
              </form>
            </>
          ) : (
            <>
              <Link
                href="/login"
                className="font-mono text-xs uppercase tracking-[0.2em] text-hud-text-dim hover:text-hud-cyan"
              >
                LOGIN
              </Link>
              <Link
                href="/register"
                className="font-mono text-xs uppercase tracking-[0.2em] text-hud-cyan hover:text-hud-orange"
              >
                ENLIST
              </Link>
            </>
          )}
        </div>
      </div>
    </header>
  );
}
