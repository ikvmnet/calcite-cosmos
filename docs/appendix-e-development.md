# Appendix E. Development, the emulator and the test suite

## E.1 Running the emulator

The Linux Cosmos DB emulator runs in Docker:

```sh
docker run -d --name cosmos-emu -p 8081:8081 mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-preview
```

Connect to `http://localhost:8081/` with the emulator's well-known key and `"connectionMode":
"gateway"` (Chapter 5).

## E.2 How the emulator differs from Azure

The emulator is convenient and **not a substitute for the service**. It has been found both to accept
statements Azure rejects and to reject features Azure implements:

| | emulator | Azure |
| --- | --- | --- |
| `ORDER BY t` over `JOIN t IN c.tags` | accepted | rejected (400) |
| full text functions and `ORDER BY RANK` | rejected (`SC2005`) | accepted |
| full text policies and indexes | accepted on create, then reported absent | kept |
| composite indexes | discarded on create | kept |
| multi-key `ORDER BY` without a composite index | accepted | rejected (400) |
| a container's document count | always zero | the count, lagging recent writes |
| request charges | not representative | real |

So: develop against the emulator, but verify sorting, full text search, and anything cost-related
against a real account. A plan chosen on the emulator is chosen without row counts (Chapter 6).

## E.3 The sample program

`samples/Apache.Cosmos.Sample` joins a CSV file of suppliers to a Cosmos container of products through
a view, prints each query's plan, the statements sent to Cosmos and what they cost. It needs the
emulator on `localhost:8081`, seeds both sources, and is safe to re-run:

```sh
dotnet run --project samples/Apache.Cosmos.Sample/Apache.Cosmos.Sample.csproj
```

## E.4 Building

```sh
dotnet build Apache.Calcite.Cosmos.slnx
```

The build resolves Calcite through IKVM's Maven support, including an Apache snapshot repository while
the Calcite version the packages use is unreleased (see `Directory.Build.props`).

## E.5 The test suite

Most of the suite plans and renders statements with no service at all. The part that executes against
Cosmos **starts an emulator for itself** where Docker is available and nothing is already listening on
port 8081. Nothing is started unless a test needs an account, so a run of the planner tests never waits
on one.

To use an emulator you started — which is what CI does — start it before the run and the suite finds
it.

To run the same suite against a **real account**, set:

```sh
COSMOS_TEST_ENDPOINT=https://myaccount.documents.azure.com:443/
COSMOS_TEST_KEY=<key>
```

A real account takes precedence over any emulator. Without Docker and without an account, the
service-backed tests report themselves skipped (inconclusive), so the suite stays runnable — but be
aware of what a skipped run does not check: **a pushdown that plans correctly and returns the wrong
rows is invisible without a service.**

### Differential tests

The heart of the service-backed suite is a differential corpus: each statement is planned twice — once
with the adapter's rules, once with every pushdown disabled so Calcite evaluates everything in process
— and both run against the same container. Equal rows, or a defect. Every new pushdown should bring its
statements to the corpus.

### Measurements

Tests under `Measurements/` assert what Calcite, IKVM or the service does — not what the adapter does.
A failure there is news about a dependency, and usually means a recorded assumption in `DESIGN.md`
needs revisiting.

## E.6 Where the reasoning lives

- [`DESIGN.md`](../src/Apache.Calcite.Cosmos.Adapter/DESIGN.md) — why each decision was made, and what
  the service was measured to do.
- [`TODO.md`](../TODO.md) — what is left, sized and reasoned.

---

[← Previous: Cosmos SQL in brief](appendix-d-cosmos-sql.md) · [Contents](README.md) · [Appendix F: Glossary →](appendix-f-glossary.md)
