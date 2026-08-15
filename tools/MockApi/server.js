// MockApi: zero-dependency Node server for end-to-end RestVtp testing.
// Run: node server.js [port]      (default 3000)
//
// Matches config/mapping.example.json exactly:
//   GET /v1/customers                 list, wrapped as { "data": [...] }
//     ?city=&active=                  equality filters
//     ?page=&pageSize=                pageNumber paging
//     ?sort=name|-name|createdAt|-createdAt
//   GET /v1/customers/{id}           single record (unwrapped)
//   Auth: X-Api-Key: test-key-123    (send anything else -> 401)

const http = require("http");
const PORT = process.argv[2] || 3000;
const API_KEY = "test-key-123";

const first = ["Ada", "Grace", "Alan", "Edsger", "Barbara", "Donald", "Margaret", "Tony", "Radia", "Linus"];
const cities = ["London", "Manchester", "Leeds", "Bristol", "Cardiff"];

const customers = Array.from({ length: 57 }, (_, i) => ({
  id: i + 1,
  name: `${first[i % first.length]} ${String.fromCharCode(65 + (i % 26))}.`,
  contact: { email: `user${i + 1}@example.com` },
  address: { city: cities[i % cities.length] },
  isActive: i % 4 !== 0,
  balance: Math.round((Math.sin(i) + 1.5) * 1000 * 100) / 100,
  createdAt: new Date(Date.UTC(2025, i % 12, (i % 27) + 1, 9, 30)).toISOString(),
}));

const server = http.createServer((req, res) => {
  const url = new URL(req.url, `http://localhost:${PORT}`);
  const send = (code, body) => {
    res.writeHead(code, { "Content-Type": "application/json" });
    res.end(JSON.stringify(body, null, 2));
  };

  console.log(`${req.method} ${req.url}`);

  if (req.headers["x-api-key"] !== API_KEY)
    return send(401, { error: "missing or invalid X-Api-Key" });

  // GET /v1/customers/{id}
  const single = url.pathname.match(/^\/v1\/customers\/(\d+)$/);
  if (req.method === "GET" && single) {
    const row = customers.find(c => c.id === Number(single[1]));
    return row ? send(200, row) : send(404, { error: "not found" });
  }

  // GET /v1/customers
  if (req.method === "GET" && url.pathname === "/v1/customers") {
    let rows = [...customers];

    const city = url.searchParams.get("city");
    if (city) rows = rows.filter(c => c.address.city.toLowerCase() === city.toLowerCase());

    const active = url.searchParams.get("active");
    if (active !== null && active !== "")
      rows = rows.filter(c => String(c.isActive) === active.toLowerCase());

    const sort = url.searchParams.get("sort");
    if (sort) {
      const desc = sort.startsWith("-");
      const field = desc ? sort.slice(1) : sort;
      const get = c => (field === "createdAt" ? c.createdAt : c[field]);
      rows.sort((a, b) => (get(a) < get(b) ? -1 : get(a) > get(b) ? 1 : 0) * (desc ? -1 : 1));
    }

    const total = rows.length;
    const page = Math.max(parseInt(url.searchParams.get("page") || "1", 10), 1);
    const pageSize = Math.min(Math.max(parseInt(url.searchParams.get("pageSize") || "25", 10), 1), 100);
    rows = rows.slice((page - 1) * pageSize, page * pageSize);

    return send(200, { data: rows, page, pageSize, total });
  }

  send(404, { error: "unknown route" });
});

server.listen(PORT, () =>
  console.log(`MockApi listening on http://localhost:${PORT}/v1/customers  (X-Api-Key: ${API_KEY})`));
