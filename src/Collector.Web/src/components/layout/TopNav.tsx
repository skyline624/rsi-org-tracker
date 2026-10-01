"use client";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { cn } from "@/lib/utils/cn";

const NAV_ITEMS = [
  { href: "/orgs", label: "ORGS" },
  { href: "/users", label: "USERS" },
  { href: "/discord", label: "DISCORD" },
  { href: "/stats", label: "STATS" },
  { href: "/changes", label: "CHANGELOG" },
];

export function TopNav({ authenticated }: { authenticated: boolean }) {
  const pathname = usePathname();

  return (
    <header className="sticky top-0 z-50 border-b border-hud-cyan/20 bg-hud-bg backdrop-blur-sm">
      <div className="mx-auto flex max-w-[1440px] flex-wrap items-center gap-x-4 gap-y-2 px-4 py-3 lg:h-14 lg:flex-nowrap lg:gap-6 lg:px-6 lg:py-0">
        {/* Logo / brand */}
        <Link
          href="/"
          className="group order-1 flex shrink-0 items-center gap-2 font-display text-xs font-bold uppercase tracking-[0.1em] sm:text-sm sm:tracking-[0.2em] lg:order-none"
        >
          <span className="inline-block h-3 w-3 border border-hud-cyan bg-hud-cyan/20 group-hover:animate-glow-pulse" />
          <span className="text-hud-cyan">CITIZEN_INTEL</span>
          <span className="hidden text-hud-text-dim sm:inline">/ v0.1</span>
        </Link>

        {/* Primary nav */}
        <nav className="order-3 flex w-full min-w-0 items-center gap-1 overflow-x-auto font-mono text-xs lg:order-none lg:w-auto">
          {NAV_ITEMS.map((item) => {
            const active =
              pathname === item.href || pathname.startsWith(`${item.href}/`);
            return (
              <Link
                key={item.href}
                href={item.href}
                className={cn(
                  "relative shrink-0 px-3 py-1 uppercase tracking-[0.2em] transition-colors",
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
        <div className="order-2 ml-auto flex shrink-0 items-center gap-2 lg:order-none">
          {authenticated ? (
            <>
              <Link
                href="/dashboard"
                className="font-mono text-[10px] uppercase tracking-[0.1em] text-hud-text-dim hover:text-hud-cyan sm:text-xs sm:tracking-[0.2em]"
              >
                DASHBOARD
              </Link>
              <form action="/api/auth/logout" method="post">
                <button
                  type="submit"
                  className="font-mono text-[10px] uppercase tracking-[0.1em] text-hud-orange hover:text-hud-red sm:text-xs sm:tracking-[0.2em]"
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
            </>
          )}
        </div>
      </div>
    </header>
  );
}
