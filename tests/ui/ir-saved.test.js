import assert from "node:assert/strict";
import test from "node:test";
import {
    canManageSaved,
    canRequestAdministration,
    sameTitle,
} from "../../src/client/report/saved.js";

test("saved-title equality is case-insensitive and Unicode-normalization aware", () => {
    assert.equal(sameTitle("Quarterly Café", "quarterly cafe\u0301"), true);
    assert.equal(sameTitle("Quarterly Cafe", "Quarterly Café"), false);
    assert.equal(sameTitle(null, "Quarterly Café"), false);
});

test("administration controls are only hints for eligible authorization modes", () => {
    assert.equal(canRequestAdministration({ whoami: { isAdministrator: true } }), true);
    assert.equal(canRequestAdministration({
        whoami: {
            authenticated: true,
            administratorListConfigured: false,
            applicationAuthorizationConfigured: true,
        },
    }), true);
    assert.equal(canRequestAdministration({
        schema: { authorization: { mayRequestAdministration: true } },
    }), true);
    assert.equal(canRequestAdministration({
        whoami: {
            authenticated: true,
            administratorListConfigured: true,
            applicationAuthorizationConfigured: true,
        },
    }), false);
    assert.equal(canRequestAdministration({
        whoami: {
            authenticated: false,
            administratorListConfigured: false,
            applicationAuthorizationConfigured: true,
        },
    }), false);
});

test("owners can manage their published report without controlling publication flags", () => {
    const owner = { mine: true, isGlobal: true, isPrimary: true, isReadOnly: false };
    assert.equal(canManageSaved({ whoami: null }, owner), true);
    assert.equal(canManageSaved({ whoami: null }, { ...owner, mine: false }), false);
    assert.equal(canManageSaved({ whoami: null }, { ...owner, isReadOnly: true }), false);
});
