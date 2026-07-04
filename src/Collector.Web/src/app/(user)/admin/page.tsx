import { redirect } from "next/navigation";
import { getSession } from "@/lib/auth/session";
import { AdminForms } from "./AdminForms";

/**
 * Admin-only page for manually curating tracker data: add a redacted person or a
 * private/undiscovered organization. The middleware already blocks anonymous
 * access; here we additionally require the admin role (the API re-checks too).
 */
export default async function AdminPage() {
  const session = await getSession();
  if (!session) redirect("/login");

  return (
    <div className="flex flex-col gap-6">
      <div>
        <div className="hud-label">— MANUAL_ENTRY</div>
        <h1 className="mt-1 font-display text-2xl">Ajout manuel</h1>
        <p className="mt-1 max-w-2xl font-mono text-xs text-hud-text-dim">
          Enregistrer manuellement un citoyen (compte « redacted » sans citizen number)
          ou une organisation absente de la collecte (privée / non encore découverte).
        </p>
      </div>
      <AdminForms />
    </div>
  );
}
