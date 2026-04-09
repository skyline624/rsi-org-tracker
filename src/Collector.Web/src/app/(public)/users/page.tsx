import { HudPanel } from "@/components/hud/HudPanel";
import { UsersTable } from "@/components/user/UsersTable";
import { Pagination } from "@/components/layout/Pagination";
import { listUsers } from "@/lib/api/endpoints";
import { formatNumber } from "@/lib/utils/format";

export const dynamic = "force-dynamic";

interface PageProps {
  searchParams: Promise<{ search?: string; page?: string }>;
}

export default async function UsersPage({ searchParams }: PageProps) {
  const sp = await searchParams;
  const page = Number(sp.page ?? "1") || 1;
  const pageSize = 30;

  const data = await listUsers(
    { search: sp.search, page, pageSize },
    { serverSide: true },
  );

  return (
    <div className="flex flex-col gap-6">
      <header className="flex items-center justify-between">
        <div>
          <div className="hud-label">— UEE::CITIZEN_REGISTRY</div>
          <h1 className="mt-1 font-display text-3xl">Citizens</h1>
        </div>
        <form className="flex items-center gap-2" action="/users">
          <input
            name="search"
            defaultValue={sp.search}
            placeholder="SEARCH HANDLE OR NAME"
            className="hud-clip w-64 border border-hud-cyan-dim bg-hud-bg/60 px-3 py-1.5 font-mono text-xs uppercase tracking-[0.15em] text-hud-text placeholder:text-hud-text-dim/60 focus:border-hud-cyan focus:outline-none focus:shadow-hud-glow"
          />
          <button className="hud-clip border border-hud-cyan px-3 py-1.5 font-mono text-xs uppercase tracking-[0.15em] text-hud-cyan hover:bg-hud-cyan/10 hover:shadow-hud-glow">
            SCAN
          </button>
        </form>
      </header>

      <HudPanel label={`${formatNumber(data.total)} CITIZENS INDEXED`}>
        <UsersTable rows={data.items} />
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
