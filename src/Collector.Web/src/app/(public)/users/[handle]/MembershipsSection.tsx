"use client";
import { useState, type FormEvent } from "react";
import { toast } from "sonner";
import { HudPanel } from "@/components/hud/HudPanel";
import { HudButton } from "@/components/hud/HudButton";
import { HudInput } from "@/components/hud/HudInput";
import { formatDate } from "@/lib/utils/format";
import { OrgCombobox } from "./OrgCombobox";
import {
  createMembershipAction,
  deleteMembershipAction,
  type MembershipDto,
  type OrgOption,
} from "./membership-actions";

interface Props {
  handle: string;
  initialMemberships: MembershipDto[];
  currentUsername?: string;
  isAdmin?: boolean;
}

const viaLabel = (v: string) =>
  v === "rsi" ? "RSI" : v === "both" ? "RSI + Discord" : "Discord";

const selectCls =
  "hud-clip border border-hud-cyan-dim bg-hud-bg/60 px-3 py-2 font-mono text-sm text-hud-text focus:border-hud-cyan focus:outline-none";

export function MembershipsSection({ handle, initialMemberships, currentUsername, isAdmin }: Props) {
  const [items, setItems] = useState<MembershipDto[]>(initialMemberships);
  const [busy, setBusy] = useState(false);
  const [selectedOrg, setSelectedOrg] = useState<OrgOption | null>(null);
  const [formKey, setFormKey] = useState(0);

  const canModify = (m: MembershipDto) => Boolean(isAdmin) || m.authorUsername === currentUsername;

  async function add(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    if (!selectedOrg) {
      toast.error("Sélectionne une organisation dans la liste.");
      return;
    }
    const el = e.currentTarget;
    const form = new FormData(el);
    setBusy(true);
    const res = await createMembershipAction(handle, {
      orgSid: selectedOrg.sid,
      rank: String(form.get("rank") ?? "").trim() || undefined,
      via: String(form.get("via") ?? "discord"),
      sinceDate: String(form.get("sinceDate") ?? "").trim() || undefined,
    });
    setBusy(false);
    if (res.ok && res.membership) {
      const created = res.membership;
      setItems((prev) => [created, ...prev.filter((m) => m.orgSid !== created.orgSid)]);
      el.reset();
      setSelectedOrg(null);
      setFormKey((k) => k + 1); // remonte le combobox (reset de sa recherche)
      toast.success("Organisation attribuée.");
    } else {
      toast.error(res.error ?? "Échec.");
    }
  }

  async function remove(id: number) {
    setBusy(true);
    const res = await deleteMembershipAction(id);
    setBusy(false);
    if (res.ok) {
      setItems((prev) => prev.filter((m) => m.id !== id));
      toast.success("Appartenance supprimée.");
    } else {
      toast.error(res.error ?? "Échec.");
    }
  }

  return (
    <HudPanel label={`ORGANISATIONS — AJOUT MANUEL (${items.length})`}>
      <div className="flex flex-col gap-4">
        <form onSubmit={add} className="grid gap-3 sm:grid-cols-2">
          <OrgCombobox key={formKey} selected={selectedOrg} onSelect={setSelectedOrg} />
          <HudInput label="GRADE (optionnel)" name="rank" type="text" maxLength={200} placeholder="ex. Officier" />
          <label className="flex flex-col gap-1">
            <span className="hud-label">PROVENANCE</span>
            <select name="via" defaultValue="discord" className={selectCls}>
              <option value="discord">Discord</option>
              <option value="rsi">RSI</option>
              <option value="both">RSI + Discord</option>
            </select>
          </label>
          <HudInput label="MEMBRE DEPUIS (optionnel)" name="sinceDate" type="date" />
          <div className="sm:col-span-2 flex justify-end">
            <HudButton type="submit" disabled={busy}>
              {busy ? "…" : "ATTRIBUER L'ORGANISATION"}
            </HudButton>
          </div>
        </form>

        {items.length === 0 ? (
          <p className="font-mono text-xs text-hud-text-dim">Aucune appartenance ajoutée manuellement.</p>
        ) : (
          <ul className="flex flex-col gap-2">
            {items.map((m) => (
              <li
                key={m.id}
                className="flex flex-wrap items-center justify-between gap-x-4 gap-y-1 border border-hud-cyan/15 bg-hud-bg/40 p-3"
              >
                <div className="flex flex-wrap items-center gap-3 font-mono text-sm">
                  <span className="text-hud-cyan">{m.orgName ?? m.orgSid}</span>
                  <span className="text-hud-text-dim">[{m.orgSid}]</span>
                  {m.rank && <span className="text-hud-text">· {m.rank}</span>}
                  <span className="border border-hud-cyan-dim px-2 py-0.5 text-[10px] uppercase tracking-wide text-hud-text-dim">
                    {viaLabel(m.via)}
                  </span>
                </div>
                <div className="flex items-center gap-3 font-mono text-[10px] uppercase tracking-wide text-hud-text-dim">
                  <span>depuis {formatDate(m.sinceDate)} · {m.authorUsername}</span>
                  {canModify(m) && (
                    <button type="button" className="hover:text-hud-red" onClick={() => remove(m.id)}>
                      SUPPR
                    </button>
                  )}
                </div>
              </li>
            ))}
          </ul>
        )}
      </div>
    </HudPanel>
  );
}
