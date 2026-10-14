package main

import (
	"encoding/json"
	"fmt"
	"math"
	"os"
	"strings"
	"testing"
	"time"

	"github.com/golang/geo/r3"
	dem "github.com/markus-wa/demoinfocs-golang/v5/pkg/demoinfocs"
	"github.com/markus-wa/demoinfocs-golang/v5/pkg/demoinfocs/common"
	"github.com/markus-wa/demoinfocs-golang/v5/pkg/demoinfocs/events"
	st "github.com/markus-wa/demoinfocs-golang/v5/pkg/demoinfocs/sendtables"
)

type bombParserFixture struct {
	dem.Parser
	state dem.GameState
	at    time.Duration
}

func (p bombParserFixture) GameState() dem.GameState   { return p.state }
func (p bombParserFixture) CurrentTime() time.Duration { return p.at }

type bombGameStateFixture struct {
	dem.GameState
	entities map[int]st.Entity
	tick     int
}

func (g bombGameStateFixture) Entities() map[int]st.Entity { return g.entities }
func (g bombGameStateFixture) IngameTick() int             { return g.tick }

type bombClassFixture struct{ st.ServerClass }

func (bombClassFixture) Name() string { return "CPlantedC4" }

type bombEntityFixture struct {
	st.Entity
	coordinateFixture
	id int
}

func (e bombEntityFixture) ID() int                     { return e.id }
func (e bombEntityFixture) ServerClass() st.ServerClass { return bombClassFixture{} }
func (e bombEntityFixture) PropertyValue(name string) (st.PropertyValue, bool) {
	return e.coordinateFixture.PropertyValue(name)
}
func (e bombEntityFixture) Position() r3.Vector { return e.coordinateFixture.Position() }
func retainedBombFixture(id int) bombEntityFixture {
	f := propertyFixture(map[string]any{"m_nBombSite": int32(0)})
	for _, name := range []string{"CBodyComponent.m_cellX", "CBodyComponent.m_cellY", "CBodyComponent.m_cellZ", "CBodyComponent.m_vecX", "CBodyComponent.m_vecY", "CBodyComponent.m_vecZ"} {
		f.values[name] = st.PropertyValue{Any: uint64(0)}
	}
	f.pos = r3.Vector{X: 100, Y: 200, Z: 300}
	return bombEntityFixture{coordinateFixture: f, id: id}
}

func TestBombActionsRejectRetainedPriorRoundC4(t *testing.T) {
	for _, kind := range []string{"bomb_plant_begin", "bomb_plant_aborted", "bomb_defuse_start", "bomb_defuse_aborted", "bomb_defused", "bomb_exploded"} {
		t.Run(kind, func(t *testing.T) {
			c := collector{match: newMatch()}
			roster := []Participation{{PlayerID: "a", Side: "T"}, {PlayerID: "b", Side: "CT"}}
			id := 17
			c.beginRound(1, 0, true, roster)
			c.record(GameEvent{Kind: "bomb_planted", Tick: 32, Time: 0.5, EntityID: &id})
			c.endRound(1, "T", nil)
			c.beginRound(2, 1, true, roster)
			p := bombParserFixture{state: bombGameStateFixture{entities: map[int]st.Entity{17: retainedBombFixture(17)}, tick: 128}, at: 2 * time.Second}
			c.captureBomb(p, kind, nil, "B")
			e := c.pending.Events[0]
			if e.EntityID != nil || e.X != nil || e.Y != nil || e.Z != nil || e.BombSite == nil || *e.BombSite != "B" {
				t.Fatalf("old C4 contaminated new round: %+v", e)
			}
		})
	}
}

