/**
 * Noms et options des cookies auth. Centralisés pour éviter les divergences
 * entre le BFF (POST) et le middleware (lecture).
 */

export const COOKIE_ACCESS = "sct_access";
export const COOKIE_REFRESH = "sct_refresh";

export const cookieBaseOptions = {
  httpOnly: true,
  sameSite: "strict" as const,
  secure: process.env.NODE_ENV === "production",
  path: "/",
};

export const accessCookieOptions = (expiresAt: Date) => ({
  ...cookieBaseOptions,
  expires: expiresAt,
});

export const refreshCookieOptions = () => ({
  ...cookieBaseOptions,
  // 30 jours — aligné sur RefreshTokenDays côté API
  maxAge: 60 * 60 * 24 * 30,
});
