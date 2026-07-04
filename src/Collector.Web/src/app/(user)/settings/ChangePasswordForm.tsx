"use client";
import { useState, type FormEvent } from "react";
import { toast } from "sonner";
import { HudButton } from "@/components/hud/HudButton";
import { HudInput } from "@/components/hud/HudInput";
import { changePasswordAction } from "./actions";

export function ChangePasswordForm() {
  const [loading, setLoading] = useState(false);

  async function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const el = e.currentTarget;
    const form = new FormData(el);
    const currentPassword = String(form.get("currentPassword") ?? "");
    const newPassword = String(form.get("newPassword") ?? "");
    const confirm = String(form.get("confirm") ?? "");

    if (newPassword !== confirm) {
      toast.error("Les nouveaux mots de passe ne correspondent pas.");
      return;
    }
    if (newPassword.length < 8) {
      toast.error("Le nouveau mot de passe doit faire au moins 8 caractères.");
      return;
    }

    setLoading(true);
    const res = await changePasswordAction(currentPassword, newPassword);
    setLoading(false);

    if (res.ok) {
      toast.success("Mot de passe modifié.");
      el.reset();
    } else {
      toast.error(res.error ?? "Échec.");
    }
  }

  return (
    <form onSubmit={submit} className="flex flex-col gap-4">
      <HudInput
        label="MOT DE PASSE ACTUEL"
        name="currentPassword"
        type="password"
        required
        autoComplete="current-password"
      />
      <HudInput
        label="NOUVEAU MOT DE PASSE (≥ 8)"
        name="newPassword"
        type="password"
        required
        minLength={8}
        autoComplete="new-password"
      />
      <HudInput
        label="CONFIRMER"
        name="confirm"
        type="password"
        required
        minLength={8}
        autoComplete="new-password"
      />
      <HudButton type="submit" disabled={loading}>
        {loading ? "…" : "CHANGER LE MOT DE PASSE"}
      </HudButton>
    </form>
  );
}
