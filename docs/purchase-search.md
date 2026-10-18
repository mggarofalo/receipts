# Purchase search

Universal search previews matching purchases and offers a full table at
`/search?q=hot+dogs`. Reports also links directly to this table. Searches cover
all dates, ordered by receipt date descending, with receipt and item IDs as
deterministic tie breakers. Each row retains its own receipt link, store,
quantity, unit price and line total. Unit prices display up to four decimals
and currency codes are explicit. These are recorded line prices, not normalized
prices per pound, litre or package size.

`GET /api/search/purchases` is an authenticated, additive read contract. The
Application query validates a trimmed phrase of 2–200 characters, a nonnegative
offset, and a page limit of 1–100. Infrastructure projects only the required
columns and applies filtering before counting and pagination. A phrase matches
case-insensitively within a raw description, item code, store, or an active
normalized description's canonical name or display label. SQL wildcard
characters are literal. Inactive canonical labels do not contribute matches;
raw purchase facts remain searchable. Deleted items and deleted parent receipts
are excluded. No grouping or averaging is implied by a match.

The response includes the normalized description ID and its display label
(falling back to canonical name), when active. The display text is not a stable
identity. Count and page are ordinary separate read queries: concurrent writes
can change the result between them. The client offers a first-page recovery for
a page that becomes empty.

The browser stores the submitted query and offset in the URL. Query requests
forward cancellation and never borrow rows from a different search. The palette
waits for debounce and hides old-query results during that wait. Purchase queries
live under the shared reports cache family so receipt, item, and normalization
changes invalidate them through existing local and realtime projection rules.

The old Item Cost Over Time report and its aggregation contracts remain intact.
The unimplemented Basket, Trips and Comparisons shells are hidden by owner
decision; their deferred backlog is not a promise that adding receipts will make
those views appear. Working reports remain available under their existing URLs.

Delivery and acceptance evidence: RECEIPTS-991. Product decisions about browser
photo attachments and a unified export experience remain in RECEIPTS-992.
