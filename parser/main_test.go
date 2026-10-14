package main

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"math"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/golang/geo/r3"
	st "github.com/markus-wa/demoinfocs-golang/v5/pkg/demoinfocs/sendtables"
)

func TestCorruptDemoNeverPublishesMatchOrComplete(t *testing.T) {
	for _, data := range [][]byte{[]byte("not a demo"), append([]byte("PBDEMS2\x00"), make([]byte, 32)...)} {
		path := filepath.Join(t.TempDir(), "损坏 demo.dem")
		if err := os.WriteFile(path, data, 0600); err != nil {
			t.Fatal(err)
		}
		var out bytes.Buffer
		code := run(context.Background(), []string{"--demo", path}, &out)
		if code == 0 {
			t.Fatal("corrupt input reported success")
		}
		if strings.Contains(out.String(), `"type":"match"`) || strings.Contains(out.String(), `"type":"complete"`) {
			t.Fatalf("published partial match: %s", &out)
		}
		if !strings.Contains(out.String(), `"type":"error"`) {
			t.Fatalf("missing protocol error: %s", &out)
		}
	}
}

// A missing CS2 coordinate field must not be converted by the library getter into a real (0,0,0).
type coordinateFixture struct {
	values map[string]st.PropertyValue
	pos    r3.Vector
}

func (f coordinateFixture) PropertyValue(name string) (st.PropertyValue, bool) {
	v, ok := f.values[name]
	return v, ok
}
func (f coordinateFixture) Position() r3.Vector { return f.pos }
func TestMissingPositionFieldDoesNotInventOrigin(t *testing.T) {
	f := coordinateFixture{values: map[string]st.PropertyValue{}, pos: r3.Vector{}}
	if _, ok := entityPosition(f); ok {
		t.Fatal("missing coordinates interpreted as genuine origin")
	}
	for _, name := range []string{"CBodyComponent.m_cellX", "CBodyComponent.m_cellY", "CBodyComponent.m_cellZ", "CBodyComponent.m_vecX", "CBodyComponent.m_vecY", "CBodyComponent.m_vecZ"} {
		f.values[name] = st.PropertyValue{Any: uint64(0)}
	}
	if pos, ok := entityPosition(f); !ok || pos != (r3.Vector{}) {
		t.Fatal("a recorded real origin is permitted")
	}
	delete(f.values, "CBodyComponent.m_vecZ")
	if _, ok := entityPosition(f); ok {
		t.Fatal("partially missing coordinates admitted")
	}
}

func TestNonFiniteMatchDoesNotPublishPartialProtocol(t *testing.T) {
	var out bytes.Buffer
	m := newMatch()
	m.Rounds = append(m.Rounds, RoundData{StartedAt: math.NaN()})
	w := protocol{enc: json.NewEncoder(&out)}
	if err := w.success(m); err == nil {
		t.Fatal("non-finite data admitted")
	}
	if out.Len() != 0 {
		t.Fatal("partial match published before validating JSON")
	}
}

func TestRestartDiscardsEarlierEventsAndSamples(t *testing.T) {
	c := collector{match: newMatch()}
	roster := []Participation{{PlayerID: "a", Side: "T"}, {PlayerID: "b", Side: "CT"}}
	c.beginRound(1, 1, true, roster)
	c.record(GameEvent{Kind: "kill", Time: 2})
	c.match.Positions = append(c.match.Positions, PositionSample{Round: 1, Time: 2})
	c.beginRound(1, 3, true, roster)
	c.record(GameEvent{Kind: "damage", Time: 4})
	c.endRound(5, "T", []string{"a"})
	if len(c.match.Positions) != 0 || len(c.match.Rounds) != 1 || len(c.match.Rounds[0].Events) != 1 || c.match.Rounds[0].Events[0].Kind != "damage" {
		t.Fatalf("restart contaminated match: %+v", c.match)
	}
}

