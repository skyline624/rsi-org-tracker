"use client";
import { useEffect, useMemo, useState, type ReactNode } from "react";
import {
  ChevronDown,
  ChevronsUpDown,
  ChevronUp,
  ChevronLeft,
  ChevronRight,
  ChevronsLeft,
  ChevronsRight,
} from "lucide-react";
import { cn } from "@/lib/utils/cn";

export interface HudColumn<T> {
  key: string;
  header: string;
  width?: string; // ex: "w-24", "flex-1"
  align?: "left" | "right" | "center";
  render: (row: T) => ReactNode;
  /**
   * Enable click-to-sort on this column's header. Default: false.
   */
  sortable?: boolean;
  /**
   * Value extractor used for comparisons. If omitted while `sortable` is true,
   * we fall back to `JSON.stringify(row[key])` which is rarely what you want —
   * always supply this for typed sort.
   */
  sortValue?: (row: T) => string | number | Date | null | undefined;
}

interface HudDataGridProps<T> {
  columns: HudColumn<T>[];
  rows: T[];
  rowKey: (row: T, i: number) => string;
  onRowClick?: (row: T) => void;
  empty?: ReactNode;
  /**
   * Default sort applied on mount, if any. e.g. { key: "members", dir: "desc" }.
   */
  defaultSort?: { key: string; dir: "asc" | "desc" };
  /**
   * Enable client-side pagination footer. Recommended for any full-dataset
   * render (org detail roster, etc.). Leave off for list pages that already
   * paginate server-side via URL params.
   */
  paginated?: boolean;
  /**
   * Page size choices shown in the "per page" selector. Defaults to
   * [10, 25, 50, 100]. Pass a number 0 to let users display "ALL" rows at once.
   */
  pageSizeOptions?: number[];
  /**
   * Initial page size. Defaults to the first value in `pageSizeOptions`.
   */
  defaultPageSize?: number;
}

type SortState = { key: string; dir: "asc" | "desc" } | null;

function compareValues(
  a: string | number | Date | null | undefined,
  b: string | number | Date | null | undefined,
): number {
  // Nullish values always sink to the bottom regardless of direction.
  const aNull = a == null || (typeof a === "number" && Number.isNaN(a));
  const bNull = b == null || (typeof b === "number" && Number.isNaN(b));
  if (aNull && bNull) return 0;
  if (aNull) return 1;
  if (bNull) return -1;

  if (typeof a === "number" && typeof b === "number") return a - b;
  if (a instanceof Date && b instanceof Date) return a.getTime() - b.getTime();
  return String(a).localeCompare(String(b), undefined, {
    numeric: true,
    sensitivity: "base",
  });
}

/**
 * Tableau HUD mono type terminal avec tri client-side sur les colonnes marquées
 * `sortable: true`. Cycle de clic : off → asc → desc → off.
 *
 * Note v1 : le tri est local à la page courante (pas de re-fetch serveur).
 * Sur les pages paginées (/orgs, /users) cela ne trie que les N résultats
 * actuellement chargés — l'ordre global reste celui imposé par l'API.
 */
