"use client";
import { useRouter } from "next/navigation";
import { type FormEvent, useState } from "react";
import { Search } from "lucide-react";

export function GlobalSearch() {
  const router = useRouter();
  const [q, setQ] = useState("");

  function handleSubmit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const term = q.trim();
    if (!term) return;
    // Heuristique : si ça ressemble à un SID d'org (ALLCAPS sans espace, 3-10 chars),
    // on tape /orgs sinon /users.
    const isOrgLike = /^[A-Z0-9]{3,16}$/.test(term);
    if (isOrgLike) {
      router.push(`/orgs?search=${encodeURIComponent(term)}`);
    } else {
      router.push(`/users?search=${encodeURIComponent(term)}`);
    }
  }

  return (
    <form onSubmit={handleSubmit} className="relative w-full">
      <Search
        aria-hidden
        className="pointer-events-none absolute left-3 top-1/2 h-3 w-3 -translate-y-1/2 text-hud-text-dim"
      />
      <input
        type="text"
        placeholder="SEARCH ORG OR HANDLE…"
        value={q}
        onChange={(e) => setQ(e.target.value)}
        className="hud-clip w-full border border-hud-cyan-dim bg-hud-bg/60 py-1.5 pl-9 pr-3 font-mono text-xs uppercase tracking-[0.15em] text-hud-text placeholder:text-hud-text-dim/60 focus:border-hud-cyan focus:shadow-hud-glow focus:outline-none"
      />
    </form>
  );
}