func TestBombActionsRequireCurrentConfirmedPlantLifecycle(t *testing.T) {
	for _, scenario := range []string{"noPlant", "futurePlant", "futureTick", "wrongID", "multipleC4", "currentID", "defused", "exploded", "afterTerminal", "abortWithoutStart", "abortAfterStart", "duplicateAbort", "reset"} {
		t.Run(scenario, func(t *testing.T) {
			c := collector{match: newMatch()}
			roster := []Participation{{PlayerID: "a", Side: "T"}, {PlayerID: "b", Side: "CT"}}
			id := 17
			c.beginRound(1, 0, true, roster)
			kind := "bomb_defuse_start"
			if scenario != "noPlant" {
				at := 1.0
				if scenario == "futurePlant" {
					at = 3
				}
				tick := 64
				if scenario == "futureTick" {
					tick = 256
				}
				c.record(GameEvent{Kind: "bomb_planted", Time: at, Tick: tick, EntityID: &id})
			}
			entities := map[int]st.Entity{17: retainedBombFixture(17)}
			if scenario == "wrongID" {
				entities = map[int]st.Entity{18: retainedBombFixture(18)}
			}
			if scenario == "multipleC4" {
				entities[18] = retainedBombFixture(18)
			}
			if scenario == "afterTerminal" {
				c.record(GameEvent{Kind: "bomb_defused", Time: 1.5, Tick: 96})
			}
			if scenario == "abortWithoutStart" || scenario == "abortAfterStart" {
				kind = "bomb_defuse_aborted"
			}
			if scenario == "abortAfterStart" {
				c.record(GameEvent{Kind: "bomb_defuse_start", Time: 1.5, Tick: 96})
			}
			if scenario == "duplicateAbort" {
				kind = "bomb_defuse_aborted"
				c.record(GameEvent{Kind: "bomb_defuse_start", Time: 1.3, Tick: 80})
				c.record(GameEvent{Kind: "bomb_defuse_aborted", Time: 1.5, Tick: 96})
			}
			if scenario == "defused" {
				kind = "bomb_defused"
			}
			if scenario == "exploded" {
				kind = "bomb_exploded"
			}
			if scenario == "reset" {
				c.resetMatch("fixture")
				c.beginRound(1, 0, true, roster)
			}
			p := bombParserFixture{state: bombGameStateFixture{entities: entities, tick: 128}, at: 2 * time.Second}
			c.captureBomb(p, kind, nil, "")
			e := c.pending.Events[len(c.pending.Events)-1]
			valid := scenario == "currentID" || scenario == "multipleC4" || scenario == "abortAfterStart" || scenario == "defused" || scenario == "exploded"
			if valid {
				if e.EntityID == nil || *e.EntityID != 17 || e.X == nil || *e.X != 100 || e.BombSite == nil || *e.BombSite != "A" {
					t.Fatalf("current plant association lost: %+v", e)
				}
			} else if e.EntityID != nil || e.X != nil || e.BombSite != nil {
				t.Fatalf("unproven lifecycle used entity: %+v", e)
			}
		})
	}
}

type actorPawnProvider struct{ pawn st.Entity }

func (actorPawnProvider) IngameTick() int                              { return 128 }
func (actorPawnProvider) TickRate() float64                            { return 64 }
func (actorPawnProvider) FindPlayerByHandle(uint64) *common.Player     { return nil }
func (actorPawnProvider) FindPlayerByPawnHandle(uint64) *common.Player { return nil }
func (actorPawnProvider) FindWeaponByEntityID(int) *common.Equipment   { return nil }
func (p actorPawnProvider) FindEntityByHandle(uint64) st.Entity        { return p.pawn }
func TestPrePlantPreservesReliableActorPositionIncludingOrigin(t *testing.T) {
	for _, position := range []r3.Vector{{X: 1, Y: 2, Z: 3}, {}} {
		for _, kind := range []string{"bomb_plant_begin", "bomb_plant_aborted"} {
			pawn := retainedBombFixture(21)
			pawn.pos = position
			controller := bombEntityFixture{coordinateFixture: propertyFixture(map[string]any{"m_hPawn": uint64(21), "m_hPlayerPawn": uint64(21)})}
			player := common.NewPlayer(actorPawnProvider{pawn: pawn})
			player.Entity = controller
			player.IsBot = true
			player.UserID = 1
			player.Team = common.TeamTerrorists
			c := collector{match: newMatch()}
			c.beginRound(1, 1, true, []Participation{{PlayerID: "bot:1", Side: "T"}, {PlayerID: "bot:2", Side: "CT"}})
			p := bombParserFixture{state: bombGameStateFixture{entities: map[int]st.Entity{17: retainedBombFixture(17)}, tick: 128}, at: 2 * time.Second}
			c.captureBomb(p, kind, player, "")
			e := c.pending.Events[0]
			if e.EntityID != nil || e.BombSite != nil || e.X == nil || *e.X != position.X || e.Y == nil || *e.Y != position.Y || e.Z == nil || *e.Z != position.Z {
				t.Fatalf("preplant actor coordinate overwritten: %+v", e)
			}
		}
	}
}

func propertyFixture(values map[string]any) coordinateFixture {
	f := coordinateFixture{values: map[string]st.PropertyValue{}}
	for name, value := range values {
		f.values[name] = st.PropertyValue{Any: value}
	}
	return f
}

