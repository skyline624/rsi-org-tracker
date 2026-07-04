"use client";
import { useState, type FormEvent } from "react";
import { useRouter } from "next/navigation";
import { toast } from "sonner";
import { HudPanel } from "@/components/hud/HudPanel";
import { HudButton } from "@/components/hud/HudButton";
import { HudInput } from "@/components/hud/HudInput";
import { createEntityAction } from "@/app/(user)/admin/actions";

export function QuickAddUser() {
  const [open, setOpen] = useState(false);
  const [loading, setLoading] = useState(false);
  const router = useRouter();

  async function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const el = e.currentTarget;
    const form = new FormData(el);
    const handle = String(form.get("handle") ?? "").trim();
    const citizenIdRaw = String(form.get("citizenId") ?? "").trim();
    if (!handle && !citizenIdRaw) {
      toast.error("Renseigne au moins un handle ou un citizen id.");
      return;
    }
    setLoading(true);
    const res = await createEntityAction({
      handle: handle || undefined,
      displayName: String(form.get("displayName") ?? "").trim() || undefined,
      citizenId: citizenIdRaw ? Number(citizenIdRaw) : undefined,
    });
    setLoading(false);
    if (res.ok) {
      toast.success("Utilisateur ajouté.");
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
          + AJOUTER UN UTILISATEUR
        </HudButton>
      </div>
    );
  }

  return (
    <HudPanel label="AJOUTER UN UTILISATEUR">
      <form onSubmit={submit} className="flex flex-col gap-4">
        <div className="grid gap-4 sm:grid-cols-3">
          <HudInput label="HANDLE" name="handle" type="text" maxLength={100} placeholder="ex. RedactedPilot" />
          <HudInput label="NOM AFFICHÉ" name="displayName" type="text" maxLength={500} />
          <HudInput label="CITIZEN ID (optionnel)" name="citizenId" type="number" min={1} />
        </div>
        <p className="font-mono text-[10px] uppercase tracking-wide text-hud-text-dim">
          Au moins un handle OU un citizen id. Pour un compte « redacted », laisse le citizen id vide.
        </p>
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
