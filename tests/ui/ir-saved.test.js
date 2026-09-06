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

test("administration controls are only hints, taken from whoami or the schema", () => {
    assert.equal(canRequestAdministration({ whoami: { isAdministrator: true } }), true);
    assert.equal(canRequestAdministration({
        schema: { authorization: { mayRequestAdministration: true } },
    }), true);
    assert.equal(canRequestAdministration({
        whoami: { authenticated: true, isAdministrator: false, administratorsManagedByApplication: true },
    }), false);
    assert.equal(canRequestAdministration({ whoami: null, schema: { authorization: {} } }), false);
});

test("owners can manage their published report without controlling publication flags", () => {
    const owner = { mine: true, isGlobal: true, isDefault: true, isReadOnly: false };
    assert.equal(canManageSaved({ whoami: null }, owner), true);
    assert.equal(canManageSaved({ whoami: null }, { ...owner, mine: false }), false);
    assert.equal(canManageSaved({ whoami: null }, { ...owner, isReadOnly: true }), false);
});
