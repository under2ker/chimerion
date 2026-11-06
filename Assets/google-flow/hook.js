(() => {
  "use strict";

  const FLOW_ORIGIN = "https://flow.google.com";
  const BATCHEXECUTE_PATH = "/_/AiSandboxAngularFrontend/data/batchexecute";
  const TARGET_RPC_ID = "cPZSdc";
  const DIAG_ATTRIBUTE = "data-flow-local-diagnostic";
  const BOOL_INDEX = 30;
  const MIN_CONFIG_LENGTH = 32;
  const EXPECTED_FRAME_TOLERANCE = [0, 1, 2];

  const utf8Length = (value) => new TextEncoder().encode(value).length;
  const byteLengthFns = [utf8Length, (value) => value.length];

  function patchBody(body) {
    const lines = body.split("\n");
    let before;
    let applied = 0;

    for (let i = 0; i < lines.length; i++) {
      if (!lines[i].startsWith("[[")) continue;

      let frame;
      try {
        frame = JSON.parse(lines[i]);
      } catch {
        continue;
      }

      let touched = false;
      for (const entry of frame) {
        if (
          !Array.isArray(entry) ||
          entry[0] !== "wrb.fr" ||
          entry[1] !== TARGET_RPC_ID
        )
          continue;

        const config = JSON.parse(entry[2]);
        if (
          !Array.isArray(config) ||
          config.length < MIN_CONFIG_LENGTH ||
          (config[BOOL_INDEX] !== null &&
            typeof config[BOOL_INDEX] !== "boolean")
        )
          throw new Error("Unexpected config schema");

        before = config[BOOL_INDEX];
        config[BOOL_INDEX] = true;
        applied++;
        entry[2] = JSON.stringify(config);
        touched = true;
      }

      if (!touched) continue;

      const raw = lines[i];
      const dataLine = raw.replace(/\r$/, "");
      const lengthLine = lines[i - 1]?.trim();
      if (!/^\d+$/.test(lengthLine || ""))
        throw new Error("Unexpected frame length");
      const declaredLength = Number(lengthLine);
      if (!Number.isSafeInteger(declaredLength))
        throw new Error("Unexpected frame length");
      const match = byteLengthFns.find((measure) =>
        EXPECTED_FRAME_TOLERANCE.includes(declaredLength - measure(dataLine)),
      );
      if (!match) throw new Error("Unexpected frame length");

      const patched = JSON.stringify(frame);
      lines[i - 1] = String(declaredLength + match(patched) - match(dataLine));
      lines[i] = patched;
    }

    if (applied === 0) throw new Error("Expected at least one config response");
    return { body: lines.join("\n"), before };
  }

  if (typeof window === "undefined" && typeof module === "object" && module.exports) {
    module.exports = { patch: patchBody };
    return;
  }

  // Страница не Flow или hook уже установлен.
  if (location.origin !== FLOW_ORIGIN || window.__flowLocalDiagnostic) return;

  const diag = (window.__flowLocalDiagnostic = { state: "armed", applied: 0 });

  function publish(state, before) {
    Object.assign(diag, { state, before });
    console.info("[Flow local diagnostic]", JSON.stringify(diag));
    const write = () =>
      document.documentElement.setAttribute(
        DIAG_ATTRIBUTE,
        JSON.stringify(diag),
      );
    if (document.documentElement) write();
    else
      document.addEventListener("DOMContentLoaded", write, { once: true });
  }

  function isTarget(url) {
    try {
      const parsed = new URL(url, location.href);
      return (
        parsed.origin === location.origin &&
        parsed.pathname === BATCHEXECUTE_PATH &&
        parsed.searchParams.get("rpcids") === TARGET_RPC_ID
      );
    } catch {
      return false;
    }
  }

  // Synthetic responses lose network metadata; retain it on every clone too.
  function withMetadata(response, original) {
    const clone = response.clone;
    Object.defineProperties(response, {
      url: { value: original.url, configurable: true },
      redirected: { value: original.redirected, configurable: true },
      type: { value: original.type, configurable: true },
      clone: {
        configurable: true,
        writable: true,
        value: function () {
          return withMetadata(Reflect.apply(clone, this, []), original);
        },
      },
    });
    return response;
  }

  if (typeof window.fetch === "function") {
    const originalFetch = window.fetch;
    window.fetch = function (...args) {
      const pending = Reflect.apply(originalFetch, this, args);
      const input = args[0];
      const target = isTarget(
        typeof Request !== "undefined" && input instanceof Request
          ? input.url
          : input,
      );
      if (!target) return pending;

      return pending.then(async (response) => {
        try {
          const result = patchBody(await response.clone().text());
          const headers = new Headers(response.headers);
          headers.delete("content-length");
          headers.delete("content-encoding");
          const patched = withMetadata(new Response(result.body, {
            status: response.status,
            statusText: response.statusText,
            headers,
          }), response);
          diag.applied++;
          publish("applied", result.before);
          return patched;
        } catch {
          publish("schema mismatch — unchanged");
          return response;
        }
      });
    };
  }

  const patchedFlags = new WeakMap();
  const patchedBodies = new WeakMap();
  const xhrProto = XMLHttpRequest.prototype;
  const originalOpen = xhrProto.open;

  if (["responseText", "response"].some((prop) => {
    const descriptor = Object.getOwnPropertyDescriptor(xhrProto, prop);
    return !descriptor?.get || !descriptor.configurable;
  })) {
    publish("hook-unavailable");
    return;
  }

  xhrProto.open = function (method, url, ...rest) {
    patchedFlags.set(this, isTarget(url));
    patchedBodies.delete(this);
    return Reflect.apply(originalOpen, this, [method, url, ...rest]);
  };

  for (const prop of ["responseText", "response"]) {
    const descriptor = Object.getOwnPropertyDescriptor(xhrProto, prop);
    if (!descriptor?.get || !descriptor.configurable) continue;

    Object.defineProperty(xhrProto, prop, {
      ...descriptor,
      get() {
        const value = Reflect.apply(descriptor.get, this, []);
        if (!patchedFlags.get(this) || typeof value !== "string") return value;

        if (this.readyState === 3) {
          diag.heldPartial = true;
          return "";
        }

        if (this.readyState === 4 && !patchedBodies.has(this)) {
          try {
            const result = patchBody(value);
            patchedBodies.set(this, result.body);
            diag.applied++;
            publish("applied", result.before);
          } catch {
            patchedBodies.set(this, value);
            publish("schema mismatch — unchanged");
          }
        }

        return patchedBodies.has(this) ? patchedBodies.get(this) : value;
      },
    });
  }

  publish("armed");
})();
