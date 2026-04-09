"use client";
import { useRouter, useSearchParams } from "next/navigation";
import Link from "next/link";
import { useState, type FormEvent } from "react";
import { toast } from "sonner";
import { HudPanel } from "@/components/hud/HudPanel";
import { HudButton } from "@/components/hud/HudButton";
import { HudInput } from "@/components/hud/HudInput";

export default function LoginPage() {
  const router = useRouter();
  const params = useSearchParams();
  const from = params.get("from") ?? "/dashboard";

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
      router.push(from);
      router.refresh();
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
            <Link
              href="/forgot-password"
              className="font-mono text-[11px] uppercase tracking-[0.15em] text-hud-text-dim hover:text-hud-cyan"
            >
              FORGOT?
            </Link>
          </div>
        </form>
      </HudPanel>
      <p className="text-center font-mono text-[11px] uppercase tracking-[0.15em] text-hud-text-dim">
        NOT REGISTERED?{" "}
        <Link href="/register" className="text-hud-cyan hover:text-hud-orange">
          ENLIST HERE
        </Link>
      </p>
    </div>
  );
}
