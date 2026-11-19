# Bug Fix: every reading rejected as "ambiguous" while the station answers

- **Slug**: ble-ambiguous-deadlock
- **Fixed**: 2026-11-19
- **Status**: applied

## Symptom

From 12:56 UTC on 2026-11-19, during an outage, every poll ended with
`блок 100×10 / 140×10: ответы остаются неоднозначными, чтение не принято`, alternating between the blocks each
minute. The dashboard showed "станция не отвечает" for 30+ minutes although the station answered every
request within ~40 ms.

## Cause

`Unanswered` keeps requests whose answer never came, so a late answer from a previous connection is not put
into the wrong block (100×10 and 140×10 answer with frames of the same length). Requests the station lost
never get an answer and stayed in the journal (up to 120 s). On an ambiguous frame the old code removed one
foreign entry but re-sent its own request, adding a new entry without retiring the first one. With three
or more stale entries all three asks failed and left three own entries behind, which then blocked the
other block on the next poll: a self-sustaining ping-pong that never healed.

## Fix

`src/ble/mod.rs`: after an ambiguous frame, wait `ReadOptions::settle` (700 ms) for another frame instead of
re-sending at once. If a second frame comes, it is claimed as usual (a late answer followed by ours). If the
station stays silent, the stale requests were lost: every journal entry with the same answer length is
dropped (`Unanswered::settle`) and the block is asked once more on a clean journal.

## Tests

`tests/protocol.rs` (fake station gets `lose`: requests that are never answered):
- `lost_requests_do_not_block_reads_forever` — the 2026-11-19 sequence: 4 lost 140×10 requests, then 4
  reads must all succeed with correct values (fails on the old code).
- `single_lost_request_costs_one_reask` — one lost request costs one re-ask, the next read has none.
- `late_answer_after_lost_requests_still_set_aside` — lost requests plus a real late answer: SoC is not
  taken from the 140×10 block.
- `late_answer_from_previous_read_is_set_aside` — updated: the late answer is now resolved without re-asks.

## Verification

- `cargo test` all green, `cargo clippy --all-targets` clean; the three new tests fail on the previous code.
- Installed 2026-11-19 13:33 UTC; live result below.