func TestPlayerStateDistinguishesMissingFromRecordedZeroAndFalse(t *testing.T) {
	missing := stateFromFields("a", "CT", 64, 1, nil, nil, nil)
	if missing == nil || missing.Money != nil || missing.EquipmentValue != nil || missing.Health != nil || missing.Armor != nil || missing.Helmet != nil || missing.DefuseKit != nil || missing.ActiveWeapon != nil {
		t.Fatal("unknown state fabricated zero/false")
	}
	controller := propertyFixture(map[string]any{"m_pInGameMoneyServices.m_iAccount": int32(0)})
	pawn := propertyFixture(map[string]any{"m_unCurrentEquipmentValue": uint64(0), "m_iHealth": int32(0), "m_ArmorValue": int32(0), "m_pItemServices.m_bHasHelmet": false, "m_pItemServices.m_bHasDefuser": false})
	known := stateFromFields("a", "CT", 64, 1, controller, pawn, nil)
	if known == nil || known.Money == nil || *known.Money != 0 || known.EquipmentValue == nil || *known.EquipmentValue != 0 || known.Health == nil || *known.Health != 0 || known.Armor == nil || *known.Armor != 0 || known.Helmet == nil || *known.Helmet || known.DefuseKit == nil || *known.DefuseKit {
		t.Fatalf("recorded zero/false lost: %+v", known)
	}
}

func TestPlayerStateRejectsBadRangesAndKeepsCurrentObservation(t *testing.T) {
	controller := propertyFixture(map[string]any{"m_pInGameMoneyServices.m_iAccount": int32(-1)})
	pawn := propertyFixture(map[string]any{"m_unCurrentEquipmentValue": uint64(3000), "m_unFreezetimeEndEquipmentValue": uint64(1000), "m_iHealth": int32(1001), "m_ArmorValue": int32(-1), "m_pItemServices.m_bHasHelmet": "false"})
	state := stateFromFields("a", "T", 128, 2, controller, pawn, nil)
	if state == nil || state.EquipmentValue == nil || *state.EquipmentValue != 3000 || state.Money != nil || state.Health != nil || state.Armor != nil || state.Helmet != nil {
		t.Fatalf("bad state validation: %+v", state)
	}
	delete(pawn.values, "m_unCurrentEquipmentValue")
	if observed := stateFromFields("a", "T", 128, 2, controller, pawn, nil); observed.EquipmentValue != nil {
		t.Fatal("missing current equipment fell back to stale freeze cache")
	}
	if stateFromFields("a", "T", 128, math.NaN(), controller, pawn, nil) != nil {
		t.Fatal("invalid clock admitted")
	}
	if stateFromFields("a", "", 128, 2, controller, pawn, nil) != nil {
		t.Fatal("unknown side admitted")
	}
}

func TestFreezeContextAndRestartNeverCarryOldEquipment(t *testing.T) {
	c := collector{match: newMatch()}
	roster := []Participation{{PlayerID: "a", Side: "T"}, {PlayerID: "b", Side: "CT"}}
	zero := 0
	timer := 37.0
	states := []PlayerState{{PlayerID: "a", Side: "T", Tick: 128, Time: 2, EquipmentValue: &zero}}
	c.freezeContext(2, states, &timer)
	if c.pending != nil {
		t.Fatal("warmup gained a scoring round")
	}
	c.beginRound(1, 1, true, roster)
	c.freezeContext(2, states, &timer)
	c.endRound(3, "T", []string{"a"})
	r := c.match.Rounds[0]
	if r.FreezeEndedAt == nil || *r.FreezeEndedAt != 2 || r.BombTimerSeconds == nil || *r.BombTimerSeconds != 37 || len(r.EconomySnapshots) != 1 || r.EconomySnapshots[0].EquipmentValue == nil {
		t.Fatalf("missing freeze observation: %+v", r)
	}
	c.beginRound(1, 10, true, roster)
	if c.pending.FreezeEndedAt != nil || c.pending.BombTimerSeconds != nil || len(c.pending.EconomySnapshots) != 0 {
		t.Fatal("restart carried previous equipment/clock")
	}
	badTimer := 0.0
	c.freezeContext(11, []PlayerState{}, &badTimer)
	if c.pending.BombTimerSeconds != nil {
		t.Fatal("missing/invalid rule fabricated clock")
	}
}

