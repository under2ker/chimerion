// Главный адрес сайта chimerion.dev: chimerion.pages.dev и www ведут на него. Превью-выкладки (*.chimerion.pages.dev) не трогаются
const MAIN = "chimerion.dev";
const ALIASES = new Set(["chimerion.pages.dev", "www.chimerion.dev"]);
const SECURITY = {
  "X-Content-Type-Options": "nosniff",
  "Referrer-Policy": "strict-origin-when-cross-origin",
  "X-Frame-Options": "DENY",
  "Permissions-Policy": "camera=(), microphone=(), geolocation=()",
  "Content-Security-Policy": "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline' https://static.cloudflareinsights.com; connect-src 'self' https://api.github.com https://cloudflareinsights.com; font-src 'self'; frame-ancestors 'none'; base-uri 'self'",
};

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (ALIASES.has(url.hostname)) {
      url.hostname = MAIN;
      return Response.redirect(url.toString(), 301);
    }
    const res = await env.ASSETS.fetch(request);
    const out = new Response(res.body, res);
    for (const [k, v] of Object.entries(SECURITY)) out.headers.set(k, v);
    if (url.pathname.startsWith("/fonts/")) out.headers.set("Cache-Control", "public, max-age=31536000, immutable");
    else if (url.pathname.startsWith("/img/")) out.headers.set("Cache-Control", "public, max-age=604800");
    return out;
  },
};
