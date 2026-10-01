import Link from "next/link";
import { HudPanel } from "@/components/hud/HudPanel";
import { Pagination } from "@/components/layout/Pagination";
import { getDiscordMulti } from "@/lib/api/endpoints";
import { requireAuthCtx, withAuthRedirect } from "@/lib/auth/server-api";
import { firstParam, type SearchParams } from "@/lib/discord/params";
import { formatNumber } from "@/lib/utils/format";
import { parsePage } from "@/lib/utils/page-param";
import { MultiMembersTable } from "./MultiMembersTable";

export const dynamic = "force-dynamic";

/** Accounts per page, paged by the API. */
const PAGE_SIZE = 50;

/** Discord accounts present on several tracked servers, with the orgs on both sides. */
export default async function DiscordMultiPage({ searchParams }: { searchParams: Promise<SearchParams> }) {
  const sp = await searchParams;
  const page = parsePage(firstParam(sp.page));
  const ctx = await requireAuthCtx();
  const data = await withAuthRedirect(getDiscordMulti(ctx, { page, pageSize: PAGE_SIZE }));

  return (
    <div className="flex flex-col gap-6">
      <header className="flex flex-wrap items-end justify-between gap-4">
        <div>
          <div className="hud-label">— UEE::DISCORD_MULTI</div>
          <h1 className="mt-1 font-display text-3xl">Multi-appartenance</h1>
          <p className="mt-1 max-w-3xl font-mono text-xs text-hud-text-dim">
            {"Comptes Discord présents sur au moins deux serveurs suivis (hors bots), avec la corpo de chaque serveur et, pour les comptes liés, les orgs RSI actives des citoyens."}
          </p>
        </div>
        <Link
          href="/discord"
          className="hud-clip border border-hud-cyan px-3 py-1.5 font-mono text-xs uppercase tracking-[0.15em] text-hud-cyan hover:bg-hud-cyan/10"
        >
          ← SERVEURS
        </Link>
      </header>

      <HudPanel label={`${formatNumber(data.total)} COMPTES`}>
        <MultiMembersTable rows={data.items} />
        {data.totalPages > 1 && (
          <Pagination param="page" page={data.page} totalPages={data.totalPages} total={data.total} />
        )}
      </HudPanel>
    </div>
  );
}
