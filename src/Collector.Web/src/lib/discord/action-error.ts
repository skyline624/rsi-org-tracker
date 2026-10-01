"use client";
import { unstable_isUnrecognizedActionError } from "next/navigation";
import { toast } from "sonner";

/** A tab kept open through a deployment must not stay locked on an obsolete action. */
export function reportDiscordActionError(error: unknown, fallback: string) {
  if (unstable_isUnrecognizedActionError(error)) {
    toast.error("Le site a été mis à jour. Actualise la page, puis réessaie.", {
      action: { label: "Actualiser", onClick: () => window.location.reload() },
    });
  } else {
    toast.error(fallback);
  }
}
