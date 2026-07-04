"use client";
import { useState, type FormEvent } from "react";
import { toast } from "sonner";
import { HudPanel } from "@/components/hud/HudPanel";
import { HudButton } from "@/components/hud/HudButton";
import { HudInput } from "@/components/hud/HudInput";
import { setCitizenIdAction } from "./entity-actions";

interface Props {
  handle: string;
  initialCitizenId: number | null;
}

export function CitizenIdPanel({ handle, initialCitizenId }: Props) {
  const [citizenId, setCitizenId] = useState<number | null>(initialCitizenId);
  const [busy, setBusy] = useState(false);
  const [editing, setEditing] = useState(initialCitizenId === null);

  async function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const val = Number(String(new FormData(e.currentTarget).get("citizenId") ?? "").trim());
    if (!Number.isInteger(val) || val < 1) {
      toast.error("Entre un citizen id valide (nombre positif).");
      return;
    }
    setBusy(true);
    const res = await setCitizenIdAction(handle, val);
    setBusy(false);
    if (res.ok && res.entity) {
      setCitizenId(res.entity.citizenId);
      setEditing(false);
      toast.success("Citizen id enregistré.");
    } else {
      toast.error(res.error ?? "Échec.");
    }
  }

  return (
    <HudPanel label="CITIZEN ID">
      {citizenId !== null && !editing ? (
        <div className="flex items-center justify-between font-mono text-sm">
          <span className="text-hud-cyan">#{citizenId}</span>
          <button
            type="button"
            className="font-mono text-[10px] uppercase tracking-wide text-hud-text-dim hover:text-hud-cyan"
            onClick={() => setEditing(true)}
          >
            modifier
          </button>
        </div>
      ) : (
        <form onSubmit={submit} className="flex flex-wrap items-end gap-3">
          <HudInput
            label="CITIZEN ID"
            name="citizenId"
            type="number"
            min={1}
            defaultValue={citizenId ?? undefined}
            required
          />
          <HudButton type="submit" disabled={busy}>
            {busy ? "…" : "ENREGISTRER"}
          </HudButton>
          {citizenId !== null && (
            <HudButton type="button" variant="ghost" onClick={() => setEditing(false)}>
              ANNULER
            </HudButton>
          )}
          <p className="w-full font-mono text-[10px] uppercase tracking-wide text-hud-text-dim">
            Ce membre n'a pas de citizen id public. Renseigne-le si tu le connais (Discord, recoupement…).
          </p>
        </form>
      )}
    </HudPanel>
  );
}