func TestCompletedRoundsCannotContaminateRestartedMatch(t *testing.T) {
	for _, oldCount := range []int{1, 3} {
		t.Run(fmt.Sprintf("completed-%d", oldCount), func(t *testing.T) {
			c := collector{match: newMatch()}
			roster := []Participation{{PlayerID: "a", Side: "T"}, {PlayerID: "b", Side: "CT"}}
			for n := 1; n <= oldCount; n++ {
				at := float64(n * 3)
				c.beginRound(n, at, true, roster)
				c.record(GameEvent{Kind: "kill", Time: at + 1, Weapon: "OLD_MATCH"})
				c.match.Positions = append(c.match.Positions, PositionSample{Round: n, Time: at + 1, PlayerID: "old-player"})
				c.endRound(at+2, "T", []string{"a"})
			}
			// Reproduce a count rewind even when no explicit reset event was recorded.
			c.beginRound(1, 100, true, roster)
			c.record(GameEvent{Kind: "damage", Time: 101, Weapon: "NEW_MATCH"})
			c.endRound(102, "CT", []string{"b"})
			c.beginRound(2, 103, true, roster)
			c.record(GameEvent{Kind: "shot", Time: 104, Weapon: "NEW_MATCH"})
			c.endRound(105, "CT", []string{"b"})
			if err := validateCoverage(c.match, 2, true); err != nil {
				t.Fatal(err)
			}
			if c.match.Rounds[0].StartedAt != 100 || len(c.match.Positions) != 0 || c.eventCount != 2 {
				t.Fatalf("old match retained: round1=%+v positions=%d events=%d", c.match.Rounds[0], len(c.match.Positions), c.eventCount)
			}
			for _, round := range c.match.Rounds {
				for _, event := range round.Events {
					if event.Weapon != "NEW_MATCH" {
						t.Fatal("old match event published as new game")
					}
				}
			}
		})
	}
}

func TestMissingBombCoordinatesPreserveActorOrNull(t *testing.T) {
	x, y, z := 12.0, 34.0, 56.0
	withActor := GameEvent{X: &x, Y: &y, Z: &z}
	if applyBombPosition(&withActor, nil) {
		t.Fatal("missing bomb evidence treated as recorded")
	}
	if withActor.X == nil || *withActor.X != 12 || *withActor.Y != 34 || *withActor.Z != 56 {
		t.Fatal("default bomb origin overwrote reliable actor")
	}
	absent := GameEvent{}
	applyBombPosition(&absent, nil)
	if absent.X != nil || absent.Y != nil || absent.Z != nil {
		t.Fatal("missing bomb position fabricated origin")
	}
	recorded := coordinateFixture{values: map[string]st.PropertyValue{}, pos: r3.Vector{}}
	for _, name := range []string{"CBodyComponent.m_cellX", "CBodyComponent.m_cellY", "CBodyComponent.m_cellZ", "CBodyComponent.m_vecX", "CBodyComponent.m_vecY", "CBodyComponent.m_vecZ"} {
		recorded.values[name] = st.PropertyValue{Any: uint64(0)}
	}
	if !applyBombPosition(&withActor, recorded) || withActor.X == nil || *withActor.X != 0 || *withActor.Y != 0 || *withActor.Z != 0 {
		t.Fatal("genuine recorded bomb origin lost")
	}
}

func TestCancelledParseIsError(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	var out bytes.Buffer
	if run(ctx, []string{"--demo", "unused.dem"}, &out) == 0 {
		t.Fatal("cancel reported success")
	}
	if strings.Contains(out.String(), `"type":"complete"`) {
		t.Fatal("cancel completed")
	}
}

