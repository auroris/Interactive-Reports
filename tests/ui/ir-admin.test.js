import assert from "node:assert/strict";
import test from "node:test";
import { Window } from "happy-dom";

const window = new Window({ url: "https://host.example/admin" });
Object.assign(globalThis, {
    window,
    document: window.document,
    HTMLElement: window.HTMLElement,
    ShadowRoot: window.ShadowRoot,
    customElements: window.customElements,
    Node: window.Node,
    CustomEvent: window.CustomEvent,
    requestAnimationFrame: callback => setTimeout(callback, 0),
});

// Per-test whoami behavior; the embedded __saved-reports listing 404s throughout
// (the non-administrator experience).
let whoami = null;
let whoamiStatus = 404;
let whoamiCalls = 0;
const json = (value, status = 200) => new Response(JSON.stringify(value), {
    status,
    headers: { "Content-Type": "application/json" },
});
globalThis.fetch = async url => {
    if (String(url).endsWith("/whoami")) {
        whoamiCalls++;
        return whoami === null
            ? new Response(null, { status: whoamiStatus })
            : json(whoami, whoamiStatus);
    }
    return new Response(null, { status: 404 });
};

await import("../../src/InteractiveReport.AspNetCore/Ui/dist/ir-admin.js");

const settle = async condition => {
    for (let attempt = 0; attempt < 40 && !condition(); attempt++)
        await new Promise(resolve => setTimeout(resolve, 5));
};

async function mount() {
    whoamiCalls = 0;
    const admin = document.createElement("interactive-report-admin");
    admin.setAttribute("api-base", "/admin-api");
    document.body.append(admin);
    await settle(() => admin.shadowRoot?.querySelector(".ir-banner"));
    return admin;
}

test("a disabled whoami endpoint yields packaged guidance instead of a bare listing error", async () => {
    whoami = null;
    whoamiStatus = 404;
    const admin = await mount();

    const banner = admin.shadowRoot.querySelector(".ir-banner-warn");
    assert.ok(banner, "the whoami-off guidance banner renders");
    assert.match(banner.textContent, /signed-in administrator/);
    assert.match(banner.textContent, /WhoamiEnabled/);
    assert.equal(whoamiCalls, 1,
        "the admin shell and its embedded report coalesce their identity request");

    admin.remove();
});

test("a configured administrator list still produces the precise denial", async () => {
    whoami = {
        authenticated: true,
        identity: "ordinary-user",
        isAdministrator: false,
        administratorListConfigured: true,
        applicationAuthorizationConfigured: false,
    };
    whoamiStatus = 200;
    const admin = await mount();

    const banner = admin.shadowRoot.querySelector(".ir-banner-error");
    assert.ok(banner, "the administrator-required banner renders");
    assert.match(banner.textContent, /Add your identity to InteractiveReport:Administrators/);
    assert.equal(admin.shadowRoot.querySelector(".ir-banner-warn"), null);
    assert.match(admin.shadowRoot.querySelector(".ir-admin-count").textContent, /ordinary-user/);

    admin.remove();
});

test("a real whoami failure presents the server problem and trace reference", async () => {
    whoami = {
        title: "Identity service failed",
        detail: "Try again later.",
        traceId: "trace-admin-1",
    };
    whoamiStatus = 500;
    const admin = await mount();

    const banner = admin.shadowRoot.querySelector(".ir-banner-error");
    assert.ok(banner);
    assert.match(banner.textContent, /Identity service failed/);
    assert.match(banner.textContent, /Try again later/);
    assert.match(banner.textContent, /trace-admin-1/);
    assert.doesNotMatch(banner.textContent, /WhoamiEnabled/);

    admin.remove();
    whoami = null;
    whoamiStatus = 404;
});
