# Working on this adapter

`DESIGN.md` records what was decided and why. `TODO.md` records what is left and what it would cost.
This file records the one thing that decides both.

## It is a storage adapter, not a translator

The word *adapter* invites a wrong reading: that the job is to convert Calcite into Cosmos, operator
by operator, and that a feature is done when the two names line up. That is not the job.

**Cosmos is a storage engine for Calcite.** A caller issues a *Calcite* query. Calcite's semantics
are the contract, and the adapter's whole job is to find the quickest way to retrieve **exactly
that** from Cosmos. Cosmos's functions are not the interface a caller programs against; they are
implementation, a set of capabilities the storage engine happens to offer for narrowing what gets
read.

Two words in that carry the weight.

**Exactly.** The rows and values Calcite's semantics specify — not nearly those. A pushdown that
returns *almost* the right answer is worse than no pushdown at all, and not by a little: whether a
clause pushes is the *planner's* decision, made on cost and container metadata, so an approximation
turns one query into two possible answers depending on which plan won. A slow correct answer is a
performance problem. A fast approximate one is a correctness problem wearing a disguise, and it is
the harder of the two to ever find.

**Quickest.** Subject to that, anything goes. The mapping between what Calcite asked for and what
Cosmos is asked to do need not be structural, and often is not.

## The mapping is not one to one, and does not try to be

The code already reads this way, and the examples are the best statement of the principle:

- `LIKE 'steel%'` renders as `STARTSWITH`, not as `LIKE`. A different function entirely, chosen
  because the index serves it where `LIKE` is a scan.
- SQL `IS NOT NULL` renders as `IS_DEFINED(p) AND NOT IS_NULL(p)` — one operator becoming two,
  because Cosmos distinguishes an absent property from one holding JSON `null` and SQL does not.
- `CosmosFilterSplitRule` pushes a restriction the predicate *implies* and rechecks the predicate
  itself above. What reaches the service is not the predicate at all; it is a bound that cannot
  exclude a row the predicate would keep.
- A partition key is recovered from a conjunction of equalities and used to *route* the request. No
  function is being mapped there; a fact was read out of the predicate and spent somewhere else.
- `SELECT DISTINCT` is emitted as `DISTINCT` rather than as an equivalent `GROUP BY`, because
  Cosmos permits `DISTINCT` with `ORDER BY` and refuses `GROUP BY` with it — measured. The spelling
  is chosen for what it lets the rest of the statement do.

So when a Cosmos function looks unrelated to the Calcite operator being served, that is not a smell.
The question is never *"is this the corresponding function?"* but *"does this get Calcite exactly
what it asked for, sooner?"*

## Where an adapter-specific operator is legitimate

Some Cosmos capabilities have no Calcite counterpart at all — full text search is the standing
example. Exposing those as operators of this adapter's own is the storage engine offering a feature,
and it is legitimate: a caller naming `FULLTEXTCONTAINS` is asking for something SQL cannot say.

What is **not** legitimate is an operator that shadows a name Calcite already defines and gives it a
different meaning. Then a query that reads as standard Calcite silently answers something else, and
the caller has no way to see it. Calcite's spatial library defines `ST_DISTANCE`, `ST_WITHIN`,
`ST_INTERSECTS` and `ST_ISVALID`; ours must not quietly become those. Measured, chaining both
operator tables does not merely shadow — it breaks: with Calcite's first, our predicates stop
validating outright.

The test is the principle again. An operator that *adds* a capability serves the caller. An operator
that *redefines* one takes the contract away.
