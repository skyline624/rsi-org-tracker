"use client";
import { useRouter, useSearchParams } from "next/navigation";
import { cn } from "@/lib/utils/cn";
import { withPage } from "@/lib/utils/page-param";

interface PaginationProps {
  page: number;
  totalPages: number;
  total: number;
  /** Search param holding the page, for pages with several paginated lists. */
  param?: string;
}

export function Pagination({ page, totalPages, total, param = "page" }: PaginationProps) {
  const router = useRouter();
  const params = useSearchParams();

  const goTo = (p: number) => {
    if (p < 1 || p > totalPages) return;
    // A secondary list (own param) keeps the reader where it is on the page.
    router.push(`?${withPage(params.toString(), param, p)}`, { scroll: param === "page" });
  };

  return (
    <div className="mt-6 flex items-center justify-between font-mono text-xs">
      <div className="text-hud-text-dim">
        PAGE {page.toLocaleString()} / {totalPages.toLocaleString()} ·{" "}
        {total.toLocaleString()} RESULTS
      </div>
      <div className="flex gap-1">
        <button
          disabled={page <= 1}
          onClick={() => goTo(1)}
          className={cn(
            "border border-hud-cyan-dim px-2 py-1 text-hud-text-dim transition-colors",
            "hover:border-hud-cyan hover:text-hud-cyan disabled:cursor-not-allowed disabled:opacity-30",
          )}
        >
          « FIRST
        </button>
        <button
          disabled={page <= 1}
          onClick={() => goTo(page - 1)}
          className={cn(
            "border border-hud-cyan-dim px-2 py-1 text-hud-text-dim transition-colors",
            "hover:border-hud-cyan hover:text-hud-cyan disabled:cursor-not-allowed disabled:opacity-30",
          )}
        >
          ‹ PREV
        </button>
        <button
          disabled={page >= totalPages}
          onClick={() => goTo(page + 1)}
          className={cn(
            "border border-hud-cyan-dim px-2 py-1 text-hud-text-dim transition-colors",
            "hover:border-hud-cyan hover:text-hud-cyan disabled:cursor-not-allowed disabled:opacity-30",
          )}
        >
          NEXT ›
        </button>
        <button
          disabled={page >= totalPages}
          onClick={() => goTo(totalPages)}
          className={cn(
            "border border-hud-cyan-dim px-2 py-1 text-hud-text-dim transition-colors",
            "hover:border-hud-cyan hover:text-hud-cyan disabled:cursor-not-allowed disabled:opacity-30",
          )}
        >
          LAST »
        </button>
      </div>
    </div>
  );
}
