"use client";
import { useState, type FormEvent } from "react";
import { useRouter } from "next/navigation";
import { toast } from "sonner";
import { HudPanel } from "@/components/hud/HudPanel";
import { HudButton } from "@/components/hud/HudButton";
import { HudInput } from "@/components/hud/HudInput";
import { createMembershipAction } from "@/app/(public)/users/[handle]/membership-actions";

/** Adds a member directly attached to the organization whose SID is given. */
export function QuickAddMember({ sid }: { sid: string }) {
  const [open, setOpen] = useState(false);
  const [loading, setLoading] = useState(false);
  const router = useRouter();

  async function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const el = e.currentTarget;
    const form = new FormData(el);
    const handle = String(form.get("handle") ?? "").trim();
    if (!handle) {
      toast.error("Handle requis.");
      return;
    }
    setLoading(true);
    const res = await createMembershipAction(handle, {
      orgSid: sid,
      rank: String(form.get("rank") ?? "").trim() || undefined,
      via: String(form.get("via") ?? "").trim() || undefined,
    });
    setLoading(false);
    if (res.ok) {
      toast.success("Membre rattaché à l'organisation.");
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
          + AJOUTER UN MEMBRE
        </HudButton>
      </div>
    );
  }

  return (
    <HudPanel label={`AJOUTER UN MEMBRE À ${sid}`}>
      <form onSubmit={submit} className="flex flex-col gap-4">
        <div className="grid gap-4 sm:grid-cols-3">
          <HudInput label="HANDLE" name="handle" type="text" required maxLength={100} placeholder="ex. RedactedPilot" />
          <HudInput label="GRADE (optionnel)" name="rank" type="text" maxLength={100} />
          <label className="flex flex-col gap-1 font-mono text-[10px] uppercase tracking-wide text-hud-text-dim">
            Rattachement
            <select
              name="via"
              className="hud-clip border border-hud-cyan-dim bg-hud-bg/60 px-3 py-2 font-mono text-sm text-hud-text focus:border-hud-cyan focus:outline-none"
            >
              <option value="">— non précisé —</option>
              <option value="rsi">RSI</option>
              <option value="discord">Discord</option>
            </select>
          </label>
        </div>
        <p className="font-mono text-[10px] uppercase tracking-wide text-hud-text-dim">
          Le membre est créé s'il n'existe pas, puis rattaché à {sid}. Date par défaut : aujourd'hui.
        </p>
        <div className="flex justify-end gap-2">
          <HudButton type="button" variant="ghost" onClick={() => setOpen(false)}>
            ANNULER
          </HudButton>
          <HudButton type="submit" disabled={loading}>
            {loading ? "AJOUT…" : "RATTACHER"}
          </HudButton>
        </div>
      </form>
    </HudPanel>
  );
}