func TestBombActionsPreserveUnknownKitAndRecordedNoKit(t *testing.T) {
	c := collector{match: newMatch()}
	c.beginRound(1, 1, true, []Participation{{PlayerID: "a", Side: "T"}, {PlayerID: "b", Side: "CT"}})
	noKit := false
	for _, e := range []GameEvent{{Kind: "bomb_plant_begin", Time: 2}, {Kind: "bomb_plant_aborted", Time: 3}, {Kind: "bomb_defuse_start", Time: 4}, {Kind: "bomb_defuse_aborted", Time: 5}} {
		var kit *bool
		if e.Time == 4 {
			kit = &noKit
		}
		c.bombAction(e, "A", kit)
	}
	c.bombAction(GameEvent{Kind: "bomb_defuse_start", Time: 6}, "?", nil)
	c.endRound(7, "CT", []string{"b"})
	events := c.match.Rounds[0].Events
	if len(events) != 5 || events[0].Kind != "bomb_plant_begin" || events[1].Kind != "bomb_plant_aborted" || events[3].Kind != "bomb_defuse_aborted" {
		t.Fatal("bomb start/abort lifecycle missing")
	}
	if events[2].HasKit == nil || *events[2].HasKit || events[2].BombSite == nil || *events[2].BombSite != "A" {
		t.Fatal("recorded no-kit/site lost")
	}
	if events[4].HasKit != nil || events[4].BombSite != nil {
		t.Fatal("unknown kit/site fabricated")
	}
}

func TestBombTimerRequiresRecordedPlantEvidenceAndRejectsConflicts(t *testing.T) {
	roster := []Participation{{PlayerID: "a", Side: "T"}, {PlayerID: "b", Side: "CT"}}
	for _, value := range []any{nil, float32(0), float32(-1), float32(math.NaN()), float32(601), "40"} {
		c := collector{match: newMatch()}
		c.beginRound(1, 1, true, roster)
		c.observeBombTimer("bomb_planted", propertyFixture(map[string]any{"m_flTimerLength": value}))
		if c.pending.BombTimerSeconds != nil {
			t.Fatalf("invalid recorded timer %v admitted", value)
		}
	}
	c := collector{match: newMatch()}
	c.beginRound(1, 1, true, roster)
	planted := propertyFixture(map[string]any{"m_flTimerLength": float32(37)})
	c.observeBombTimer("bomb_defuse_start", planted)
	if c.pending.BombTimerSeconds != nil {
		t.Fatal("later event backfilled bomb timer")
	}
	c.observeBombTimer("bomb_planted", planted)
	if c.pending.BombTimerSeconds == nil || *c.pending.BombTimerSeconds != 37 {
		t.Fatal("recorded nondefault plant timer lost")
	}
	c.observeBombTimer("bomb_planted", propertyFixture(map[string]any{"m_flTimerLength": float32(40)}))
	if c.pending.BombTimerSeconds != nil {
		t.Fatal("conflicting clocks presented as reliable")
	}
	c.observeBombTimer("bomb_planted", planted)
	if c.pending.BombTimerSeconds != nil {
		t.Fatal("timer conflict silently restored after re-read")
	}
	c.beginRound(2, 10, true, roster)
	c.observeBombTimer("bomb_planted", planted)
	if c.pending.BombTimerSeconds == nil {
		t.Fatal("previous conflict leaked into new round")
	}
}

func TestPlantEntityEvidenceMustMatchSameRoundTickAndClock(t *testing.T) {
	c := collector{match: newMatch()}
	roster := []Participation{{PlayerID: "a", Side: "T"}, {PlayerID: "b", Side: "CT"}}
	c.beginRound(1, 1, true, roster)
	c.record(GameEvent{Kind: "bomb_planted", Tick: 128, Time: 2})
	entity := propertyFixture(map[string]any{"m_flTimerLength": float32(37)})
	c.completePlantEvidence(129, 2, 17, entity)
	if c.pending.BombTimerSeconds != nil {
		t.Fatal("later tick backfilled clock")
	}
	c.completePlantEvidence(128, 3, 17, entity)
	if c.pending.BombTimerSeconds != nil {
		t.Fatal("later clock backfilled timer")
	}
	c.completePlantEvidence(128, 2, 17, entity)
	if c.pending.BombTimerSeconds == nil || *c.pending.BombTimerSeconds != 37 || c.pending.Events[0].EntityID == nil || *c.pending.Events[0].EntityID != 17 {
		t.Fatal("same creation plant evidence not captured")
	}
	c.beginRound(2, 4, true, roster)
	c.completePlantEvidence(128, 2, 17, entity)
	if c.pending.BombTimerSeconds != nil {
		t.Fatal("prior round plant evidence leaked")
	}
}