func TestProtocolUsesVersionAndCamelCaseContract(t *testing.T) {
	var out bytes.Buffer
	m := newMatch()
	m.Players = append(m.Players, PlayerData{ID: "76561198000000000", Name: "名字"})
	w := protocol{enc: json.NewEncoder(&out)}
	if err := w.success(m); err != nil {
		t.Fatal(err)
	}
	lines := strings.Split(strings.TrimSpace(out.String()), "\n")
	if len(lines) != 2 {
		t.Fatalf("want match and complete, got %d", len(lines))
	}
	var first map[string]json.RawMessage
	if err := json.Unmarshal([]byte(lines[0]), &first); err != nil {
		t.Fatal(err)
	}
	if string(first["schemaVersion"]) != "2" || string(first["type"]) != `"match"` {
		t.Fatalf("invalid envelope: %s", lines[0])
	}
	var payload map[string]json.RawMessage
	if err := json.Unmarshal(first["match"], &payload); err != nil {
		t.Fatal(err)
	}
	for _, key := range []string{"schemaVersion", "id", "sourcePath", "sourceSha256", "mapName", "parserVersion", "platform", "importedAt", "warnings", "players", "rounds", "positions", "finalTScore", "finalCTScore"} {
		if _, exists := payload[key]; !exists {
			t.Errorf("missing contract key %s", key)
		}
	}
	if string(payload["finalTScore"]) != "null" || string(payload["finalCTScore"]) != "null" {
		t.Fatal("missing final score must remain unknown, not fabricated zero")
	}
	if string(payload["rounds"]) != "[]" || string(payload["positions"]) != "[]" {
		t.Fatal("empty collections must be arrays")
	}
	if !strings.Contains(lines[0], `"id":"76561198000000000"`) {
		t.Fatal("SteamID must be a string")
	}
	if lines[1] != `{"schemaVersion":2,"type":"complete"}` {
		t.Fatalf("completion envelope: %s", lines[1])
	}
}

func TestLifecycleRejectsWarmupIncompleteAndPostEndEvents(t *testing.T) {
	c := collector{match: newMatch()}
	roster := []Participation{{PlayerID: "a", Side: "T"}, {PlayerID: "b", Side: "CT"}}
	c.beginRound(1, 1, false, roster)
	c.record(GameEvent{Kind: "kill", Time: 2})
	c.endRound(3, "T", []string{"a"})
	c.beginRound(1, 4, true, roster)
	c.record(GameEvent{Kind: "damage", Time: 5, Damage: 20})
	c.endRound(6, "CT", []string{"b"})
	c.record(GameEvent{Kind: "kill", Time: 7})
	c.beginRound(2, 8, true, roster)
	c.record(GameEvent{Kind: "kill", Time: 9})
	c.discardPending("incomplete")
	if len(c.match.Rounds) != 1 {
		t.Fatalf("valid rounds: %d", len(c.match.Rounds))
	}
	r := c.match.Rounds[0]
	if !r.IsValid || len(r.Events) != 1 || r.Events[0].Kind != "damage" || len(r.Participants) != 2 || len(r.Survivors) != 1 {
		t.Fatalf("bad lifecycle: %+v", r)
	}
}

func TestLifecycleInvalidWinnerCannotCompleteRound(t *testing.T) {
	c := collector{match: newMatch()}
	c.beginRound(1, 0, true, []Participation{{PlayerID: "a", Side: "T"}, {PlayerID: "b", Side: "CT"}})
	c.endRound(10, "", nil)
	if len(c.match.Rounds) != 0 {
		t.Fatal("unreliable winner admitted")
	}
}

func TestEndPanelCannotMakePartialRecordingComplete(t *testing.T) {
	for _, numbers := range [][]int{{2, 3}, {1, 3}, {1, 2}} {
		m := newMatch()
		for _, number := range numbers {
			m.Rounds = append(m.Rounds, RoundData{Number: number, IsValid: true})
		}
		if err := validateCoverage(m, 3, true); err == nil {
			t.Fatalf("incomplete rounds %v admitted", numbers)
		}
	}
	m := newMatch()
	for _, number := range []int{1, 2, 3} {
		m.Rounds = append(m.Rounds, RoundData{Number: number, IsValid: true})
	}
	if err := validateCoverage(m, 3, false); err == nil {
		t.Fatal("missing end panel admitted")
	}
	if err := validateCoverage(m, 3, true); err != nil {
		t.Fatalf("complete coverage rejected: %v", err)
	}
}

