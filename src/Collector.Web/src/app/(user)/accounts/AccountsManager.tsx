"use client";
import { useState, type FormEvent } from "react";
import { toast } from "sonner";
import { HudPanel } from "@/components/hud/HudPanel";
import { HudButton } from "@/components/hud/HudButton";
import { HudInput } from "@/components/hud/HudInput";
import { HudBadge } from "@/components/hud/HudBadge";
import {
  createAccountAction,
  deleteAccountAction,
  setUserFlagsAction,
  type AdminUserDto,
} from "./actions";

interface Props {
  initialUsers: AdminUserDto[];
  currentUserId: number;
}

export function AccountsManager({ initialUsers, currentUserId }: Props) {
  const [users, setUsers] = useState<AdminUserDto[]>(initialUsers);
  const [busy, setBusy] = useState(false);

  async function create(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const el = e.currentTarget;
    const form = new FormData(el);
    setBusy(true);
    const res = await createAccountAction({
      username: String(form.get("username") ?? "").trim(),
      email: String(form.get("email") ?? "").trim(),
      password: String(form.get("password") ?? ""),
      isAdmin: form.get("isAdmin") === "on",
    });
    setBusy(false);
    if (res.ok && res.user) {
      const created = res.user;
      setUsers((prev) => [...prev, created]);
      el.reset();
      toast.success("Compte créé.");
    } else {
      toast.error(res.error ?? "Échec.");
    }
  }

  async function toggleFlag(u: AdminUserDto, key: "isAdmin" | "isBanned") {
    setBusy(true);
    const res = await setUserFlagsAction(u.id, { [key]: !u[key] });
    setBusy(false);
    if (res.ok && res.user) {
      const updated = res.user;
      setUsers((prev) => prev.map((x) => (x.id === u.id ? updated : x)));
    } else {
      toast.error(res.error ?? "Échec.");
    }
  }

  async function remove(u: AdminUserDto) {
    if (!confirm(`Supprimer le compte ${u.username} ?`)) return;
    setBusy(true);
    const res = await deleteAccountAction(u.id);
    setBusy(false);
    if (res.ok) {
      setUsers((prev) => prev.filter((x) => x.id !== u.id));
      toast.success("Compte supprimé.");
    } else {
      toast.error(res.error ?? "Échec.");
    }
  }

  return (
    <div className="flex flex-col gap-6">
      <HudPanel label="CRÉER UN COMPTE">
        <form onSubmit={create} className="grid gap-3 sm:grid-cols-2">
          <HudInput label="USERNAME" name="username" type="text" required minLength={3} maxLength={100} autoComplete="off" />
          <HudInput label="EMAIL" name="email" type="email" required autoComplete="off" />
          <HudInput label="MOT DE PASSE (≥ 8)" name="password" type="password" required minLength={8} autoComplete="new-password" />
          <label className="flex items-center gap-2 self-end font-mono text-xs text-hud-text-dim">
            <input type="checkbox" name="isAdmin" className="accent-hud-cyan" />
            COMPTE ADMIN
          </label>
          <div className="sm:col-span-2 flex justify-end">
            <HudButton type="submit" disabled={busy}>{busy ? "…" : "CRÉER LE COMPTE"}</HudButton>
          </div>
        </form>
      </HudPanel>

      <HudPanel label={`COMPTES (${users.length})`}>
        <ul className="flex flex-col divide-y divide-hud-cyan/10">
          {users.map((u) => (
            <li key={u.id} className="flex flex-wrap items-center justify-between gap-x-4 gap-y-2 py-3">
              <div className="flex flex-wrap items-center gap-3 font-mono text-sm">
                <span className="text-hud-cyan">{u.username}</span>
                <span className="text-hud-text-dim">{u.email}</span>
                {u.isAdmin && <HudBadge tone="orange">ADMIN</HudBadge>}
                {u.isBanned && <HudBadge tone="red">BANNI</HudBadge>}
                {u.id === currentUserId && (
                  <span className="text-[10px] uppercase tracking-wide text-hud-text-dim">(vous)</span>
                )}
              </div>
              {u.id !== currentUserId && (
                <div className="flex items-center gap-3 font-mono text-[10px] uppercase tracking-wide text-hud-text-dim">
                  <button type="button" className="hover:text-hud-orange" onClick={() => toggleFlag(u, "isAdmin")}>
                    {u.isAdmin ? "retirer admin" : "promouvoir admin"}
                  </button>
                  <button type="button" className="hover:text-hud-orange" onClick={() => toggleFlag(u, "isBanned")}>
                    {u.isBanned ? "débannir" : "bannir"}
                  </button>
                  <button type="button" className="hover:text-hud-red" onClick={() => remove(u)}>
                    supprimer
                  </button>
                </div>
              )}
            </li>
          ))}
        </ul>
      </HudPanel>
    </div>
  );
}
