import assert from "node:assert/strict";
import test from "node:test";
import { createSqliteDb, InteractiveReportServer, installFetchInterceptor } from "../../src/browser-server/index.js";
import { seedSampleOrders } from "../../src/demo/sample-data.js";

async function setupServer() {
    const db = await createSqliteDb();
    seedSampleOrders(db);

    const server = new InteractiveReportServer(db, { apiPrefix: "/api/reports" });
    server.registerReport({
        name: "orders",
        title: "Orders",
        sql: "SELECT ORDER_ID, CUSTOMER, REGION, STATUS, AMOUNT, ORDER_DATE, NOTES FROM ORDERS",
        columnLabels: {
            ORDER_ID: "Order #",
            CUSTOMER: "Customer Name",
            ORDER_DATE: "Ordered On",
        },
    });

    return { db, server };
}

test("server discovers schema and generates column metadata", async () => {
    const { server } = await setupServer();
    const schema = server.getSchema("orders");

    assert.equal(schema.name, "orders");
    assert.equal(schema.title, "Orders");
    assert.ok(schema.columns.length >= 7);

    const amountCol = schema.columns.find(c => c.name === "AMOUNT");
    assert.ok(amountCol);
    assert.equal(amountCol.type, "number");

    const custCol = schema.columns.find(c => c.name === "CUSTOMER");
    assert.ok(custCol);
    assert.equal(custCol.label, "Customer Name");
    assert.equal(custCol.type, "text");

    assert.ok(schema.capabilities.expressionFunctions.includes("CONTAINS"));
    assert.ok(schema.features.includes("search"));
});

test("server executes basic paged query", async () => {
    const { server } = await setupServer();
    const result = await server.query("orders", {
        page: { index: 1, size: 25 },
    });

    assert.equal(result.rows.length, 25);
    assert.equal(result.totalRows, 500);
    assert.equal(result.page.index, 1);
    assert.equal(result.page.size, 25);
    assert.ok(result.columns.some(c => c.name === "ORDER_ID"));
    assert.ok(result.columns.some(c => c.name === "CUSTOMER"));
});

test("server filters and sorts query results", async () => {
    const { server } = await setupServer();
    const result = await server.query("orders", {
        page: { index: 1, size: 50 },
        tables: {
            base: {
                from: "definition",
                composables: [
                    {
                        kind: "filter",
                        filters: [{ expr: "AMOUNT > 10000 AND REGION = 'NORTH'" }],
                    },
                    {
                        kind: "sort",
                        sorts: [{ col: "AMOUNT", dir: "desc" }],
                    },
                ],
            },
        },
    });

    assert.ok(result.totalRows > 0);
    assert.ok(result.rows.length <= 50);
    for (const row of result.rows) {
        assert.ok(row.AMOUNT > 10000);
        assert.equal(row.REGION, "NORTH");
    }

    // Verify descending order
    for (let i = 1; i < result.rows.length; i++) {
        assert.ok(result.rows[i - 1].AMOUNT >= result.rows[i].AMOUNT);
    }
});

test("server handles toolbar search across text columns", async () => {
    const { server } = await setupServer();
    const result = await server.query("orders", {
        search: "Acme",
        page: { index: 1, size: 50 },
    });

    assert.ok(result.totalRows > 0);
    for (const row of result.rows) {
        const matches = row.CUSTOMER.includes("Acme") || (row.NOTES && row.NOTES.includes("Acme"));
        assert.ok(matches, `Row ${JSON.stringify(row)} should match Acme`);
    }
});