export function HudDataGrid<T>({
  columns,
  rows,
  rowKey,
  onRowClick,
  empty,
  defaultSort = undefined,
  paginated = false,
  pageSizeOptions = [10, 25, 50, 100],
  defaultPageSize,
}: HudDataGridProps<T>) {
  const [sort, setSort] = useState<SortState>(defaultSort ?? null);
  const initialSize = defaultPageSize ?? pageSizeOptions[0] ?? 25;
  const [pageSize, setPageSize] = useState<number>(initialSize);
  const [page, setPage] = useState(1);

  const sortedRows = useMemo(() => {
    if (!sort) return rows;
    const col = columns.find((c) => c.key === sort.key);
    if (!col || !col.sortable || !col.sortValue) return rows;
    const dir = sort.dir === "asc" ? 1 : -1;
    // Copy before sort to avoid mutating the parent array.
    return [...rows].sort(
      (a, b) => dir * compareValues(col.sortValue!(a), col.sortValue!(b)),
    );
  }, [rows, columns, sort]);

  // Pagination math (client-side). `pageSize === 0` means "show all".
  const totalPages = paginated && pageSize > 0
    ? Math.max(1, Math.ceil(sortedRows.length / pageSize))
    : 1;

  // Clamp the current page whenever the total shrinks (sort change, filter, etc.)
  useEffect(() => {
    if (page > totalPages) setPage(totalPages);
  }, [page, totalPages]);

  const visibleRows = useMemo(() => {
    if (!paginated || pageSize === 0) return sortedRows;
    const start = (page - 1) * pageSize;
    return sortedRows.slice(start, start + pageSize);
  }, [sortedRows, paginated, pageSize, page]);

  function toggleSort(key: string, sortable: boolean | undefined) {
    if (!sortable) return;
    setSort((prev) => {
      if (!prev || prev.key !== key) return { key, dir: "asc" };
      if (prev.dir === "asc") return { key, dir: "desc" };
      return null; // troisième clic : retour à l'ordre natif
    });
    // Resetting to page 1 keeps the mental model simple: "I sorted → I'm at
    // the top of the new order".
    setPage(1);
  }

  return (
    <div className="w-full overflow-x-auto">
      <div className="min-w-full font-mono text-xs">
        {/* header */}
        <div className="flex border-b border-hud-cyan/30 pb-2 text-[10px] uppercase tracking-[0.15em] text-hud-text-dim">
          {columns.map((c) => {
            const isSorted = sort?.key === c.key;
            const arrow = !c.sortable ? null : isSorted && sort?.dir === "asc" ? (
              <ChevronUp className="h-3 w-3 text-hud-cyan" aria-hidden />
            ) : isSorted && sort?.dir === "desc" ? (
              <ChevronDown className="h-3 w-3 text-hud-cyan" aria-hidden />
            ) : (
              <ChevronsUpDown
                className="h-3 w-3 text-hud-text-dim/40"
                aria-hidden
              />
            );

            return (
              <div
                key={c.key}
                className={cn(
                  "px-3",
                  c.width ?? "flex-1",
                  c.align === "right" && "text-right",
                  c.align === "center" && "text-center",
                  c.sortable &&
                    "cursor-pointer select-none transition-colors hover:text-hud-cyan",
                  isSorted && "text-hud-cyan",
                )}
                onClick={() => toggleSort(c.key, c.sortable)}
                role={c.sortable ? "button" : undefined}
                aria-sort={
                  !c.sortable || !isSorted
                    ? undefined
                    : sort!.dir === "asc"
                      ? "ascending"
                      : "descending"
                }
                tabIndex={c.sortable ? 0 : undefined}
                onKeyDown={(e) => {
                  if (!c.sortable) return;
                  if (e.key === "Enter" || e.key === " ") {
                    e.preventDefault();
                    toggleSort(c.key, c.sortable);
                  }
                }}
              >
                <span
                  className={cn(
                    "inline-flex items-center gap-1",
                    c.align === "right" && "justify-end w-full",
                    c.align === "center" && "justify-center w-full",
                  )}
                >
                  {c.header}
                  {arrow}
                </span>
              </div>
            );
          })}
        </div>

        {/* rows */}
        {visibleRows.length === 0 ? (
          <div className="py-8 text-center text-hud-text-dim">
            {empty ?? "— no data —"}
          </div>
        ) : (
          visibleRows.map((row, i) => (
            <div
              key={rowKey(row, i)}
              onClick={onRowClick ? () => onRowClick(row) : undefined}
              className={cn(
                "flex items-center border-b border-hud-cyan/10 py-2 transition-colors",
                onRowClick && "cursor-pointer hover:bg-hud-cyan/5 hover:text-hud-cyan",
              )}
            >
              {columns.map((c) => (
                <div
                  key={c.key}
                  className={cn(
                    "px-3",
                    c.width ?? "flex-1",
                    c.align === "right" && "text-right tabular-nums",
                    c.align === "center" && "text-center",
                  )}
                >
                  {c.render(row)}
                </div>
              ))}
            </div>
          ))
        )}
      </div>

      {/* ─── Pagination footer (client-side) ─────────────────────── */}
      {paginated && sortedRows.length > 0 && (
        <GridPaginator
          page={page}
          totalPages={totalPages}
          pageSize={pageSize}
          totalRows={sortedRows.length}
          pageSizeOptions={pageSizeOptions}
          onPageChange={setPage}
          onPageSizeChange={(n) => {
            setPageSize(n);
            setPage(1);
          }}
        />
      )}
    </div>
  );
}

