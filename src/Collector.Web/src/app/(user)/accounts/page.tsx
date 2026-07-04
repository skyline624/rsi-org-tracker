import { redirect } from "next/navigation";
import { getSession } from "@/lib/auth/session";
import { apiGet } from "@/lib/api/client";
import { AccountsManager } from "./AccountsManager";
import type { AdminUserDto } from "./actions";

/** Admin-only account management: create, promote, ban, delete access accounts. */
export default async function AccountsPage() {
  const session = await getSession();
  if (!session) redirect("/login");
  if (!session.isAdmin) redirect("/dashboard");

  const users = await apiGet<{ items: AdminUserDto[] }>(
    "/api/admin/users",
    { pageSize: 200 },
    { bearerToken: session.accessToken },
  )
    .then((r) => r.items ?? [])
    .catch(() => [] as AdminUserDto[]);

  return (
    <div className="flex flex-col gap-6">
      <div>
        <div className="hud-label">— ADMIN::ACCOUNTS</div>
        <h1 className="mt-1 font-display text-2xl">Gestion des comptes</h1>
        <p className="mt-1 font-mono text-xs text-hud-text-dim">
          Créer, promouvoir, bannir ou supprimer les comptes d'accès au site.
        </p>
      </div>
      <AccountsManager initialUsers={users} currentUserId={session.userId} />
    </div>
  );
}
