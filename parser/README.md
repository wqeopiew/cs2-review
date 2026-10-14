# CS2 Review demo parser

Windows CLI: `cs2-parser.exe --demo PATH`. The desktop application starts it with a parameter array. The source demo is opened read-only; no runtime service, game installation or network is required after build.

## Protocol

stdout is schema version **2** NDJSON, using the camelCase contract in `src/CS2Review.Core/Models.cs`. Every progress record includes `progress` in `[0,1]` and a message. A successful process writes one `match` record followed by `complete`, and exits 0. A failure writes `error`, exits nonzero and publishes neither a partial match nor completion. stderr contains a compact final game-state diagnostic; it is not part of the protocol. Parser version is `cs2-review/2 demoinfocs/v5.2.0`; previous schema 1 snapshots have a different parser version and must remain independently readable in the consumer.

The match ID and sourceSha256 are the file SHA-256. SteamIDs are strings; bots without a SteamID use `bot:<userid>`. Unavailable event actors, targets, assist identities and positions are JSON null, not fabricated IDs or coordinates. Nicknames preserve recorded spelling and whitespace. Platform remains unknown when the demo does not establish it. Final T/CT scores are nullable and read from the verified final scoreboard; summing winning sides across a half switch does not produce the final score.

## Reliability and limits

- Locked dependency: demoinfocs-golang **v5.2.0**, verified against two private local complete recordings. v5.0.0 had an upstream unused-import build failure, and v5.0.1 failed the real-demo entity regression. No upstream source, master branch or ignored damaged packets are used.
- Warmup, game-start/reset and missing-end rounds are excluded. Event capture stops immediately at RoundEnd, excluding post-end cleanup. Participants reflect round-start players plus players observed during the round. Survivors are captured at reliable round termination.
- Publishing a complete match requires a match-end panel and contiguous valid rounds from round 1 through the game's total-round count. Partial, corrupt, truncated, unsupported and POV recordings fail conservatively. Reopened/restarted pending rounds discard their events and trajectory samples.
- Events retain the actual game-state tick and parser clock, never demo frame numbers. Damage uses `HealthDamageTaken` (actual health damage, excluding armor and overkill). Team damage/kills and world damage remain distinguishable by recorded identities and sides for Core analysis.
- Position sampling is at most **2 Hz**, limited to **100,000** samples with a visible warning. Missing coordinate/health/angle fields are skipped; genuine recorded zero coordinates remain valid. Positions represent feet, and yaw is in degrees. No geometry or visibility/precise reaction-time claims are derived here.
- At **200,000** events or **64 MiB** serialized match payload, parsing fails rather than silently truncating event statistics. Warnings are deduplicated and limited to 100; protocol messages are bounded to 4 KiB. Serialization is validated before the match is published.
- Cancellation is checked during hashing and between parsing frames. An interrupted or externally killed process cannot emit completion. A caller must require both the completion marker and process exit 0 before storing history.

## Recorded context

Schema 2 retains ordinary event statistics and adds nullable context keys on every round and event. A reliable FreezeEnd captures connected players' current money, equipment value, health, armor, helmet, defuse kit and active weapon. Equipment uses the observed `m_unCurrentEquipmentValue`, never the potentially stale `m_unFreezetimeEndEquipmentValue` cache. Player states on kill, damage and bomb events are observations at the callback time, not states before damage. Property presence, type and range are checked: missing values remain null; recorded zero and false remain usable.

Bomb actions additionally contain `bomb_plant_begin`, `bomb_plant_aborted`, `bomb_defuse_start` and `bomb_defuse_aborted`. Sites are A/B only. `hasKit` is published only on defuse start and only with a recorded player pawn defuser property, without defaulting the upstream event's bool. Round `bombTimerSeconds` comes from a positive recorded rule at FreezeEnd or a recorded positive finite `CPlantedC4.m_flTimerLength` at planting, bounded to 600 seconds. Contradictory clocks remain unknown for that round with a warning. No fixed 40-second fallback or subtraction of server absolute time from demo time is used.

The dependency dispatches BombPlanted inside its entity creation handler before registering the entity in GameState. A subsequent class creation handler associates the constructed entity only with one existing plant event in the same pending round, tick and clock. It reads initial recorded coordinates, site, stable entity ID and timer; it never waits for a later frame or property update to fill planting context. Plant begin/abort retain the actor's verified coordinates and recorded site, without consulting planted entities. Defuse actions and terminal bomb actions can supplement fields only from the current round's confirmed plant ID, with preceding tick/time and an applicable active lifecycle. A defuse abort requires a preceding active defuse start. Terminal actions close the association. Entity uniqueness alone is not evidence; other or previous-round C4 entities cannot supply fields.

`inferno_start` and `inferno_expired` contain the recorded entity ID and verified fire points. Each point requires both a finite position (direct vector or recorded origin plus recorded deltas) and an explicit burning property. Missing fire data is null with a warning, distinct from a recorded empty array. At most 128 verified points are retained with a warning if capped. Expiry requires a start in the same scoring round and a monotonic clock. Resets and round boundaries clear context, and post-end expiry events are excluded. Initial fire points describe that instant only; they do not claim a static area for the whole lifetime. Map geometry and game-version metadata remain unavailable.

## Build and verification

Use Go 1.24 or newer with `GOTOOLCHAIN=local`; the verified local toolchain was Go 1.27.2 Windows x64. Keep GOCACHE, GOPATH and GOMODCACHE inside your ignored tooling directory.

From this directory:

```powershell
go test ./... -count=1
go vet ./...
go build -trimpath -o ../artifacts/parser/cs2-parser.exe .
```

An opt-in private regression uses an authorized local recording supplied through `CS2REVIEW_TEST_DEMO`:

```powershell
$env:CS2REVIEW_TEST_DEMO = $DemoPath
go test ./... -count=1 -v
```

It checks the complete protocol, scoreboard, valid lifecycle and 2 Hz sample spacing, then verifies that a half-length copy and cancellation cannot publish success. A second opt-in regression follows the same per-frame parsing path as the CLI and verifies recorded timer/entity association by the same constructor's completion, with unchanged round, tick and clock. Independent dispatcher subscribers have no guaranteed order, so the assertion uses OnCreateFinished after all class callbacks and before generic GameState entity registration. The temporary truncation is in the test temp directory; personal demos, sample NDJSON, JSON payloads and player data must remain outside Git. Fixture tests cover missing versus recorded zero/false, current versus cached equipment, start/abort/kit observations, clock conflicts, fire-point validity/caps, schema keys and reset boundaries.
