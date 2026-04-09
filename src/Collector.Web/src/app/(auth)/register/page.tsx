"use client";
import { useRouter } from "next/navigation";
import Link from "next/link";
import { useState, type FormEvent } from "react";
import { toast } from "sonner";
import { HudPanel } from "@/components/hud/HudPanel";
import { HudButton } from "@/components/hud/HudButton";
import { HudInput } from "@/components/hud/HudInput";

export default function RegisterPage() {
  const router = useRouter();
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function handleSubmit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    setError(null);
    setLoading(true);

    const form = new FormData(e.currentTarget);
    const payload = {
      username: String(form.get("username") ?? ""),
      email: String(form.get("email") ?? ""),
      password: String(form.get("password") ?? ""),
    };

    try {
      const regRes = await fetch("/api/auth/register", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(payload),
      });
      if (!regRes.ok) {
        const body = await regRes.json().catch(() => ({}));
        throw new Error(body.title ?? body.error ?? "Registration failed");
      }

      // Immediate login after successful registration
      const loginRes = await fetch("/api/auth/login", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          username: payload.username,
          password: payload.password,
        }),
      });
      if (!loginRes.ok) {
        throw new Error("Registered, but login failed. Please log in.");
      }
      toast.success("Welcome aboard, citizen");
      router.push("/dashboard");
      router.refresh();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Registration failed");
    } finally {
      setLoading(false);
    }
  }

  return (
    <div className="mx-auto flex max-w-md flex-col gap-6 pt-12">
      <div>
        <div className="hud-label">— UEE::NEW_CITIZEN</div>
        <h1 className="mt-1 font-display text-3xl">Enlist</h1>
      </div>
      <HudPanel label="REGISTRATION FORM">
        <form onSubmit={handleSubmit} className="flex flex-col gap-4">
          <HudInput
            label="USERNAME"
            name="username"
            type="text"
            required
            minLength={3}
            maxLength={50}
            autoComplete="username"
          />
          <HudInput
            label="EMAIL"
            name="email"
            type="email"
            required
            autoComplete="email"
          />
          <HudInput
            label="PASSWORD (≥ 8 chars)"
            name="password"
            type="password"
            required
            minLength={8}
            autoComplete="new-password"
          />
          {error && (
            <div className="border border-hud-red bg-hud-red/10 px-3 py-2 font-mono text-[11px] uppercase tracking-wide text-hud-red">
              {error}
            </div>
          )}
          <HudButton type="submit" disabled={loading}>
            {loading ? "ENLISTING…" : "ENLIST"}
          </HudButton>
        </form>
      </HudPanel>
      <p className="text-center font-mono text-[11px] uppercase tracking-[0.15em] text-hud-text-dim">
        ALREADY REGISTERED?{" "}
        <Link href="/login" className="text-hud-cyan hover:text-hud-orange">
          LOGIN
        </Link>
      </p>
    </div>
  );
}
