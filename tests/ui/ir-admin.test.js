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
    Event: window.Event,
    Option: function Option(text = "", value = "") {
        const option = window.document.createElement("option");
        option.textContent = text;
        option.value = value;
        return option;
    },
    CustomEvent: window.CustomEvent,
    requestAnimationFrame: callback => setTimeout(callback, 0),
});

// Per-test whoami behavior; the embedded __saved-reports listing 404s throughout
// (the non-administrator experience).
let whoami = null;
let whoamiStatus = 404;
let whoamiCalls = 0;
let users = null; // Directory entries, or null when the lookup route does not exist.
let userSearches = [];
let administrators = null; // { configured, database, managedByApplication }, or null for a 404.
let administratorCalls = [];
let savedCalls = [];
const json = (value, status = 200) => new Response(JSON.stringify(value), {
    status,
    headers: { "Content-Type": "application/json" },
});
globalThis.fetch = async (url, options = {}) => {
    const method = options.method ?? "GET";
    const target = new URL(String(url), "https://host.example");
    const path = target.pathname;
    const body = options.body === undefined ? null : JSON.parse(options.body);
    if (path === "/admin-api") {
        return json([{ name: "orders", title: "Orders" }]);
    }
    if (path === "/admin-api/orders") {
        return json([{
            id: 1,
            reportName: "orders",
            title: "Default",
            isDefault: true,
            isGlobal: true,
        }]);
    }
    if (path.endsWith("/whoami")) {
        whoamiCalls++;
        return whoami === null
            ? new Response(null, { status: whoamiStatus })
            : json(whoami, whoamiStatus);
    }
    if (path.endsWith("/admin/users")) {
        const search = (target.searchParams.get("search") ?? "").toLocaleLowerCase();
        userSearches.push(search);
        if (users === null) return new Response(null, { status: 404 });
        return json({
            items: users.filter(user => !search
                || user.display.toLocaleLowerCase().includes(search)
                || user.value.toLocaleLowerCase().includes(search)),
            truncated: false,
        });
    }
    if (path.endsWith("/admin/administrators")) {
        administratorCalls.push({ method, body });
        if (method === "GET") {
            return administrators === null
                ? new Response(null, { status: 404 })
                : json(administrators);
        }
        return new Response(null, { status: 204 });
    }
    if (path === "/admin-api/saved-1" && method === "PUT") {
        savedCalls.push(body);
        return json({ id: "saved-1", title: "Regional" });
    }
    return new Response(null, { status: 404 });
};

await import("../../src/InteractiveReport.Client.Json/Ui/dist/ir-admin.js");

const settle = async condition => {
    for (let attempt = 0; attempt < 120 && !condition(); attempt++)
        await new Promise(resolve => setTimeout(resolve, 10));
};

const asAdministrator = (identity = "admin-user", source = "configuration") => {
    whoami = {
        authenticated: true,
        identity,
        isAdministrator: true,
        administratorSource: source,
        administratorsManagedByApplication: source === "application" || source === "policy",
    };
    whoamiStatus = 200;
};

const directory = [
    { display: "Ada Lovelace", value: "ada-id" },
    { display: "Grace Hopper", value: "grace-id" },
];

const submit = form => form.dispatchEvent(new Event("submit"));

async function mount(language = null) {
    whoamiCalls = 0;
    userSearches = [];
    administratorCalls = [];
    savedCalls = [];
    const admin = document.createElement("interactive-report-admin");
    admin.setAttribute("api-base", "/admin-api");
    if (language) admin.setAttribute("lang", language);
    document.body.append(admin);
    await settle(() => admin.shadowRoot?.querySelector(".ir-admin-bar") && whoamiCalls > 0
        && administratorCalls.length > 0);
    return admin;
}

const toolbarButtons = admin => [...admin.shadowRoot.querySelectorAll(".ir-admin-bar button")]
    .filter(button => !button.hidden)
    .map(button => button.textContent.trim());

test("the administration shell and its embedded report share the selected locale", async () => {
    asAdministrator("administrateur");
    administrators = { configured: ["administrateur"], database: [], managedByApplication: false };
    const admin = await mount("fr-CA");

    assert.deepEqual(toolbarButtons(admin), ["Actualiser", "Téléverser un JSON…", "Administrateurs…"]);
    assert.equal(admin.shadowRoot.querySelector(".ir-admin-count").textContent,
        "Connecté en tant que administrateur");
    assert.equal(admin.shadowRoot.querySelector("interactive-report").getAttribute("lang"), "fr-CA");
    assert.deepEqual(admin.availableReports.map(report => [report.reportName, report.id]), [
        ["orders", 1],
    ], "the admin shell enumerates the root families and then each family document list");

    admin.remove();
    administrators = null;
});

