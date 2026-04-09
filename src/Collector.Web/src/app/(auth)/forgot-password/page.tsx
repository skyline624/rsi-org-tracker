"use client";
import Link from "next/link";
import { useState, type FormEvent } from "react";
import { HudPanel } from "@/components/hud/HudPanel";
import { HudButton } from "@/components/hud/HudButton";
import { HudInput } from "@/components/hud/HudInput";

export default function ForgotPasswordPage() {
  const [loading, setLoading] = useState(false);
  const [done, setDone] = useState(false);

  async function handleSubmit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    setLoading(true);
    const form = new FormData(e.currentTarget);
    try {
      await fetch("/api/auth/forgot-password", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ email: String(form.get("email") ?? "") }),
      });
    } catch {
      /* ignore — always show the same message to avoid user enumeration */
    } finally {
      setLoading(false);
      setDone(true);
    }
  }

  return (
    <div className="mx-auto flex max-w-md flex-col gap-6 pt-12">
      <div>
        <div className="hud-label">— UEE::PASSWORD_RECOVERY</div>
        <h1 className="mt-1 font-display text-3xl">Reset Access</h1>
      </div>
      <HudPanel label="EMAIL LOOKUP">
        {done ? (
          <p className="font-mono text-xs text-hud-green">
            — transmission sent. If the address exists, a reset token has been
            issued via the operator channel.
          </p>
        ) : (
          <form onSubmit={handleSubmit} className="flex flex-col gap-4">
            <HudInput
              label="EMAIL"
              name="email"
              type="email"
              required
              autoComplete="email"
            />
            <HudButton type="submit" disabled={loading}>
              {loading ? "SENDING…" : "SEND RESET TOKEN"}
            </HudButton>
          </form>
        )}
      </HudPanel>
      <p className="text-center font-mono text-[11px] uppercase tracking-[0.15em] text-hud-text-dim">
        <Link href="/login" className="text-hud-cyan hover:text-hud-orange">
          ← BACK TO LOGIN
        </Link>
      </p>
    </div>
  );
}
