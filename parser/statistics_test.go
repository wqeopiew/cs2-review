package main

import (
	"encoding/json"
	"math"
	"testing"
	"time"

	dem "github.com/markus-wa/demoinfocs-golang/v5/pkg/demoinfocs"
	"github.com/markus-wa/demoinfocs-golang/v5/pkg/demoinfocs/common"
	"github.com/markus-wa/demoinfocs-golang/v5/pkg/demoinfocs/events"
)

func scoringFixture() (collector, []Participation) {
	roster := []Participation{{PlayerID: "a", Side: "T"}, {PlayerID: "b", Side: "CT"}}
	c := collector{match: newMatch()}
	c.beginRound(1, 1, true, roster)
	return c, roster
}

func combatFixture(kind string, at float64) GameEvent {
	a, b, ts, cts := "a", "b", "T", "CT"
	return GameEvent{Kind: kind, Time: at, Tick: int(at * 64), ActorID: &a, TargetID: &b, ActorSide: &ts, TargetSide: &cts, Damage: 25}
}

func TestOfficialStatisticsWindowPreservesWinContextAndRoster(t *testing.T) {
	c, roster := scoringFixture()
	c.record(combatFixture("damage", 2))
	c.endRound(6, "T", []string{"a"})
	for _, kind := range []string{"damage", "kill", "flash"} {
		c.record(combatFixture(kind, 7))
	}
	c.record(combatFixture("bomb_planted", 7)) // Do not extend tactical context past the win.
	outsider := combatFixture("kill", 7)
	other := "outsider"
	outsider.TargetID = &other
	c.record(outsider)
	swapped := combatFixture("damage", 7)
	wrongSide := "CT"
	swapped.ActorSide = &wrongSide
	c.record(swapped)
	if len(c.match.Rounds[0].Events) != 1 {
		t.Fatal("unconfirmed combat exposed before official boundary")
	}
	c.endStatistics(8, 1)
	r := c.match.Rounds[0]
	if r.EndedAt != 6 || r.StatisticsEndedAt == nil || *r.StatisticsEndedAt != 8 || r.Winner != "T" || len(r.Survivors) != 1 || r.Survivors[0] != "a" || len(r.Participants) != 2 || len(r.Events) != 4 {
		t.Fatalf("statistics window changed win context or admitted outsiders: %+v", r)
	}
	c.record(combatFixture("kill", 9))
	c.beginRound(2, 10, true, roster)
	c.record(combatFixture("damage", 11))
	c.endRound(12, "CT", []string{"b"})
	if len(c.match.Rounds[0].Events) != 4 || len(c.match.Rounds[1].Events) != 1 || c.match.Rounds[1].Events[0].Time != 11 || c.eventCount != 5 {
		t.Fatal("post-official combat crossed round boundary")
	}
	ids := map[string]bool{}
	for _, round := range c.match.Rounds {
		for _, e := range round.Events {
			if e.ID == "" || ids[e.ID] {
				t.Fatal("missing or repeated event IDs")
			}
			ids[e.ID] = true
		}
	}
}

func TestUnconfirmedStatisticsFallbackRejectsPostEndCombat(t *testing.T) {
	for _, scenario := range []string{"next-round", "eof", "bad-clock", "nonfinite-clock", "wrong-round", "match-finished"} {
		t.Run(scenario, func(t *testing.T) {
			c, roster := scoringFixture()
			c.record(combatFixture("damage", 2))
			c.endRound(6, "T", []string{"a"})
			c.record(combatFixture("kill", 7))
			switch scenario {
			case "next-round":
				c.beginRound(2, 8, true, roster)
			case "eof":
				c.discardStatistics("demo ended")
			case "bad-clock":
				c.endStatistics(5, 1)
			case "nonfinite-clock":
				c.endStatistics(math.NaN(), 1)
			case "wrong-round":
				c.endStatistics(8, 2)
			case "match-finished":
				c.finishMatch()
				c.record(combatFixture("damage", 8))
				c.discardStatistics("demo ended without official event")
			}
			r := c.match.Rounds[0]
			if r.StatisticsEndedAt != nil || len(r.Events) != 1 || r.EndedAt != 6 || c.eventCount != 1 {
				t.Fatalf("fallback invented boundary or retained unconfirmed combat: %+v count=%d", r, c.eventCount)
			}
		})
	}
}