test("server calculates computed columns and evaluates highlights", async () => {
    const { server } = await setupServer();
    const result = await server.query("orders", {
        page: { index: 1, size: 50 },
        tables: {
            base: {
                from: "definition",
                composables: [
                    {
                        kind: "compute",
                        computed: [
                            { id: "TAX", label: "Estimated Tax", expr: "ROUND(AMOUNT * 0.1, 2)" },
                        ],
                    },
                    {
                        kind: "highlight",
                        highlights: [
                            { id: "h_high", scope: "row", expr: "AMOUNT > 20000" },
                            { id: "h_tax", scope: "cell", col: "TAX", expr: "TAX > 2000" },
                        ],
                    },
                ],
            },
        },
    });

    // Computed column exists
    assert.ok(result.availableColumns.some(c => c.name === "TAX"));
    assert.ok(result.rows[0].TAX !== undefined);

    // Highlights returned
    assert.ok(Array.isArray(result.highlights));
    for (const hit of result.highlights) {
        const row = result.rows[hit.row];
        if (hit.id === "h_high") {
            assert.ok(row.AMOUNT > 20000);
            assert.equal(hit.col, null);
        } else if (hit.id === "h_tax") {
            assert.ok(row.TAX > 2000);
            assert.equal(hit.col, "TAX");
        }
    }
});

test("server computes footer aggregates and control break subtotals", async () => {
    const { server } = await setupServer();
    const result = await server.query("orders", {
        page: { index: 1, size: 50 },
        tables: {
            base: {
                from: "definition",
                composables: [
                    { kind: "break", breaks: ["REGION"] },
                    {
                        kind: "aggregate",
                        aggregates: [
                            { col: "AMOUNT", fn: "sum" },
                            { col: "AMOUNT", fn: "avg" },
                        ],
                    },
                ],
            },
        },
    });

    // Aggregates computed
    assert.ok(result.aggregates.AMOUNT);
    assert.ok(result.aggregates.AMOUNT.sum > 0);
    assert.ok(result.aggregates.AMOUNT.avg > 0);

    // Break totals computed
    assert.ok(result.breakTotals.length >= 4);
    for (const bt of result.breakTotals) {
        assert.ok(bt.key.REGION);
        assert.ok(bt.rows > 0);
        assert.ok(bt.aggregates.AMOUNT.sum > 0);
    }
});

test("server supports Group By composables", async () => {
    const { server } = await setupServer();
    const result = await server.query("orders", {
        tables: {
            grouped: {
                from: "definition",
                composables: [
                    {
                        kind: "group",
                        by: ["REGION"],
                        values: [{ id: "TOT_AMT", col: "AMOUNT", fn: "sum" }],
                    },
                ],
            },
        },
        activeTable: "grouped",
    });

    assert.equal(result.rows.length, 4); // 4 regions
    for (const row of result.rows) {
        assert.ok(row.REGION);
        assert.ok(row.__count > 0);
        assert.ok(row.TOT_AMT > 0);
    }
});

test("server supports Pivot composables", async () => {
    const { server } = await setupServer();
    const result = await server.query("orders", {
        tables: {
            pivoted: {
                from: "definition",
                composables: [
                    {
                        kind: "pivot",
                        rows: ["REGION"],
                        cols: ["STATUS"],
                        values: [{ id: "AMT", col: "AMOUNT", fn: "sum" }],
                    },
                ],
            },
        },
        activeTable: "pivoted",
    });

    assert.equal(result.rows.length, 4); // 4 regions
    assert.ok(result.columns.length > 2); // REGION + pivot columns
    const firstRow = result.rows[0];
    assert.ok(firstRow.REGION);
});

test("server executes LOV distinct query", async () => {
    const { server } = await setupServer();
    const lovRes = await server.lov("orders", {
        document: { activeTable: "base", tables: { base: { from: "definition" } } },
        table: "base",
        column: "STATUS",
    });

    assert.equal(lovRes.column, "STATUS");
    assert.deepEqual(lovRes.values.sort(), ["CANCELLED", "NEW", "PENDING", "SHIPPED"]);
    assert.equal(lovRes.truncated, false);
});

test("server exports data to CSV format with UTF-8 BOM", async () => {
    const { server } = await setupServer();
    const csv = await server.export("orders", {
        page: { index: 1, size: 10 },
    });

    assert.ok(csv.startsWith("\uFEFF"));
    assert.ok(csv.includes("Customer Name"));
    assert.ok(csv.includes("Order #"));
});