// Observe the dependency's actual synchronous constructor order, without assuming a fixed timer.
func TestRealDemoPlantConstructorObservationOrder(t *testing.T) {
	path := os.Getenv("CS2REVIEW_TEST_DEMO")
	if path == "" {
		t.Skip("set CS2REVIEW_TEST_DEMO to an authorized local complete demo")
	}
	f, err := os.Open(path)
	if err != nil {
		t.Fatal(err)
	}
	defer f.Close()
	p := dem.NewParser(f)
	defer p.Close()
	c := collector{match: newMatch()}
	p.RegisterEventHandler(func(events.RoundStart) {
		gs := p.GameState()
		c.beginRound(gs.TotalRoundsPlayed()+1, p.CurrentTime().Seconds(), gs.IsMatchStarted() && !gs.IsWarmupPeriod(), nil)
	})
	p.RegisterEventHandler(func(e events.BombPlanted) { c.captureBomb(p, "bomb_planted", e.Player, bombSite(e.Site)) })
	observed := 0
	observerBeforeEnrichment := 0
	p.RegisterEventHandler(func(events.DataTablesParsed) {
		class := p.ServerClasses().FindByName("CPlantedC4")
		if class == nil {
			return
		}
		class.OnEntityCreated(func(entity st.Entity) {
			if c.pending == nil {
				return
			}
			createdTick, createdTime := p.GameState().IngameTick(), p.CurrentTime().Seconds()
			createdRound := c.pending
			alreadyEnriched := false
			for _, event := range c.pending.Events {
				if event.Kind == "bomb_planted" && event.Tick == createdTick && event.Time == createdTime && event.EntityID != nil && *event.EntityID == entity.ID() {
					alreadyEnriched = true
				}
			}
			if !alreadyEnriched {
				observerBeforeEnrichment++
			}
			value, timerExists := observedProperty(entity, "m_flTimerLength")
			// DataTablesParsed dispatcher subscribers have no stable order. Assert at the
			// same constructor's completion, after every class callback and before GS registration.
			entity.OnCreateFinished(func() {
				observed++
				if c.pending != createdRound || p.GameState().IngameTick() != createdTick || p.CurrentTime().Seconds() != createdTime {
					t.Error("plant evidence validation moved beyond its original constructor clock/round")
					return
				}
				found := false
				for _, event := range c.pending.Events {
					if event.Kind == "bomb_planted" && event.Tick == createdTick && event.Time == createdTime && event.EntityID != nil && *event.EntityID == entity.ID() {
						found = true
					}
				}
				if !found {
					t.Error("constructed entity did not enrich same-clock plant event by creation completion")
				}
				if timer, ok := value.(float32); timerExists && ok && finite(float64(timer)) && timer > 0 && timer <= 600 {
					if c.pending.BombTimerSeconds == nil || *c.pending.BombTimerSeconds != float64(timer) {
						t.Error("recorded constructor timer lost")
					}
				}
			})
		})
	})
	// Register the independent observer first to exercise the opposite possible subscriber order.
	c.registerContextEvents(p)
	for {
		more, err := p.ParseNextFrame()
		if err != nil {
			t.Fatal(err)
		}
		if !more {
			break
		}
	}
	if observed == 0 {
		t.Skip("recording has no scoring plant constructors to check")
	}
	t.Logf("verified %d synchronous plant constructors; observer ran before enrichment in %d", observed, observerBeforeEnrichment)
}

func TestInfernoFirePointsRequirePositionAndBurningEvidence(t *testing.T) {
	if points, warning := readFirePoints(nil); points != nil || warning == "" {
		t.Fatal("missing inferno must stay unknown")
	}
	empty := propertyFixture(map[string]any{"m_fireCount": int32(0)})
	if points, warning := readFirePoints(empty); points == nil || len(points) != 0 || warning != "" {
		t.Fatal("recorded zero fires must remain empty array")
	}
	known := propertyFixture(map[string]any{"m_fireCount": int32(1), "m_firePositions.0000": [3]float32{0, 0, 0}, "m_bFireIsBurning.0000": false})
	if points, warning := readFirePoints(known); len(points) != 1 || points[0] != (FirePoint{}) || warning != "" {
		t.Fatalf("genuine zero/unlit fire lost: %+v %s", points, warning)
	}
	delete(known.values, "m_bFireIsBurning.0000")
	if points, warning := readFirePoints(known); points != nil || warning == "" {
		t.Fatal("missing burn field fabricated false")
	}
	known.values["m_bFireIsBurning.0000"] = st.PropertyValue{Any: true}
	known.values["m_firePositions.0000"] = st.PropertyValue{Any: [3]float32{float32(math.NaN()), 0, 0}}
	if points, warning := readFirePoints(known); points != nil || warning == "" {
		t.Fatal("nonfinite fire admitted")
	}
}

