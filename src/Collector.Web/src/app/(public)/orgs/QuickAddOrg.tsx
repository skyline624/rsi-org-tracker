"use client";
import { useState, type FormEvent } from "react";
import { useRouter } from "next/navigation";
import { toast } from "sonner";
import { HudPanel } from "@/components/hud/HudPanel";
import { HudButton } from "@/components/hud/HudButton";
import { HudInput } from "@/components/hud/HudInput";
import { createOrganizationAction } from "@/app/(user)/admin/actions";

export function QuickAddOrg() {
  const [open, setOpen] = useState(false);
  const [loading, setLoading] = useState(false);
  const router = useRouter();

  async function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const el = e.currentTarget;
    const form = new FormData(el);
    setLoading(true);
    const res = await createOrganizationAction({
      sid: String(form.get("sid") ?? "").trim(),
      name: String(form.get("name") ?? "").trim(),
      urlImage: String(form.get("urlImage") ?? "").trim() || undefined,
      archetype: String(form.get("archetype") ?? "").trim() || undefined,
      description: String(form.get("description") ?? "").trim() || undefined,
    });
    setLoading(false);
    if (res.ok) {
      toast.success("Organisation ajoutée.");
      el.reset();
      setOpen(false);
      router.refresh();
    } else {
      toast.error(res.error ?? "Échec.");
    }
  }

  if (!open) {
    return (
      <div className="flex justify-end">
        <HudButton type="button" onClick={() => setOpen(true)}>
          + AJOUTER UNE ORGANISATION
        </HudButton>
      </div>
    );
  }

  return (
    <HudPanel label="AJOUTER UNE ORGANISATION">
      <form onSubmit={submit} className="flex flex-col gap-4">
        <div className="grid gap-4 sm:grid-cols-2">
          <HudInput label="SID" name="sid" type="text" required maxLength={50} placeholder="ex. PRIVATEORG" />
          <HudInput label="NOM" name="name" type="text" required maxLength={500} />
          <HudInput label="ARCHETYPE (optionnel)" name="archetype" type="text" maxLength={100} />
          <HudInput label="IMAGE URL (optionnel)" name="urlImage" type="url" maxLength={2000} />
        </div>
        <HudInput label="DESCRIPTION (optionnel)" name="description" type="text" />
        <div className="flex justify-end gap-2">
          <HudButton type="button" variant="ghost" onClick={() => setOpen(false)}>
            ANNULER
          </HudButton>
          <HudButton type="submit" disabled={loading}>
            {loading ? "AJOUT…" : "AJOUTER"}
          </HudButton>
        </div>
      </form>
    </HudPanel>
  );
}