func TestOfficialBoundaryCannotConfirmFutureEventsOrIncompleteRound(t *testing.T) {
	c, _ := scoringFixture()
	c.record(combatFixture("damage", 2))
	c.endStatistics(4, 1)
	if len(c.match.Rounds) != 0 {
		t.Fatal("official end substituted for missing win evidence")
	}
	c.endRound(6, "T", []string{"a"})
	c.record(combatFixture("kill", 10))
	c.endStatistics(8, 1)
	if len(c.match.Rounds[0].Events) != 1 || c.eventCount != 1 {
		t.Fatal("event after recorded official boundary admitted")
	}
}

func TestPostWinCombatAtIdenticalClockNeedsOfficialBoundary(t *testing.T) {
	for _, confirmed := range []bool{true, false} {
		c, _ := scoringFixture()
		c.record(combatFixture("damage", 2))
		c.endRound(6, "T", []string{"a"})
		c.record(combatFixture("kill", 6))
		if confirmed {
			c.endStatistics(6, 1)
		} else {
			c.discardStatistics("no official boundary")
		}
		want := 1
		if confirmed {
			want = 2
		}
		if len(c.match.Rounds[0].Events) != want {
			t.Fatal("same-tick post-win event did not require official evidence")
		}
		pre, _ := json.Marshal(c.match.Rounds[0].Events[0])
		var preFields map[string]any
		_ = json.Unmarshal(pre, &preFields)
		if preFields["afterRoundEnd"] != nil {
			t.Fatal("pre-win combat fabricated post-win order evidence")
		}
		if confirmed {
			post, _ := json.Marshal(c.match.Rounds[0].Events[1])
			var postFields map[string]any
			_ = json.Unmarshal(post, &postFields)
			if postFields["afterRoundEnd"] != true {
				t.Fatal("post-win combat at identical time/tick lacks explicit afterRoundEnd=true")
			}
		}
	}
}

func TestStatisticsBufferDoesNotSurviveWarmupOrRestart(t *testing.T) {
	for _, scenario := range []string{"warmup", "explicit-restart", "count-rewind"} {
		t.Run(scenario, func(t *testing.T) {
			c, roster := scoringFixture()
			c.record(combatFixture("damage", 2))
			c.endRound(6, "T", []string{"a"})
			c.record(combatFixture("kill", 7))
			if scenario == "count-rewind" {
				c.beginRound(1, 10, true, roster)
			} else {
				c.resetMatch(scenario)
				if scenario == "warmup" {
					c.beginRound(1, 8, false, roster)
					c.record(combatFixture("kill", 9))
					c.endStatistics(9, 1)
				}
				c.beginRound(1, 10, true, roster)
			}
			c.record(combatFixture("damage", 11))
			c.endRound(12, "CT", []string{"b"})
			c.endStatistics(13, 1)
			if len(c.match.Rounds) != 1 || c.match.Rounds[0].StartedAt != 10 || len(c.match.Rounds[0].Events) != 1 || c.match.Rounds[0].Events[0].Time != 11 || c.eventCount != 1 {
				t.Fatalf("restart reused earlier statistics: %+v", c.match.Rounds)
			}
		})
	}
}

func TestMatchFinishAndOfficialEndOrderingPreservesReliableEvidence(t *testing.T) {
	for _, officialFirst := range []bool{true, false} {
		t.Run(map[bool]string{true: "official-before-panel", false: "panel-before-official"}[officialFirst], func(t *testing.T) {
			c, roster := scoringFixture()
			c.record(combatFixture("damage", 2))
			c.endRound(6, "T", []string{"a"})
			c.record(combatFixture("kill", 7))
			if officialFirst {
				c.endStatistics(8, 1)
			}
			c.finishMatch()
			c.record(combatFixture("damage", 9))
			if !officialFirst {
				c.endStatistics(10, 1)
			}
			c.beginRound(2, 11, true, roster)
			c.record(combatFixture("kill", 12))
			c.endStatistics(13, 2)
			r := c.match.Rounds[0]
			if r.StatisticsEndedAt == nil || len(r.Events) != 2 || len(c.match.Rounds) != 1 || c.pending != nil || c.eventCount != 2 {
				t.Fatalf("panel discarded reliable evidence or resumed collection: %+v", r)
			}
		})
	}
}

func TestGameOverStopsCombatUntilNewMatch(t *testing.T) {
	c, roster := scoringFixture()
	c.record(combatFixture("damage", 2))
	c.collectionStopped = true
	c.record(combatFixture("kill", 3))
	c.endRound(6, "T", []string{"a"})
	c.record(combatFixture("damage", 7))
	c.endStatistics(8, 1)
	c.beginRound(2, 9, true, roster)
	if len(c.match.Rounds[0].Events) != 1 || c.pending != nil {
		t.Fatal("game-over collection continued")
	}
	c.resetMatch("new match")
	c.beginRound(1, 10, true, roster)
	c.record(combatFixture("damage", 11))
	c.endRound(12, "T", []string{"a"})
	if len(c.match.Rounds) != 1 || len(c.match.Rounds[0].Events) != 1 {
		t.Fatal("new match failed to resume independent collection")
	}
}