// Opt-in private regression: demo files remain outside the repository and are never test fixtures.
func TestRealDemoCompleteAndTruncatedRegression(t *testing.T) {
	path := os.Getenv("CS2REVIEW_TEST_DEMO")
	if path == "" {
		t.Skip("set CS2REVIEW_TEST_DEMO to an authorized local complete demo")
	}
	var out bytes.Buffer
	if code := run(context.Background(), []string{"--demo", path}, &out); code != 0 {
		t.Fatalf("complete demo failed: %s", &out)
	}
	var m MatchData
	found := false
	for _, line := range strings.Split(strings.TrimSpace(out.String()), "\n") {
		var envelope struct {
			SchemaVersion int       `json:"schemaVersion"`
			Type          string    `json:"type"`
			Match         MatchData `json:"match"`
		}
		if err := json.Unmarshal([]byte(line), &envelope); err != nil {
			t.Fatal(err)
		}
		if envelope.SchemaVersion != 2 || (envelope.Type == "match" && envelope.Match.SchemaVersion != 2) {
			t.Fatal("wrong version")
		}
		if envelope.Type == "match" {
			m = envelope.Match
			found = true
		}
	}
	if !found || len(m.Rounds) == 0 || len(m.Players) == 0 || len(m.Positions) == 0 || len(m.ID) != 64 {
		t.Fatal("missing complete match evidence")
	}
	if m.FinalTScore == nil || m.FinalCTScore == nil || *m.FinalTScore+*m.FinalCTScore != len(m.Rounds) {
		t.Fatal("missing or inconsistent final scoreboard")
	}
	for _, r := range m.Rounds {
		if !r.IsValid || r.EndedAt <= r.StartedAt || len(r.Participants) == 0 {
			t.Fatalf("invalid published round %d", r.Number)
		}
		for _, e := range r.Events {
			statisticsEnd := r.EndedAt
			if r.StatisticsEndedAt != nil {
				statisticsEnd = *r.StatisticsEndedAt
			}
			if e.Time < r.StartedAt || e.Time > statisticsEnd || e.Damage < 0 || e.ID == "" {
				t.Fatalf("invalid event in round %d", r.Number)
			}
		}
	}
	lastSamples := map[string]float64{}
	for _, sample := range m.Positions {
		key := fmt.Sprintf("%d:%s", sample.Round, sample.PlayerID)
		if previous, exists := lastSamples[key]; exists && sample.Time-previous < 0.499999 {
			t.Fatal("sampling exceeds 2Hz")
		}
		lastSamples[key] = sample.Time
	}
	f, err := os.Open(path)
	if err != nil {
		t.Fatal(err)
	}
	defer f.Close()
	stat, err := f.Stat()
	if err != nil {
		t.Fatal(err)
	}
	truncated := filepath.Join(t.TempDir(), "截断 demo.dem")
	target, err := os.Create(truncated)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = io.CopyN(target, f, stat.Size()/2); err != nil {
		t.Fatal(err)
	}
	target.Close()
	out.Reset()
	if code := run(context.Background(), []string{"--demo", truncated}, &out); code == 0 {
		t.Fatal("half demo reported complete")
	}
	if strings.Contains(out.String(), `"type":"match"`) || strings.Contains(out.String(), `"type":"complete"`) {
		t.Fatal("truncated source published match")
	}
	ctx, cancel := context.WithTimeout(context.Background(), time.Millisecond)
	defer cancel()
	out.Reset()
	if code := run(ctx, []string{"--demo", path}, &out); code == 0 || strings.Contains(out.String(), `"type":"complete"`) {
		t.Fatal("active parsing cancellation reported complete")
	}
}