/**
 * Paginator bar: left "per page" selector · center page buttons · right count.
 * Kept local to the grid so callers don't have to wire anything.
 */
interface GridPaginatorProps {
  page: number;
  totalPages: number;
  pageSize: number;
  totalRows: number;
  pageSizeOptions: number[];
  onPageChange: (p: number) => void;
  onPageSizeChange: (n: number) => void;
}

function GridPaginator({
  page,
  totalPages,
  pageSize,
  totalRows,
  pageSizeOptions,
  onPageChange,
  onPageSizeChange,
}: GridPaginatorProps) {
  // Compact page list: ex [1, …, 4, 5, 6, …, 12] — keeps footer width bounded.
  const pages = useMemo(() => {
    const delta = 1; // how many pages to show on each side of current
    const range: (number | "…")[] = [];
    const left = Math.max(2, page - delta);
    const right = Math.min(totalPages - 1, page + delta);

    range.push(1);
    if (left > 2) range.push("…");
    for (let i = left; i <= right; i++) range.push(i);
    if (right < totalPages - 1) range.push("…");
    if (totalPages > 1) range.push(totalPages);
    return range;
  }, [page, totalPages]);

  const from = pageSize === 0 ? 1 : (page - 1) * pageSize + 1;
  const to =
    pageSize === 0 ? totalRows : Math.min(page * pageSize, totalRows);

  const btn =
    "inline-flex h-7 min-w-[28px] items-center justify-center border border-hud-cyan-dim px-2 font-mono text-[11px] uppercase tracking-[0.1em] text-hud-text-dim transition-colors hover:border-hud-cyan hover:text-hud-cyan disabled:cursor-not-allowed disabled:opacity-30";

  return (
    <div className="mt-4 grid grid-cols-1 items-center gap-3 border-t border-hud-cyan/20 pt-3 font-mono text-[11px] md:grid-cols-3">
      {/* Left: page size selector */}
      <div className="flex items-center gap-2 text-hud-text-dim">
        <span className="uppercase tracking-wider">ROWS/PAGE</span>
        <select
          value={pageSize}
          onChange={(e) => onPageSizeChange(Number(e.target.value))}
          className="hud-clip border border-hud-cyan-dim bg-hud-bg/60 px-2 py-0.5 font-mono text-[11px] text-hud-text focus:border-hud-cyan focus:outline-none"
        >
          {pageSizeOptions.map((n) => (
            <option key={n} value={n}>
              {n === 0 ? "ALL" : n}
            </option>
          ))}
        </select>
      </div>

      {/* Center: page navigation */}
      <div className="flex items-center justify-center gap-1">
        <button
          type="button"
          className={btn}
          disabled={page <= 1}
          onClick={() => onPageChange(1)}
          aria-label="First page"
        >
          <ChevronsLeft className="h-3 w-3" />
        </button>
        <button
          type="button"
          className={btn}
          disabled={page <= 1}
          onClick={() => onPageChange(page - 1)}
          aria-label="Previous page"
        >
          <ChevronLeft className="h-3 w-3" />
        </button>
        {pages.map((p, i) =>
          p === "…" ? (
            <span
              key={`dots-${i}`}
              className="px-1 text-hud-text-dim"
              aria-hidden
            >
              …
            </span>
          ) : (
            <button
              key={p}
              type="button"
              className={cn(
                btn,
                p === page && "border-hud-cyan text-hud-cyan shadow-hud-glow",
              )}
              onClick={() => onPageChange(p)}
              aria-current={p === page ? "page" : undefined}
              aria-label={`Page ${p}`}
            >
              {p}
            </button>
          ),
        )}
        <button
          type="button"
          className={btn}
          disabled={page >= totalPages}
          onClick={() => onPageChange(page + 1)}
          aria-label="Next page"
        >
          <ChevronRight className="h-3 w-3" />
        </button>
        <button
          type="button"
          className={btn}
          disabled={page >= totalPages}
          onClick={() => onPageChange(totalPages)}
          aria-label="Last page"
        >
          <ChevronsRight className="h-3 w-3" />
        </button>
      </div>

      {/* Right: row count summary */}
      <div className="text-right uppercase tracking-wider text-hud-text-dim">
        {from.toLocaleString()}–{to.toLocaleString()} / {totalRows.toLocaleString()}
      </div>
    </div>
  );
}
