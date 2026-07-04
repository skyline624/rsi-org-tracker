import { getSession } from "@/lib/auth/session";

/**
 * BFF stream proxy for audio. A browser <audio> element can't send the bearer
 * token, so it hits this route (which carries the session cookie); we forward the
 * request to the API with the token, propagating Range for seek support.
 */
export async function GET(
  req: Request,
  { params }: { params: Promise<{ id: string }> },
) {
  const session = await getSession();
  if (!session) return new Response("Unauthorized", { status: 401 });

  const { id } = await params;
  const range = req.headers.get("range");

  const apiRes = await fetch(`${process.env.API_BASE_URL}/api/audio/${encodeURIComponent(id)}`, {
    headers: {
      Authorization: `Bearer ${session.accessToken}`,
      ...(range ? { Range: range } : {}),
    },
    cache: "no-store",
  });

  if (apiRes.status !== 200 && apiRes.status !== 206) {
    return new Response("Not found", { status: apiRes.status });
  }

  const headers = new Headers();
  for (const h of ["content-type", "content-length", "content-range", "accept-ranges"]) {
    const v = apiRes.headers.get(h);
    if (v) headers.set(h, v);
  }
  return new Response(apiRes.body, { status: apiRes.status, headers });
}
