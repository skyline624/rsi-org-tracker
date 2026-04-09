import { HudPanel } from "@/components/hud/HudPanel";
import { OrgsTable } from "@/components/org/OrgsTable";
import { Pagination } from "@/components/layout/Pagination";
import { listOrgs } from "@/lib/api/endpoints";
import { formatNumber } from "@/lib/utils/format";

export const dynamic = "force-dynamic";

interface PageProps {
  searchParams: Promise<{
    search?: string;
    archetype?: string;
    lang?: string;
    recruiting?: string;
    page?: string;
  }>;
}

export default async function OrgsPage({ searchParams }: PageProps) {
  const sp = await searchParams;
  const page = Number(sp.page ?? "1") || 1;
  const pageSize = 30;

  const data = await listOrgs(
    {
      search: sp.search,
      archetype: sp.archetype,
      lang: sp.lang,
      recruiting:
        sp.recruiting === "true"
          ? true
          : sp.recruiting === "false"
            ? false
            : undefined,
      page,
      pageSize,
    },
    { serverSide: true },
  );

  return (
    <div className="flex flex-col gap-6">
      <header className="flex items-center justify-between">
        <div>
          <div className="hud-label">— UEE::ORG_CATALOG</div>
          <h1 className="mt-1 font-display text-3xl">Organizations</h1>
        </div>
        <form className="flex items-center gap-2" action="/orgs">
          <input
            name="search"
            defaultValue={sp.search}
            placeholder="SEARCH SID OR NAME"
            className="hud-clip w-56 border border-hud-cyan-dim bg-hud-bg/60 px-3 py-1.5 font-mono text-xs uppercase tracking-[0.15em] text-hud-text placeholder:text-hud-text-dim/60 focus:border-hud-cyan focus:outline-none focus:shadow-hud-glow"
          />
          <select
            name="lang"
            defaultValue={sp.lang ?? ""}
            className="hud-clip border border-hud-cyan-dim bg-hud-bg/60 px-2 py-1.5 font-mono text-xs uppercase text-hud-text focus:border-hud-cyan focus:outline-none"
          >
            <option value="">ANY LANG</option>
            <option value="English">EN</option>
            <option value="French">FR</option>
            <option value="German">DE</option>
            <option value="Spanish">ES</option>
            <option value="Russian">RU</option>
          </select>
          <select
            name="recruiting"
            defaultValue={sp.recruiting ?? ""}
            className="hud-clip border border-hud-cyan-dim bg-hud-bg/60 px-2 py-1.5 font-mono text-xs uppercase text-hud-text focus:border-hud-cyan focus:outline-none"
          >
            <option value="">ANY STATUS</option>
            <option value="true">RECRUITING</option>
            <option value="false">CLOSED</option>
          </select>
          <button className="hud-clip border border-hud-cyan px-3 py-1.5 font-mono text-xs uppercase tracking-[0.15em] text-hud-cyan hover:bg-hud-cyan/10 hover:shadow-hud-glow">
            SCAN
          </button>
        </form>
      </header>

      <HudPanel label={`${formatNumber(data.total)} ORGS INDEXED`}>
        <OrgsTable rows={data.items} />
        <Pagination
          page={data.page}
          totalPages={data.totalPages}
          total={data.total}
        />
        <p className="mt-2 font-mono text-[10px] uppercase tracking-wider text-hud-text-dim">
          — click any column header to sort the current page —
        </p>
      </HudPanel>
    </div>
  );
}