test("ephemeral saved report store CRUD operations", async () => {
    const { server } = await setupServer();

    // 1. Initial list has Default report
    const initialList = server.savedReports.list("orders");
    assert.equal(initialList.length, 1);
    assert.equal(initialList[0].isDefault, true);

    // 2. Save new report
    const saved = server.savedReports.save("orders", {
        title: "My Custom View",
        state: { search: "Test" },
    });
    assert.ok(saved.id > 1);
    assert.equal(saved.title, "My Custom View");

    // 3. List contains newly saved report
    const listAfter = server.savedReports.list("orders");
    assert.equal(listAfter.length, 2);

    // 4. Load saved report
    const loaded = server.savedReports.load("orders", saved.id);
    assert.equal(loaded.summary.title, "My Custom View");
    assert.deepEqual(loaded.state, { search: "Test" });

    // 5. Update saved report
    const updated = server.savedReports.update(saved.id, { title: "Renamed View" });
    assert.equal(updated.title, "Renamed View");

    // 6. Delete saved report
    const deleted = server.savedReports.delete(saved.id);
    assert.equal(deleted, true);
    assert.equal(server.savedReports.list("orders").length, 1);
});

test("handleRequest handles REST endpoints as standard Response objects", async () => {
    const { server } = await setupServer();

    // GET /whoami
    const whoamiRes = await server.handleRequest("/api/reports/whoami");
    assert.equal(whoamiRes.status, 200);
    const whoamiData = await whoamiRes.json();
    assert.equal(whoamiData.identity, "demo-user");

    // GET /orders/schema
    const schemaRes = await server.handleRequest("/api/reports/orders/schema");
    assert.equal(schemaRes.status, 200);
    const schemaData = await schemaRes.json();
    assert.equal(schemaData.name, "orders");

    // POST /orders/query
    const queryRes = await server.handleRequest("/api/reports/orders/query", {
        method: "POST",
        body: JSON.stringify({ page: { index: 1, size: 5 } }),
    });
    assert.equal(queryRes.status, 200);
    const queryData = await queryRes.json();
    assert.equal(queryData.rows.length, 5);

    // POST /orders/lov
    const lovRes = await server.handleRequest("/api/reports/orders/lov", {
        method: "POST",
        body: JSON.stringify({
            document: { activeTable: "base", tables: { base: { from: "definition" } } },
            table: "base",
            column: "STATUS",
        }),
    });
    assert.equal(lovRes.status, 200);
    const lovData = await lovRes.json();
    assert.equal(lovData.column, "STATUS");

    // GET /orders (saved reports list)
    const savedListRes = await server.handleRequest("/api/reports/orders");
    assert.equal(savedListRes.status, 200);
    const savedListData = await savedListRes.json();
    assert.equal(savedListData[0].isDefault, true);

    // 404 on unknown report
    const notFoundRes = await server.handleRequest("/api/reports/nonexistent/schema");
    assert.equal(notFoundRes.status, 400); // Handled error response
});

test("installFetchInterceptor routes /api/reports calls in-process", async () => {
    const { server } = await setupServer();
    const interceptor = installFetchInterceptor(server, { apiPrefix: "/api/reports" });

    try {
        const res = await fetch("/api/reports/orders/schema");
        assert.equal(res.status, 200);
        const data = await res.json();
        assert.equal(data.name, "orders");
    } finally {
        interceptor.uninstall();
    }
});

test("switching between multiple reports preserves default document for each", async () => {
    const { server } = await setupServer();
    server.registerReport({
        name: "big-orders",
        title: "Big Orders",
        sql: "SELECT ORDER_ID, AMOUNT FROM ORDERS",
    });

    // 1. Initial list for orders has default
    const ordersList1 = await server.handleRequest("/api/reports/orders").then(r => r.json());
    assert.ok(ordersList1.some(r => r.isDefault && r.reportName === "orders"));

    // 2. Switch to big-orders: has its own default
    const bigList = await server.handleRequest("/api/reports/big-orders").then(r => r.json());
    assert.ok(bigList.some(r => r.isDefault && r.reportName === "big-orders"));

    // 3. Switch back to orders: must still have its default report!
    const ordersList2 = await server.handleRequest("/api/reports/orders").then(r => r.json());
    assert.ok(ordersList2.some(r => r.isDefault && r.reportName === "orders"));
    assert.notEqual(ordersList1[0].id, bigList[0].id);
});
