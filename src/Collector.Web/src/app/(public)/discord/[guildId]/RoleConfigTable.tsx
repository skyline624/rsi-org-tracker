"use client";
import { useState } from "react";
import { useRouter } from "next/navigation";
import { toast } from "sonner";
import { DiscordText } from "@/components/discord/DiscordText";
import { RoleDot } from "@/components/discord/RoleDot";
import { HudBadge } from "@/components/hud/HudBadge";
import { HudButton } from "@/components/hud/HudButton";
import type { DiscordRoleDto } from "@/lib/api/types";
import { cleanDiscordText } from "@/lib/discord/format";
import { parseRankOrderInput } from "@/lib/discord/params";
import { formatNumber } from "@/lib/utils/format";
import { updateGuildRoleAction } from "../actions";

const fieldClass =
  "hud-clip w-full border border-hud-cyan-dim bg-hud-bg px-2 py-1 font-mono text-xs text-hud-text [color-scheme:dark] focus:border-hud-cyan focus:outline-none disabled:opacity-40";

interface RoleConfigRowProps {
  guildId: string;
  role: DiscordRoleDto;
  rsiRanks: string[];
  canEdit: boolean;
}

function RoleConfigRow({ guildId, role, rsiRanks, canEdit }: RoleConfigRowProps) {
  const router = useRouter();
  const savedOrder = role.rankOrder === null ? "" : String(role.rankOrder);
  const savedLabel = role.rsiRankLabel ?? "";
  const [isRank, setIsRank] = useState(role.isRank);
  const [order, setOrder] = useState(savedOrder);
  const [label, setLabel] = useState(savedLabel);
  const [busy, setBusy] = useState(false);
  const name = cleanDiscordText(role.name) ?? role.roleId;
  const dirty = isRank !== role.isRank || order !== savedOrder || label !== savedLabel;
  // A label chosen earlier may no longer be among the org's RSI ranks: keep it selectable.
  const labels = label !== "" && !rsiRanks.includes(label) ? [label, ...rsiRanks] : rsiRanks;
  const disabled = !canEdit || busy;

  async function save() {
    const parsedOrder = parseRankOrderInput(order);
    if (!parsedOrder.ok) {
      toast.error("Ordre : un nombre entier de 0 à 1000, ou vide.");
      return;
    }
    setBusy(true);
    const res = await updateGuildRoleAction(guildId, role.roleId, isRank, parsedOrder.value, label === "" ? null : label);
    setBusy(false);
    if (res.ok) {
      toast.success(`Rôle « ${name} » enregistré.`);
      router.refresh();
    } else {
      toast.error(res.error ?? "Échec.");
    }
  }

  return (
    <tr className="border-b border-hud-cyan/10">
      <td className="px-2 py-2">
        <span className="flex min-w-0 items-center gap-2">
          <RoleDot color={role.color} />
          <DiscordText value={role.name} fallback={role.roleId} className="min-w-0" />
          {role.managed && <HudBadge tone="dim">GÉRÉ</HudBadge>}
          {role.deleted && <HudBadge tone="red">SUPPRIMÉ</HudBadge>}
        </span>
      </td>
      <td className="px-2 py-2 text-right tabular-nums text-hud-text-dim">{formatNumber(role.memberCount)}</td>
      <td className="px-2 py-2 text-center">
        <input
          type="checkbox"
          checked={isRank}
          onChange={(e) => setIsRank(e.target.checked)}
          disabled={disabled}
          aria-label={`${name} est un rang`}
          className="accent-hud-cyan"
        />
      </td>
      <td className="px-2 py-2">
        <input
          type="number"
          min={0}
          max={1000}
          step={1}
          value={order}
          onChange={(e) => setOrder(e.target.value)}
          disabled={disabled}
          placeholder={String(role.position)}
          aria-label={`Ordre du rang ${name}`}
          className={fieldClass}
        />
      </td>
      <td className="px-2 py-2">
        <select
          value={label}
          onChange={(e) => setLabel(e.target.value)}
          disabled={disabled}
          aria-label={`Rang RSI équivalent à ${name}`}
          className={fieldClass}
        >
          <option value="">— aucun —</option>
          {labels.map((rank) => (
            <option key={rank} value={rank}>
              {rank}
            </option>
          ))}
        </select>
      </td>
      <td className="px-2 py-2 text-right">
        <HudButton type="button" className="px-2 py-1" disabled={disabled || !dirty} onClick={save}>
          {busy ? "…" : "Enregistrer"}
        </HudButton>
      </td>
    </tr>
  );
}

interface RoleConfigTableProps {
  guildId: string;
  roles: DiscordRoleDto[];
  /** RSI ranks of the mapped org, offered as equivalents. */
  rsiRanks: string[];
  canEdit: boolean;
}

/**
 * The server's roles: rank or not, rank order and RSI equivalent. A blank order is sent
 * as null: the API keeps a rank's own order and gives a role that becomes a rank its
 * Discord position (C6). Read-only unless the caller may configure the server.
 */
export function RoleConfigTable({ guildId, roles, rsiRanks, canEdit }: RoleConfigTableProps) {
  const ordered = [...roles].sort((a, b) => Number(a.deleted) - Number(b.deleted) || b.position - a.position);
  if (ordered.length === 0) {
    return <p className="py-4 text-center font-mono text-xs text-hud-text-dim">Aucun rôle reçu.</p>;
  }
  return (
    <div className="w-full overflow-x-auto">
      <table className="w-full min-w-[40rem] table-fixed font-mono text-xs">
        <thead>
          <tr className="border-b border-hud-cyan/30 text-left text-[10px] uppercase tracking-[0.15em] text-hud-text-dim">
            <th className="px-2 py-2 font-normal">RÔLE</th>
            <th className="w-20 px-2 py-2 text-right font-normal">MEMBRES</th>
            <th className="w-16 px-2 py-2 text-center font-normal">RANG</th>
            <th className="w-24 px-2 py-2 font-normal">ORDRE</th>
            <th className="w-48 px-2 py-2 font-normal">RANG RSI</th>
            <th className="w-32 px-2 py-2" />
          </tr>
        </thead>
        <tbody>
          {ordered.map((role) => (
            <RoleConfigRow
              // Saved values in the key: after router.refresh() the row starts from what the API stored.
              key={`${role.roleId}:${role.isRank}:${role.rankOrder}:${role.rsiRankLabel}`}
              guildId={guildId}
              role={role}
              rsiRanks={rsiRanks}
              canEdit={canEdit}
            />
          ))}
        </tbody>
      </table>
    </div>
  );
}
