"use client";
import { useEffect, useRef, useState } from "react";
import { searchOrgsAction, type OrgOption } from "./membership-actions";

interface Props {
  selected: OrgOption | null;
  onSelect: (org: OrgOption | null) => void;
}

/** Searchable dropdown to pick an organization from the database. */
export function OrgCombobox({ selected, onSelect }: Props) {
  const [query, setQuery] = useState("");
  const [results, setResults] = useState<OrgOption[]>([]);
  const [open, setOpen] = useState(false);
  const [loading, setLoading] = useState(false);
  const boxRef = useRef<HTMLDivElement>(null);

  // Debounced search — skip while an org is already selected.
  useEffect(() => {
    if (selected) return;
    const q = query.trim();
    if (q.length < 2) {
      setResults([]);
      return;
    }
    setLoading(true);
    const timer = setTimeout(async () => {
      const r = await searchOrgsAction(q);
      setResults(r);
      setOpen(true);
      setLoading(false);
    }, 300);
    return () => clearTimeout(timer);
  }, [query, selected]);

  // Close on outside click.
  useEffect(() => {
    function onDoc(e: MouseEvent) {
      if (boxRef.current && !boxRef.current.contains(e.target as Node)) setOpen(false);
    }
    document.addEventListener("mousedown", onDoc);
    return () => document.removeEventListener("mousedown", onDoc);
  }, []);

  function pick(org: OrgOption) {
    onSelect(org);
    setQuery(`${org.name} [${org.sid}]`);
    setResults([]);
    setOpen(false);
  }

  function clear() {
    onSelect(null);
    setQuery("");
    setResults([]);
  }

  return (
    <div ref={boxRef} className="relative flex flex-col gap-1">
      <span className="hud-label">ORGANISATION</span>
      <input
        type="text"
        value={query}
        onChange={(e) => {
          setQuery(e.target.value);
          if (selected) onSelect(null);
        }}
        onFocus={() => {
          if (results.length) setOpen(true);
        }}
        placeholder="Rechercher une organisation…"
        className="hud-clip border border-hud-cyan-dim bg-hud-bg/60 px-3 py-2 font-mono text-sm text-hud-text placeholder:text-hud-text-dim/60 focus:border-hud-cyan focus:outline-none"
      />
      {selected && (
        <button
          type="button"
          onClick={clear}
          className="self-start font-mono text-[10px] uppercase tracking-wide text-hud-text-dim hover:text-hud-red"
        >
          ✕ effacer
        </button>
      )}
      {open && !selected && (
        <ul className="absolute top-full z-20 mt-1 max-h-56 w-full overflow-auto border border-hud-cyan-dim bg-hud-bg font-mono text-sm shadow-hud-glow">
          {loading && <li className="px-3 py-2 text-hud-text-dim">recherche…</li>}
          {!loading && results.length === 0 && (
            <li className="px-3 py-2 text-hud-text-dim">Aucun résultat</li>
          )}
          {results.map((o) => (
            <li key={o.sid}>
              <button
                type="button"
                onClick={() => pick(o)}
                className="flex w-full items-center justify-between gap-3 px-3 py-2 text-left hover:bg-hud-cyan/10"
              >
                <span className="truncate text-hud-text">{o.name}</span>
                <span className="shrink-0 text-hud-text-dim">[{o.sid}]</span>
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