func TestInfernoUsesVerifiedOriginOffsetsAndCapsFirePoints(t *testing.T) {
	f := propertyFixture(map[string]any{"m_fireCount": int32(129)})
	for i := 0; i < 129; i++ {
		suffix := fmt.Sprintf("%04d", i)
		f.values["m_firePositions."+suffix] = st.PropertyValue{Any: [3]float32{float32(i), 1, 2}}
		f.values["m_bFireIsBurning."+suffix] = st.PropertyValue{Any: true}
	}
	points, warning := readFirePoints(f)
	if len(points) != 128 || warning == "" {
		t.Fatal("fire cap lost or silently truncated")
	}
	f = propertyFixture(map[string]any{"m_fireCount": int32(1), "m_fireXDelta.0000": int32(1), "m_fireYDelta.0000": int32(2), "m_fireZDelta.0000": int32(3), "m_bFireIsBurning.0000": true})
	if points, _ = readFirePoints(f); points != nil {
		t.Fatal("missing origin fabricated offset coordinates")
	}
	for _, name := range []string{"CBodyComponent.m_cellX", "CBodyComponent.m_cellY", "CBodyComponent.m_cellZ", "CBodyComponent.m_vecX", "CBodyComponent.m_vecY", "CBodyComponent.m_vecZ"} {
		f.values[name] = st.PropertyValue{Any: uint64(0)}
	}
	f.pos = r3.Vector{X: 10, Y: 20, Z: 30}
	points, warning = readFirePoints(f)
	if len(points) != 1 || points[0].X != 11 || points[0].Y != 22 || points[0].Z != 33 || warning != "" {
		t.Fatal("verified fire offsets lost")
	}
}

func TestInfernoExpiryCannotLeakAcrossRoundOrMatchReset(t *testing.T) {
	c := collector{match: newMatch()}
	id := 17
	roster := []Participation{{PlayerID: "a", Side: "T"}, {PlayerID: "b", Side: "CT"}}
	fire := propertyFixture(map[string]any{"m_fireCount": int32(1), "m_firePositions.0000": [3]float32{1, 2, 3}, "m_bFireIsBurning.0000": true})
	c.beginRound(1, 1, true, roster)
	c.recordInferno(GameEvent{Kind: "inferno_start", Time: 2, EntityID: &id}, fire)
	c.recordInferno(GameEvent{Kind: "inferno_expired", Time: 4, EntityID: &id}, fire)
	c.endRound(5, "T", []string{"a"})
	if len(c.match.Rounds[0].Events) != 2 || c.match.Rounds[0].Events[1].Duration != 2 {
		t.Fatal("inferno lifecycle/duration missing")
	}
	c.beginRound(2, 6, true, roster)
	c.recordInferno(GameEvent{Kind: "inferno_expired", Time: 7, EntityID: &id}, fire)
	if len(c.pending.Events) != 0 {
		t.Fatal("prior round fire leaked")
	}
	c.recordInferno(GameEvent{Kind: "inferno_start", Time: 8, EntityID: &id}, fire)
	c.resetMatch("test restart")
	c.beginRound(1, 10, true, roster)
	c.recordInferno(GameEvent{Kind: "inferno_expired", Time: 11, EntityID: &id}, fire)
	if len(c.pending.Events) != 0 {
		t.Fatal("prior match fire leaked")
	}
}

func TestProtocolTwoIncludesNullableContextKeys(t *testing.T) {
	round := RoundData{EconomySnapshots: []PlayerState{}}
	encoded, _ := json.Marshal(round)
	for _, key := range []string{`"freezeEndedAt":null`, `"bombTimerSeconds":null`, `"economySnapshots":[]`} {
		if !strings.Contains(string(encoded), key) {
			t.Errorf("round context missing %s", key)
		}
	}
	encoded, _ = json.Marshal(GameEvent{})
	for _, key := range []string{`"actorState":null`, `"targetState":null`, `"bombSite":null`, `"hasKit":null`, `"entityId":null`, `"firePoints":null`} {
		if !strings.Contains(string(encoded), key) {
			t.Errorf("event context missing %s", key)
		}
	}
}
