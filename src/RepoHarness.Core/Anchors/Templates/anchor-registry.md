# {{title}}

{{purpose}}

## How the registries work

An anchor is a named piece of deferred work: something known to be wrong, missing or
unfinished, written down where it cannot be forgotten. Code, plans and other documents cite
it by its id, for example `{{example}}`.

Two registries hold every anchor, and each anchor lives in exactly one of them:

- the **pending** registry holds the live anchors: open, gated and disclosed;
- the **done** registry is the archive of closed anchors, and nothing in it is work.

When an anchor closes it **moves** from pending to done, and reopening it moves it back. A
row is relocated, never deleted, so its history survives every change of status.

### Statuses

The Status cell is the only verdict a row carries. Nothing is read from the prose beside it.

- `🟠 OPEN`: live work that can be picked up now.
- `⏳ GATED`: live work waiting on a trigger. When the trigger fires, set the anchor to open.
- `🔵 DISCLOSED`: live debt that existed before anyone wrote it down. A newly disclosed
  anchor does not count against the anchor balance.
- `✅ CLOSED`: finished. Closed anchors live only in the done registry.

### Row shape

Every row has exactly six cells: **Anchor** (the id, in backticks), **Priority** (`P0`, the
most urgent, to `P5`), **Status**, **Trigger** (what is wrong, and what makes it worth
doing), **Closing work** (what remains to close it), and **Cross-refs** (where it is cited,
and related anchors). Rows stay in the order they were added.

An id is permanent: renaming one orphans every citation of it.

### Changing a registry

Do not edit the table by hand. A raw pipe inside a cell silently adds a column, and a wrapped
id disappears from every search; the commands escape, validate and move rows for you.

- `DssHarness write-anchor` adds an anchor.
- `DssHarness set-anchor` changes one, and changing its status moves it between registries.
- `DssHarness read-anchor` shows anchors in full, and `DssHarness read-anchors` lists them.
- `DssHarness check-anchor-balance` confirms a change did not open more anchors than it
  closed.

Run `DssHarness help anchors` for the details.

## Anchors

| Anchor | Priority | Status | Trigger | Closing work | Cross-refs |
|---|---|---|---|---|---|
