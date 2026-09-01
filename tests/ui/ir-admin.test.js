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
let users = null;
let usersStatus = 404;
let usersCalls = 0;
let authorization = null;
let authorizationCalls = [];
const json = (value, status = 200) => new Response(JSON.stringify(value), {
    status,
    headers: { "Content-Type": "application/json" },
});
globalThis.fetch = async (url, options = {}) => {
    const method = options.method ?? "GET";
    if (String(url) === "/admin-api") {
        return json([{ name: "orders", title: "Orders" }]);
    }
    if (String(url) === "/admin-api/orders") {
        return json([{
            id: 1,
            reportName: "orders",
            title: "Default",
            isDefault: true,
            isGlobal: true,
        }]);
    }
    if (String(url).endsWith("/whoami")) {
        whoamiCalls++;
        return whoami === null
            ? new Response(null, { status: whoamiStatus })
            : json(whoami, whoamiStatus);
    }
    if (String(url).endsWith("/admin/users")) {
        usersCalls++;
        return users === null
            ? new Response(null, { status: usersStatus })
            : json(users, usersStatus);
    }
    if (String(url).endsWith("/admin/authorization") && method === "GET") {
        authorizationCalls.push({ url: String(url), method });
        return authorization === null
            ? new Response(null, { status: 404 })
            : json(authorization);
    }
    if (String(url).includes("/admin/authorization/") && method !== "GET") {
        authorizationCalls.push({
            url: String(url), method,
            body: options.body === undefined ? null : JSON.parse(options.body),
        });
        return new Response(null, { status: 204 });
    }
    return new Response(null, { status: 404 });
};

await import("../../src/InteractiveReport.Client.Json/Ui/dist/ir-admin.js");

const settle = async condition => {
    for (let attempt = 0; attempt < 40 && !condition(); attempt++)
        await new Promise(resolve => setTimeout(resolve, 5));
};

async function mount(language = null) {
    whoamiCalls = 0;
    usersCalls = 0;
    authorizationCalls = [];
    const admin = document.createElement("interactive-report-admin");
    admin.setAttribute("api-base", "/admin-api");
    if (language) admin.setAttribute("lang", language);
    document.body.append(admin);
    await settle(() => admin.shadowRoot?.querySelector(".ir-banner"));
    return admin;
}

test("the administration shell and its embedded report share the selected locale", async () => {
    whoami = {
        authenticated: true,
        identity: "administrateur",
        isAdministrator: true,
        administratorListConfigured: true,
        applicationAuthorizationConfigured: false,
    };
    whoamiStatus = 200;
    const admin = await mount("fr-CA");

    const buttons = [...admin.shadowRoot.querySelectorAll(".ir-admin-bar button")]
        .map(button => button.textContent.trim());
    assert.deepEqual(buttons, ["Actualiser", "Téléverser un JSON…", "Autorisation…"]);
    assert.equal(admin.shadowRoot.querySelector(".ir-admin-count").textContent,
        "Connecté en tant que administrateur");
    assert.equal(admin.shadowRoot.querySelector("interactive-report").getAttribute("lang"), "fr-CA");
    assert.deepEqual(admin.availableReports.map(report => [report.reportName, report.id]), [
        ["orders", 1],
    ], "the admin shell enumerates the root families and then each family document list");

    admin.remove();
});

test("authorization editor distinguishes configured and database grants", async () => {
    whoami = {
        authenticated: true,
        identity: "admin-user",
        isAdministrator: true,
        administratorListConfigured: true,
        applicationAuthorizationConfigured: false,
    };
    whoamiStatus = 200;
    users = [
        { display: "Ada Lovelace", value: "ada-id" },
        { display: "Grace Hopper", value: "grace-id" },
    ];
    usersStatus = 200;
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

    await admin.authorizationDialog();

    const dialog = admin.shadowRoot.querySelector(".ir-dialog");
    assert.ok(dialog);
    assert.match(dialog.textContent, /Ada Lovelace \(ada-id\)/);
    assert.match(dialog.textContent, /Grace Hopper \(grace-id\)/);
    assert.match(dialog.textContent, /appsettings\.json/);
    assert.match(dialog.textContent, /administration center/);
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

    admin.remove();
    authorization = null;
    users = null;
    usersStatus = 404;
});

test("owner reassignment uses the application user list as display/value options", async () => {
    whoami = {
        authenticated: true,
        identity: "admin-user",
        isAdministrator: true,
        administratorListConfigured: true,
        applicationAuthorizationConfigured: false,
    };
    whoamiStatus = 200;
    users = [
        { display: "Ada Lovelace", value: "ada-id" },
        { display: "Grace Hopper", value: "grace-id" },
    ];
    usersStatus = 200;
    const admin = await mount();

    await admin.reassign("saved-1", {
        TITLE: "Regional", REPORT_NAME: "orders", OWNER: "grace-id",
    });

    const select = admin.shadowRoot.querySelector(".ir-dialog select");
    assert.ok(select);
    assert.deepEqual([...select.options].map(option => [option.textContent, option.value]), [
        ["Ada Lovelace", "ada-id"],
        ["Grace Hopper", "grace-id"],
    ]);
    assert.equal(select.value, "grace-id");
    assert.equal(usersCalls, 1);

    admin.remove();
});

test("an empty or absent user list retains free-form owner entry", async () => {
    whoami = {
        authenticated: true,
        identity: "admin-user",
        isAdministrator: true,
        administratorListConfigured: true,
        applicationAuthorizationConfigured: false,
    };
    whoamiStatus = 200;
    users = [];
    usersStatus = 200;
    const admin = await mount();

    await admin.reassign("saved-1", {
        TITLE: "Regional", REPORT_NAME: "orders", OWNER: "existing-id",
    });

    const input = admin.shadowRoot.querySelector('.ir-dialog input[type="text"]');
    assert.ok(input);
    assert.equal(input.value, "existing-id");
    assert.equal(admin.shadowRoot.querySelector(".ir-dialog select"), null);

    admin.remove();
    users = null;
    usersStatus = 404;
});

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