type hurtGameStateFixture struct {
	dem.GameState
	tick            int
	started, warmup bool
	phase           common.GamePhase
}

func (s hurtGameStateFixture) IngameTick() int             { return s.tick }
func (s hurtGameStateFixture) IsMatchStarted() bool        { return s.started }
func (s hurtGameStateFixture) IsWarmupPeriod() bool        { return s.warmup }
func (s hurtGameStateFixture) GamePhase() common.GamePhase { return s.phase }

func TestHurtPreservesSameTickDamageAndHealthFacts(t *testing.T) {
	a := &common.Player{SteamID64: 1, Name: "a", Team: common.TeamTerrorists}
	b := &common.Player{SteamID64: 2, Name: "b", Team: common.TeamCounterTerrorists}
	c := collector{match: newMatch()}
	c.beginRound(1, 1, true, []Participation{{PlayerID: "1", Side: "T"}, {PlayerID: "2", Side: "CT"}})
	p := bombParserFixture{state: hurtGameStateFixture{tick: 113271, started: true, phase: common.GamePhaseStartGamePhase}, at: 2 * time.Second}
	c.captureHurt(p, events.PlayerHurt{Attacker: a, Player: b, Health: 5, HealthDamage: 95, HealthDamageTaken: 95})
	c.captureHurt(p, events.PlayerHurt{Attacker: a, Player: b, Health: 0, HealthDamage: 64, HealthDamageTaken: 100})
	c.endRound(3, "T", []string{"1"})
	r := c.match.Rounds[0]
	if len(r.Events) != 2 || r.Events[0].Damage != 95 || r.Events[0].TargetHealthAfter == nil || *r.Events[0].TargetHealthAfter != 5 || r.Events[1].Damage != 64 || r.Events[1].TargetHealthAfter == nil || *r.Events[1].TargetHealthAfter != 0 {
		t.Fatalf("hurt event facts replaced by entity HP: %+v", r.Events)
	}
}

func TestHurtUnreliableHealthRemainsUnknownAndInactiveCombatIsRejected(t *testing.T) {
	a := &common.Player{SteamID64: 1, Team: common.TeamTerrorists}
	b := &common.Player{SteamID64: 2, Team: common.TeamCounterTerrorists}
	for _, health := range []int{-1, 101} {
		c := collector{match: newMatch()}
		c.beginRound(1, 1, true, []Participation{{PlayerID: "1", Side: "T"}, {PlayerID: "2", Side: "CT"}})
		p := bombParserFixture{state: hurtGameStateFixture{tick: 128, started: true, phase: common.GamePhaseStartGamePhase}, at: 2 * time.Second}
		c.captureHurt(p, events.PlayerHurt{Attacker: a, Player: b, Health: health, HealthDamage: 20})
		if len(c.pending.Events) != 1 || c.pending.Events[0].TargetHealthAfter != nil {
			t.Fatal("out-of-range health fabricated a valid value")
		}
	}
	cMissing := collector{match: newMatch()}
	cMissing.beginRound(1, 1, true, []Participation{{PlayerID: "1", Side: "T"}, {PlayerID: "2", Side: "CT"}})
	cMissing.captureHurt(bombParserFixture{state: hurtGameStateFixture{tick: 128, started: true, phase: common.GamePhaseStartGamePhase}, at: 2 * time.Second}, events.PlayerHurt{Attacker: a, Player: nil, Health: 0, HealthDamage: 20})
	if len(cMissing.pending.Events) != 1 || cMissing.pending.Events[0].TargetHealthAfter != nil {
		t.Fatal("missing target evidence fabricated zero health")
	}
	for _, state := range []hurtGameStateFixture{{tick: 128, started: false}, {tick: 128, started: true, warmup: true}, {tick: 128, started: true, phase: common.GamePhaseGameEnded}} {
		c := collector{match: newMatch()}
		c.beginRound(1, 1, true, []Participation{{PlayerID: "1", Side: "T"}, {PlayerID: "2", Side: "CT"}})
		c.captureHurt(bombParserFixture{state: state, at: 2 * time.Second}, events.PlayerHurt{Attacker: a, Player: b, Health: 80, HealthDamage: 20})
		if len(c.pending.Events) != 0 {
			t.Fatal("inactive game emitted combat")
		}
	}
}
