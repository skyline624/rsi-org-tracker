"use client";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { useState, type ReactNode } from "react";
import { Toaster } from "sonner";

export function Providers({ children }: { children: ReactNode }) {
  const [client] = useState(
    () =>
      new QueryClient({
        defaultOptions: {
          queries: {
            staleTime: 30_000,
            refetchOnWindowFocus: false,
            retry: (failureCount, error) => {
              // On laisse le client.ts gérer les 401/429
              const status = (error as { status?: number })?.status;
              if (status && [400, 401, 403, 404, 429].includes(status)) {
                return false;
              }
              return failureCount < 2;
            },
          },
        },
      }),
  );

  return (
    <QueryClientProvider client={client}>
      {children}
      <Toaster
        theme="dark"
        position="bottom-right"
        toastOptions={{
          style: {
            background: "var(--hud-bg-elevated)",
            border: "1px solid var(--hud-cyan)",
            color: "var(--hud-text)",
            fontFamily: "var(--font-mono), monospace",
            fontSize: "0.75rem",
            textTransform: "uppercase",
            letterSpacing: "0.1em",
          },
        }}
      />
    </QueryClientProvider>
  );
}
