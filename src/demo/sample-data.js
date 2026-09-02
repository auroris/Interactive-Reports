// Deterministic sample data generator matching samples/Workbench/SampleData.cs.

const Customers = [
    "Acme Corp", "Globex", "Initech", "Umbrella Group", "Stark Industries", "Wayne Enterprises",
    "Tyrell Corp", "Wonka Industries", "Cyberdyne Systems", "Aperture Science", "Soylent Corp", "Hooli",
];

const Regions = ["NORTH", "SOUTH", "EAST", "WEST"];
const Statuses = ["NEW", "PENDING", "SHIPPED", "CANCELLED"];

/**
 * Simple pseudo-random number generator with fixed seed (matches Random(42) in C#).
 */
class LcgRandom {
    constructor(seed = 42) {
        this.state = seed % 2147483647;
        if (this.state <= 0) this.state += 2147483646;
    }

    next() {
        this.state = (this.state * 16807) % 2147483647;
        return (this.state - 1) / 2147483646;
    }

    nextInt(max) {
        return Math.floor(this.next() * max);
    }
}

/**
 * Creates the ORDERS table and seeds 500 deterministic orders into the SQLite database.
 *
 * @param {object} db
 */
export function seedSampleOrders(db) {
    db.run(`
        CREATE TABLE IF NOT EXISTS ORDERS (
            ORDER_ID   INTEGER PRIMARY KEY AUTOINCREMENT,
            CUSTOMER   TEXT    NOT NULL,
            REGION     TEXT    NOT NULL,
            STATUS     TEXT    NOT NULL,
            AMOUNT     NUMERIC NOT NULL,
            ORDER_DATE TEXT    NOT NULL,
            NOTES      TEXT    NULL
        );
    `);

    const count = db.queryScalar("SELECT COUNT(*) FROM ORDERS");
    if (count > 0) return;

    const rng = new LcgRandom(42);
    const startDate = new Date(Date.UTC(2025, 0, 1));

    for (let i = 0; i < 500; i++) {
        const customer = Customers[rng.nextInt(Customers.length)];
        const region = Regions[rng.nextInt(Regions.length)];
        const status = Statuses[rng.nextInt(Statuses.length)];
        const amount = Math.round((rng.next() * 24990 + 10) * 100) / 100;

        const dayOffset = rng.nextInt(550);
        const orderDate = new Date(startDate.getTime() + dayOffset * 86400000)
            .toISOString()
            .slice(0, 10);

        const notes = rng.nextInt(4) === 0 ? `Priority handling #${i}` : null;

        db.run(
            `INSERT INTO ORDERS (CUSTOMER, REGION, STATUS, AMOUNT, ORDER_DATE, NOTES) VALUES (?, ?, ?, ?, ?, ?)`,
            [customer, region, status, amount, orderDate, notes]
        );
    }
}
