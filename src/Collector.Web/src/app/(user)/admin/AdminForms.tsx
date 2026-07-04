"use client";
import { useState, type FormEvent } from "react";
import { toast } from "sonner";
import { HudPanel } from "@/components/hud/HudPanel";
import { HudButton } from "@/components/hud/HudButton";
import { HudInput } from "@/components/hud/HudInput";
import { createEntityAction, createOrganizationAction } from "./actions";

export function AdminForms() {
  const [entityLoading, setEntityLoading] = useState(false);
  const [orgLoading, setOrgLoading] = useState(false);

  async function submitEntity(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const el = e.currentTarget;
    const form = new FormData(el);
    const handle = String(form.get("handle") ?? "").trim();
    const citizenIdRaw = String(form.get("citizenId") ?? "").trim();
    if (!handle && !citizenIdRaw) {
      toast.error("Renseigne au moins un handle ou un citizen id.");
      return;
    }
    setEntityLoading(true);
    const res = await createEntityAction({
      handle: handle || undefined,
      displayName: String(form.get("displayName") ?? "").trim() || undefined,
      citizenId: citizenIdRaw ? Number(citizenIdRaw) : undefined,
    });
    setEntityLoading(false);
    if (res.ok) {
      toast.success("Utilisateur ajouté.");
      el.reset();
    } else {
      toast.error(res.error ?? "Échec.");
    }
  }

  async function submitOrg(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const el = e.currentTarget;
    const form = new FormData(el);
    setOrgLoading(true);
    const res = await createOrganizationAction({
      sid: String(form.get("sid") ?? "").trim(),
      name: String(form.get("name") ?? "").trim(),
      urlImage: String(form.get("urlImage") ?? "").trim() || undefined,
      archetype: String(form.get("archetype") ?? "").trim() || undefined,
      description: String(form.get("description") ?? "").trim() || undefined,
    });
    setOrgLoading(false);
    if (res.ok) {
      toast.success("Organisation ajoutée.");
      el.reset();
    } else {
      toast.error(res.error ?? "Échec.");
    }
  }

  return (
    <div className="grid gap-6 md:grid-cols-2">
      <HudPanel label="AJOUTER UN UTILISATEUR">
        <form onSubmit={submitEntity} className="flex flex-col gap-4">
          <HudInput label="HANDLE" name="handle" type="text" maxLength={100} placeholder="ex. RedactedPilot" />
          <HudInput label="NOM AFFICHÉ" name="displayName" type="text" maxLength={500} />
          <HudInput label="CITIZEN ID (optionnel)" name="citizenId" type="number" min={1} />
          <p className="font-mono text-[10px] uppercase tracking-wide text-hud-text-dim">
            Au moins un handle OU un citizen id. Pour un compte « redacted », laisse le citizen id vide.
          </p>
          <HudButton type="submit" disabled={entityLoading}>
            {entityLoading ? "AJOUT…" : "AJOUTER L'UTILISATEUR"}
          </HudButton>
        </form>
      </HudPanel>

      <HudPanel label="AJOUTER UNE ORGANISATION">
        <form onSubmit={submitOrg} className="flex flex-col gap-4">
          <HudInput label="SID" name="sid" type="text" required maxLength={50} placeholder="ex. PRIVATEORG" />
          <HudInput label="NOM" name="name" type="text" required maxLength={500} />
          <HudInput label="ARCHETYPE (optionnel)" name="archetype" type="text" maxLength={100} />
          <HudInput label="IMAGE URL (optionnel)" name="urlImage" type="url" maxLength={2000} />
          <HudInput label="DESCRIPTION (optionnel)" name="description" type="text" />
          <HudButton type="submit" disabled={orgLoading}>
            {orgLoading ? "AJOUT…" : "AJOUTER L'ORGANISATION"}
          </HudButton>
        </form>
      </HudPanel>
    </div>
  );
}