test("owner reassignment searches the directory and applies the picked value", async () => {
    asAdministrator();
    users = directory;
    const admin = await mount();

    await admin.reassign("saved-1", {
        TITLE: "Regional", REPORT_NAME: "orders", OWNER: "grace-id",
    });

    const dialog = admin.shadowRoot.querySelector(".ir-dialog");
    assert.ok(dialog);
    assert.match(dialog.textContent, /Current owner: grace-id/);
    const input = dialog.querySelector('input[type="text"]');
    assert.equal(input.value, "", "the picker starts empty so the directory can be browsed");
    await settle(() => dialog.querySelectorAll(".ir-users-item").length === 2);
    assert.deepEqual(
        [...dialog.querySelectorAll(".ir-users-item")].map(item => item.textContent),
        ["Ada Lovelaceada-id", "Grace Hoppergrace-id"]);

    input.value = "ada";
    input.dispatchEvent(new Event("input"));
    await settle(() => userSearches.length === 2);
    assert.equal(userSearches[1], "ada", "typing searches the server, debounced");
    await settle(() => dialog.querySelectorAll(".ir-users-item").length === 1);

    dialog.querySelector(".ir-users-item").click();
    assert.equal(input.value, "ada-id", "picking fills the identity field with the canonical value");

    submit(dialog.querySelector("form"));
    await settle(() => savedCalls.length === 1);
    assert.deepEqual(savedCalls, [{ owner: "ada-id" }]);
    await settle(() => !admin.shadowRoot.querySelector(".ir-dialog"));

    admin.remove();
    users = null;
});

test("a failed lookup is reported in the dialog and still allows free-form owner entry", async () => {
    asAdministrator();
    users = null;
    const admin = await mount();

    await admin.reassign("saved-1", {
        TITLE: "Regional", REPORT_NAME: "orders", OWNER: "existing-id",
    });

    const dialog = admin.shadowRoot.querySelector(".ir-dialog");
    const errorBox = dialog.querySelector(".ir-dialog-error");
    await settle(() => !errorBox.hidden);
    assert.match(errorBox.textContent, /HTTP 404/, "the lookup failure is shown, not swallowed");
    assert.equal(dialog.querySelectorAll(".ir-users-item").length, 0);
    const input = dialog.querySelector('input[type="text"]');
    input.value = "typed-owner";
    submit(dialog.querySelector("form"));
    await settle(() => savedCalls.length === 1);
    assert.deepEqual(savedCalls, [{ owner: "typed-owner" }]);

    admin.remove();
});

test("the administrators editor edits the database list locally and saves it as a whole", async () => {
    asAdministrator();
    users = directory;
    administrators = { configured: ["ada-id"], database: ["grace-id"], managedByApplication: false };
    const admin = await mount();
    const initialCalls = administratorCalls.length;

    await admin.administratorsDialog();

    const dialog = admin.shadowRoot.querySelector(".ir-dialog");
    assert.ok(dialog);
    assert.deepEqual(administratorCalls.slice(initialCalls).map(call => call.method), ["GET"]);
    await settle(() => dialog.querySelectorAll(".ir-users-item").length === 2);
    assert.match(dialog.textContent, /Ada Lovelace \(ada-id\)/);
    assert.match(dialog.textContent, /Grace Hopper \(grace-id\)/);
    assert.equal(dialog.querySelector('button[aria-label="Remove ada-id"]'), null,
        "configured administrators cannot be removed here");

    dialog.querySelector('button[aria-label="Remove grace-id"]').click();
    assert.equal(dialog.querySelector('button[aria-label="Remove grace-id"]'), null);
    assert.equal(administratorCalls.length, initialCalls + 1, "removal is local until Save");

    const input = dialog.querySelector('input[type="text"]');
    input.value = "new-admin";
    dialog.querySelector(".ir-auth-add button").click();
    assert.ok(dialog.querySelector('button[aria-label="Remove new-admin"]'));
    assert.equal(input.value, "", "a typed identity is consumed by Add");

    dialog.querySelectorAll(".ir-users-item")[1].click();
    assert.ok(dialog.querySelector('button[aria-label="Remove grace-id"]'), "picking adds a directory entry");

    submit(dialog.querySelector("form"));
    await settle(() => administratorCalls.length === initialCalls + 2);
    const saved = administratorCalls.at(-1);
    assert.equal(saved.method, "PUT");
    assert.deepEqual(saved.body, { identities: ["new-admin", "grace-id"] });
    await settle(() => !admin.shadowRoot.querySelector(".ir-dialog"));
    assert.match(admin.shadowRoot.querySelector(".ir-banner-ok").textContent, /Administrator list saved\./);

    admin.remove();
    administrators = null;
    users = null;
});

