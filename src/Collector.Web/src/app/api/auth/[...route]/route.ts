import { NextRequest, NextResponse } from "next/server";
import {
  COOKIE_ACCESS,
  COOKIE_REFRESH,
  accessCookieOptions,
  refreshCookieOptions,
} from "@/lib/auth/cookies";
import type { AuthResponse } from "@/lib/api/types";

/**
 * BFF (Backend-for-Frontend) des routes `/api/auth/*`.
 *
 * Proxifie vers `Collector.Api` et intercepte les réponses :
 *   - `login` / `refresh` / `register` → pose `sct_access` + `sct_refresh` en httpOnly
 *   - `logout` → supprime les cookies après relais
 *   - autres (`forgot-password`, `reset-password`) → relais pur
 *
 * Le navigateur ne voit jamais les tokens : ils sont posés en `HttpOnly` et
 * le fetch côté client utilise `credentials: "include"` qui les renverra
 * automatiquement pour les appels au même host.
 */

const API_BASE = process.env.API_BASE_URL ?? "https://localhost:5001";

// Les segments de route qui existent côté API .NET.
const ALLOWED_SEGMENTS = new Set([
  "login",
  "register",
  "refresh",
  "logout",
  "me",
  "forgot-password",
  "reset-password",
]);

function proxyHeaders(req: NextRequest): HeadersInit {
  const h: Record<string, string> = {
    Accept: "application/json",
    "Content-Type": "application/json",
  };
  const cid = req.headers.get("x-correlation-id");
  if (cid) h["X-Correlation-Id"] = cid;
  return h;
}

async function handle(
  req: NextRequest,
  ctx: { params: Promise<{ route: string[] }> },
  method: "GET" | "POST",
): Promise<NextResponse> {
  const { route } = await ctx.params;
  const segment = route[0];
  if (!segment || !ALLOWED_SEGMENTS.has(segment)) {
    return NextResponse.json({ error: "Not found" }, { status: 404 });
  }

  // Pour `me`, on relit le JWT depuis le cookie et on le pose en Authorization.
  const isMe = segment === "me";
  const headers: Record<string, string> = { ...proxyHeaders(req) } as Record<
    string,
    string
  >;
  if (isMe) {
    const access = req.cookies.get(COOKIE_ACCESS)?.value;
    if (!access) {
      return NextResponse.json({ error: "Unauthorized" }, { status: 401 });
    }
    headers["Authorization"] = `Bearer ${access}`;
  }

  // Body transparent pour POST.
  const body = method === "POST" ? await req.text() : undefined;

  // Pour refresh, si pas de body fourni, on lit le cookie sct_refresh.
  let finalBody = body;
  if (segment === "refresh" && (!body || body === "")) {
    const rt = req.cookies.get(COOKIE_REFRESH)?.value;
    if (!rt) {
      return NextResponse.json({ error: "Unauthorized" }, { status: 401 });
    }
    finalBody = JSON.stringify({ refreshToken: rt });
  }
  // Pour logout idem.
  if (segment === "logout" && (!body || body === "")) {
    const rt = req.cookies.get(COOKIE_REFRESH)?.value;
    finalBody = JSON.stringify({ refreshToken: rt ?? "" });
  }

  let upstream: Response;
  try {
    upstream = await fetch(`${API_BASE}/api/auth/${segment}`, {
      method,
      headers,
      body: finalBody,
      // accept self-signed cert in dev
      // @ts-expect-error — Next/Node 20+ supports this via env var only
      rejectUnauthorized: false,
    });
  } catch (e) {
    return NextResponse.json(
      { title: "Upstream unreachable", detail: String(e) },
      { status: 502 },
    );
  }

  const text = await upstream.text();
  const response = new NextResponse(text, {
    status: upstream.status,
    headers: {
      "Content-Type":
        upstream.headers.get("content-type") ?? "application/json",
    },
  });

  // Post login/register/refresh — extraire AuthResponse et poser cookies.
  if (
    upstream.ok &&
    (segment === "login" || segment === "refresh" || segment === "register")
  ) {
    try {
      const auth = JSON.parse(text) as AuthResponse;
      // `register` renvoie UserDto, pas AuthResponse — skip si pas de token.
      if (auth?.accessToken && auth?.refreshToken && auth?.expiresAt) {
        response.cookies.set(
          COOKIE_ACCESS,
          auth.accessToken,
          accessCookieOptions(new Date(auth.expiresAt)),
        );
        response.cookies.set(
          COOKIE_REFRESH,
          auth.refreshToken,
          refreshCookieOptions(),
        );
      }
    } catch {
      /* body non-JSON — laisser tel quel */
    }
  }

  // Post logout — clear cookies quelle que soit la réponse upstream.
  if (segment === "logout") {
    response.cookies.delete(COOKIE_ACCESS);
    response.cookies.delete(COOKIE_REFRESH);
  }

  return response;
}

export async function POST(
  req: NextRequest,
  ctx: { params: Promise<{ route: string[] }> },
) {
  return handle(req, ctx, "POST");
}

export async function GET(
  req: NextRequest,
  ctx: { params: Promise<{ route: string[] }> },
) {
  return handle(req, ctx, "GET");
}
