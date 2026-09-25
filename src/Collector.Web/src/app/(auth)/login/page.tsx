"use client";
import { useSearchParams } from "next/navigation";
import { useState, type FormEvent } from "react";
import { toast } from "sonner";
import { HudPanel } from "@/components/hud/HudPanel";
import { HudButton } from "@/components/hud/HudButton";
import { HudInput } from "@/components/hud/HudInput";
import { safeInternalPath } from "@/lib/utils/safe-redirect";

export default function LoginPage() {
  const params = useSearchParams();
  const from = safeInternalPath(params.get("from"));

  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function handleSubmit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    setError(null);
    setLoading(true);

    const form = new FormData(e.currentTarget);
    const payload = {
      username: String(form.get("username") ?? ""),
      password: String(form.get("password") ?? ""),
    };

    try {
      const res = await fetch("/api/auth/login", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(payload),
      });
      if (!res.ok) {
        const body = await res.json().catch(() => ({}));
        throw new Error(body.title ?? body.error ?? "Login failed");
      }
      toast.success("Session opened");
      // Navigation pleine page : garantit l'envoi du cookie fraîchement posé et
      // évite un cache RSC périmé qui renverrait vers /login.
      window.location.assign(from);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Login failed");
    } finally {
      setLoading(false);
    }
  }

  return (
    <div className="mx-auto flex max-w-md flex-col gap-6 pt-12">
      <div>
        <div className="hud-label">— UEE::SESSION_AUTH</div>
        <h1 className="mt-1 font-display text-3xl">Secure Login</h1>
      </div>
      <HudPanel label="CREDENTIALS">
        <form onSubmit={handleSubmit} className="flex flex-col gap-4">
          <HudInput
            label="USERNAME"
            name="username"
            type="text"
            required
            autoComplete="username"
          />
          <HudInput
            label="PASSWORD"
            name="password"
            type="password"
            required
            autoComplete="current-password"
          />
          {error && (
            <div className="border border-hud-red bg-hud-red/10 px-3 py-2 font-mono text-[11px] uppercase tracking-wide text-hud-red">
              {error}
            </div>
          )}
          <div className="mt-2 flex items-center justify-between">
            <HudButton type="submit" disabled={loading}>
              {loading ? "AUTHENTICATING…" : "CONNECT"}
            </HudButton>
          </div>
        </form>
      </HudPanel>
    </div>
  );
}