test("saving a list that drops your own access asks first", async () => {
    asAdministrator("grace-id", "database");
    users = [];
    administrators = { configured: [], database: ["grace-id", "other-id"], managedByApplication: false };
    const admin = await mount();

    await admin.administratorsDialog();

    const dialog = admin.shadowRoot.querySelector(".ir-dialog");
    dialog.querySelector('button[aria-label="Remove grace-id"]').click();
    submit(dialog.querySelector("form"));
    await settle(() => admin.shadowRoot.querySelector("dialog.ir-dialog-modal"));
    const confirmation = admin.shadowRoot.querySelector("dialog.ir-dialog-modal");
    assert.ok(confirmation, "a confirmation opens before the list is saved");
    assert.match(confirmation.textContent, /Remove your own access\?/);
    assert.equal(administratorCalls.filter(call => call.method === "PUT").length, 0);

    submit(confirmation.querySelector("form"));
    await settle(() => administratorCalls.some(call => call.method === "PUT"));
    assert.deepEqual(administratorCalls.find(call => call.method === "PUT").body, {
        identities: ["other-id"],
    });

    admin.remove();
    administrators = null;
    users = null;
});

test("when the application decides administrators the editor is withdrawn", async () => {
    asAdministrator("app-admin", "application");
    administrators = { configured: ["ignored-id"], database: [], managedByApplication: true };
    const admin = await mount();

    assert.deepEqual(toolbarButtons(admin), ["Refresh", "Upload JSON…"],
        "the inert lists are not offered for editing");

    await admin.administratorsDialog();
    assert.equal(admin.shadowRoot.querySelector(".ir-dialog"), null, "no editor opens");
    assert.match(admin.shadowRoot.querySelector(".ir-banner-warn").textContent,
        /The application decides who administers reports/);

    admin.remove();
    administrators = null;
});

test("a disabled whoami endpoint yields packaged guidance instead of a bare listing error", async () => {
    whoami = null;
    whoamiStatus = 404;
    const admin = await mount();

    await settle(() => admin.shadowRoot.querySelector(".ir-banner-warn"));
    const banner = admin.shadowRoot.querySelector(".ir-banner-warn");
    assert.ok(banner, "the whoami-off guidance banner renders");
    assert.match(banner.textContent, /signed-in administrator/);
    assert.match(banner.textContent, /WhoamiEnabled/);
    assert.equal(whoamiCalls, 1,
        "the admin shell and its embedded report coalesce their identity request");

    admin.remove();
});

test("a non-administrator is told how administrators are decided", async () => {
    whoami = {
        authenticated: true,
        identity: "ordinary-user",
        isAdministrator: false,
        administratorSource: "none",
        administratorsManagedByApplication: false,
    };
    whoamiStatus = 200;
    const fallback = await mount();

    await settle(() => fallback.shadowRoot.querySelector(".ir-banner-error"));
    const banner = fallback.shadowRoot.querySelector(".ir-banner-error");
    assert.ok(banner, "the administrator-required banner renders");
    assert.match(banner.textContent, /Add your identity to InteractiveReport:Administrators/);
    assert.equal(fallback.shadowRoot.querySelector(".ir-banner-warn"), null);
    assert.match(fallback.shadowRoot.querySelector(".ir-admin-count").textContent, /ordinary-user/);
    fallback.remove();

    whoami = { ...whoami, administratorsManagedByApplication: true };
    const managed = await mount();
    await settle(() => managed.shadowRoot.querySelector(".ir-banner-error"));
    assert.match(managed.shadowRoot.querySelector(".ir-banner-error").textContent,
        /The application decides who administers reports/);
    managed.remove();
});

test("a real whoami failure presents the server problem and trace reference", async () => {
    whoami = {
        title: "Identity service failed",
        description: "Try again later.",
        traceId: "trace-admin-1",
    };
    whoamiStatus = 500;
    const admin = await mount();

    await settle(() => admin.shadowRoot.querySelector(".ir-banner-error"));
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
