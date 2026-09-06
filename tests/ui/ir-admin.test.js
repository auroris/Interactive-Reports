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
let authorization = null;
let authorizationCalls = [];
let administrators = null;
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
    if (path.endsWith("/admin/authorization/administrators")) {
        administratorCalls.push({ method, body });
        if (method === "GET") {
            return administrators === null
                ? new Response(null, { status: 404 })
                : json(administrators);
        }
        return new Response(null, { status: 204 });
    }
    if (path.endsWith("/admin/authorization") && method === "GET") {
        authorizationCalls.push({ url: path, method });
        return authorization === null
            ? new Response(null, { status: 404 })
            : json(authorization);
    }
    if (path.includes("/admin/authorization/") && method !== "GET") {
        authorizationCalls.push({ url: path, method, body });
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

const asAdministrator = (identity = "admin-user") => {
    whoami = {
        authenticated: true,
        identity,
        isAdministrator: true,
        administratorListConfigured: true,
        applicationAuthorizationConfigured: false,
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
    authorizationCalls = [];
    administratorCalls = [];
    savedCalls = [];
    const admin = document.createElement("interactive-report-admin");
    admin.setAttribute("api-base", "/admin-api");
    if (language) admin.setAttribute("lang", language);
    document.body.append(admin);
    await settle(() => admin.shadowRoot?.querySelector(".ir-admin-bar") && whoamiCalls > 0);
    return admin;
}

test("the administration shell and its embedded report share the selected locale", async () => {
    asAdministrator("administrateur");
    const admin = await mount("fr-CA");

    const buttons = [...admin.shadowRoot.querySelectorAll(".ir-admin-bar button")]
        .map(button => button.textContent.trim());
    assert.deepEqual(buttons, [
        "Actualiser", "Téléverser un JSON…", "Administrateurs…", "Accès aux rapports…",
    ]);
    assert.equal(admin.shadowRoot.querySelector(".ir-admin-count").textContent,
        "Connecté en tant que administrateur");
    assert.equal(admin.shadowRoot.querySelector("interactive-report").getAttribute("lang"), "fr-CA");
    assert.deepEqual(admin.availableReports.map(report => [report.reportName, report.id]), [
        ["orders", 1],
    ], "the admin shell enumerates the root families and then each family document list");

    admin.remove();
});

test("report access editor distinguishes configured and database grants and grants picked users", async () => {
    asAdministrator();
    users = directory;
    authorization = {
        configuredAdministrators: ["ada-id"],
        databaseAdministrators: ["grace-id"],
        reports: [
            {
                name: "configured", title: "Configured report", restricted: true,
                configuredRestricted: true, databaseRestricted: false, canRestrict: true,
                configuredUsers: ["ada-id"], databaseUsers: ["grace-id"],
            },
            {
                name: "database", title: "Database report", restricted: false,
                configuredRestricted: false, databaseRestricted: false, canRestrict: true,
                configuredUsers: [], databaseUsers: [],
            },
        ],
    };
    const admin = await mount();

    await admin.reportAccessDialog();

    const dialog = admin.shadowRoot.querySelector(".ir-dialog");
    assert.ok(dialog);
    await settle(() => dialog.querySelectorAll(".ir-users-item").length === 2);
    assert.deepEqual(userSearches, [""], "the picker browses the directory once when the editor opens");
    // Display names learned from the lookup decorate the grant rows.
    assert.match(dialog.textContent, /Ada Lovelace \(ada-id\)/);
    assert.match(dialog.textContent, /Grace Hopper \(grace-id\)/);
    assert.match(dialog.textContent, /appsettings\.json/);
    assert.match(dialog.textContent, /administration center/);
    assert.doesNotMatch(dialog.textContent, /Administration access/, "administrators moved to their own editor");
    let restriction = dialog.querySelector('.ir-auth-report input[type="checkbox"]');
    assert.equal(restriction.checked, true);
    assert.equal(restriction.disabled, true);

    const reportSelect = dialog.querySelector('select[aria-label="Report"]');
    reportSelect.value = "database";
    reportSelect.dispatchEvent(new Event("change"));
    restriction = dialog.querySelector('.ir-auth-report input[type="checkbox"]');
    assert.equal(restriction.checked, false);
    assert.equal(restriction.disabled, false);

    restriction.checked = true;
    restriction.dispatchEvent(new Event("change"));
    await settle(() => authorizationCalls.some(call => call.method === "PUT"));
    const update = authorizationCalls.find(call => call.method === "PUT");
    assert.match(update.url, /\/admin\/authorization\/reports\/database$/);
    assert.deepEqual(update.body, { restricted: true });

    // Picking a directory entry grants it to the selected report straight away.
    await settle(() => dialog.querySelectorAll(".ir-users-item").length === 2);
    dialog.querySelector(".ir-users-item").click();
    await settle(() => authorizationCalls.some(call => call.method === "POST"));
    const grant = authorizationCalls.find(call => call.method === "POST");
    assert.match(grant.url, /\/admin\/authorization\/reports\/database\/users$/);
    assert.deepEqual(grant.body, { identity: "ada-id" });

    admin.remove();
    authorization = null;
    users = null;
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
    administrators = { configured: ["ada-id"], database: ["grace-id"] };
    const admin = await mount();

    await admin.administratorsDialog();

    const dialog = admin.shadowRoot.querySelector(".ir-dialog");
    assert.ok(dialog);
    assert.deepEqual(administratorCalls.map(call => call.method), ["GET"]);
    await settle(() => dialog.querySelectorAll(".ir-users-item").length === 2);
    assert.match(dialog.textContent, /Ada Lovelace \(ada-id\)/);
    assert.match(dialog.textContent, /Grace Hopper \(grace-id\)/);
    assert.equal(dialog.querySelector('button[aria-label="Remove ada-id"]'), null,
        "configured administrators cannot be removed here");

    dialog.querySelector('button[aria-label="Remove grace-id"]').click();
    assert.equal(dialog.querySelector('button[aria-label="Remove grace-id"]'), null);
    assert.equal(administratorCalls.length, 1, "removal is local until Save");

    const input = dialog.querySelector('input[type="text"]');
    input.value = "new-admin";
    dialog.querySelector(".ir-auth-add button").click();
    assert.ok(dialog.querySelector('button[aria-label="Remove new-admin"]'));
    assert.equal(input.value, "", "a typed identity is consumed by Add");

    dialog.querySelectorAll(".ir-users-item")[1].click();
    assert.ok(dialog.querySelector('button[aria-label="Remove grace-id"]'), "picking adds a directory entry");

    submit(dialog.querySelector("form"));
    await settle(() => administratorCalls.length === 2);
    assert.equal(administratorCalls[1].method, "PUT");
    assert.deepEqual(administratorCalls[1].body, { identities: ["new-admin", "grace-id"] });
    await settle(() => !admin.shadowRoot.querySelector(".ir-dialog"));
    assert.match(admin.shadowRoot.querySelector(".ir-banner-ok").textContent, /Administrator list saved\./);

    admin.remove();
    administrators = null;
    users = null;
});

test("saving a list that drops your own access asks first", async () => {
    asAdministrator("grace-id");
    users = [];
    administrators = { configured: [], database: ["grace-id", "other-id"] };
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

    await settle(() => admin.shadowRoot.querySelector(".ir-banner-error"));
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
