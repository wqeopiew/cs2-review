package main

import (
	"bytes"
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"math"
	"os"
	"os/signal"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"time"

	"github.com/golang/geo/r3"
	st "github.com/markus-wa/demoinfocs-golang/v5/pkg/demoinfocs/sendtables"

	dem "github.com/markus-wa/demoinfocs-golang/v5/pkg/demoinfocs"
	"github.com/markus-wa/demoinfocs-golang/v5/pkg/demoinfocs/common"
	"github.com/markus-wa/demoinfocs-golang/v5/pkg/demoinfocs/events"
	"github.com/markus-wa/demoinfocs-golang/v5/pkg/demoinfocs/msg"
)

type MatchData struct {
	SchemaVersion int              `json:"schemaVersion"`
	ID            string           `json:"id"`
	SourcePath    string           `json:"sourcePath"`
	SourceSHA256  string           `json:"sourceSha256"`
	MapName       string           `json:"mapName"`
	ParserVersion string           `json:"parserVersion"`
	Platform      string           `json:"platform"`
	FinalTScore   *int             `json:"finalTScore"`
	FinalCTScore  *int             `json:"finalCTScore"`
	ImportedAt    time.Time        `json:"importedAt"`
	Warnings      []string         `json:"warnings"`
	Players       []PlayerData     `json:"players"`
	Rounds        []RoundData      `json:"rounds"`
	Positions     []PositionSample `json:"positions"`
}
type PlayerData struct {
	ID   string `json:"id"`
	Name string `json:"name"`
}
type Participation struct {
	PlayerID string `json:"playerId"`
	Side     string `json:"side"`
}
type RoundData struct {
	Number            int             `json:"number"`
	IsValid           bool            `json:"isValid"`
	Winner            string          `json:"winner"`
	StartedAt         float64         `json:"startedAt"`
	EndedAt           float64         `json:"endedAt"`
	StatisticsEndedAt *float64        `json:"statisticsEndedAt"`
	Participants      []Participation `json:"participants"`
	Survivors         []string        `json:"survivors"`
	Events            []GameEvent     `json:"events"`
	FreezeEndedAt     *float64        `json:"freezeEndedAt"`
	BombTimerSeconds  *float64        `json:"bombTimerSeconds"`
	EconomySnapshots  []PlayerState   `json:"economySnapshots"`
}
type GameEvent struct {
	ID                string       `json:"id"`
	Kind              string       `json:"kind"`
	Tick              int          `json:"tick"`
	Time              float64      `json:"time"`
	AfterRoundEnd     *bool        `json:"afterRoundEnd"`
	ActorID           *string      `json:"actorId"`
	TargetID          *string      `json:"targetId"`
	AssisterID        *string      `json:"assisterId"`
	ActorSide         *string      `json:"actorSide"`
	TargetSide        *string      `json:"targetSide"`
	Damage            int          `json:"damage"`
	TargetHealthAfter *int         `json:"targetHealthAfter"`
	Weapon            string       `json:"weapon"`
	Headshot          bool         `json:"headshot"`
	FlashAssist       bool         `json:"flashAssist"`
	Duration          float64      `json:"duration"`
	X                 *float64     `json:"x"`
	Y                 *float64     `json:"y"`
	Z                 *float64     `json:"z"`
	ActorState        *PlayerState `json:"actorState"`
	TargetState       *PlayerState `json:"targetState"`
	BombSite          *string      `json:"bombSite"`
	HasKit            *bool        `json:"hasKit"`
	EntityID          *int         `json:"entityId"`
	FirePoints        []FirePoint  `json:"firePoints"`
}
type PositionSample struct {
	Round    int     `json:"round"`
	Tick     int     `json:"tick"`
	Time     float64 `json:"time"`
	PlayerID string  `json:"playerId"`
	Side     string  `json:"side"`
	X        float64 `json:"x"`
	Y        float64 `json:"y"`
	Z        float64 `json:"z"`
	Yaw      float64 `json:"yaw"`
	Health   int     `json:"health"`
}
type protocol struct{ enc *json.Encoder }

const schemaVersion = 2
const parserVersion = "cs2-review/2.1 demoinfocs/v5.2.0"
const maxPositions = 100000
const maxEvents = 200000
const maxMatchBytes = 64 * 1024 * 1024

func (w *protocol) message(kind, message string, progress *float64) error {
	if len(message) > 4096 {
		message = message[:4096]
	}
	return w.enc.Encode(struct {
		SchemaVersion int      `json:"schemaVersion"`
		Type          string   `json:"type"`
		Progress      *float64 `json:"progress,omitempty"`
		Message       string   `json:"message"`
	}{schemaVersion, kind, progress, message})
}
func (w *protocol) success(m MatchData) error {
	// Serialize before publishing any match, so a size/non-finite error cannot expose a partial payload.
	payload := struct {
		SchemaVersion int       `json:"schemaVersion"`
		Type          string    `json:"type"`
		Match         MatchData `json:"match"`
	}{schemaVersion, "match", m}
	data, err := json.Marshal(payload)
	if err != nil {
		return err
	}
	if len(data) > maxMatchBytes {
		return errors.New("解析结果超过 64 MiB 协议上限")
	}
	if err = w.enc.Encode(json.RawMessage(data)); err != nil {
		return err
	}
	return w.enc.Encode(struct {
		SchemaVersion int    `json:"schemaVersion"`
		Type          string `json:"type"`
	}{schemaVersion, "complete"})
}
func newMatch() MatchData {
	return MatchData{SchemaVersion: schemaVersion, ParserVersion: parserVersion, Platform: "未知", ImportedAt: time.Now().UTC(), Warnings: []string{}, Players: []PlayerData{}, Rounds: []RoundData{}, Positions: []PositionSample{}}
}
func validateCoverage(m MatchData, total int, ended bool) error {
	if !ended {
		return errors.New("未记录比赛结束面板；拒绝将部分录制保存为完整比赛")
	}
	if total <= 0 || len(m.Rounds) != total {
		return fmt.Errorf("计分回合证据不完整：游戏状态记录 %d，可靠回合 %d；拒绝保存为完整比赛", total, len(m.Rounds))
	}
	for i, r := range m.Rounds {
		if !r.IsValid || r.Number != i+1 {
			return errors.New("录制缺少开场或中间计分回合；拒绝保存为完整比赛")
		}
	}
	return nil
}

type collector struct {
	match             MatchData
	pending           *RoundData
	positionStart     int
	eventCount        int
	eventSequence     int
	playerNames       map[string]string
	warningSet        map[string]bool
	nextSample        float64
	fatal             error
	finishedMatch     bool
	collectionStopped bool
	finalized         *roundStatistics
	infernos          map[int]infernoObservation
	bombTimerConflict bool
}

func (c *collector) warn(message string) {
	if c.warningSet == nil {
		c.warningSet = map[string]bool{}
	}
	if !c.warningSet[message] && len(c.match.Warnings) < 100 {
		c.warningSet[message] = true
		c.match.Warnings = append(c.match.Warnings, message)
	}
}
func (c *collector) beginRound(n int, at float64, scoring bool, roster []Participation) {
	if scoring && len(c.match.Rounds) > 0 && n <= c.match.Rounds[len(c.match.Rounds)-1].Number {
		// A scoring count rewind starts a new game, even when the reset event is absent.
		currentNames := map[string]string{}
		for _, participant := range roster {
			if name, ok := c.playerNames[participant.PlayerID]; ok {
				currentNames[participant.PlayerID] = name
			}
		}
		c.resetMatch("计分回合编号回退，作废前一比赛")
		c.playerNames = currentNames
	}
	c.discardStatistics("下一回合前未记录可靠统计结算边界")
	if c.finishedMatch || c.collectionStopped {
		return
	}
	c.discardPending("缺少可靠回合终结，已排除")
	c.infernos = map[int]infernoObservation{}
	c.bombTimerConflict = false
	c.positionStart = len(c.match.Positions)
	if !scoring {
		return
	}
	c.pending = &RoundData{Number: n, StartedAt: at, Participants: []Participation{}, Survivors: []string{}, Events: []GameEvent{}, EconomySnapshots: []PlayerState{}}
	for _, p := range roster {
		c.participate(p)
	}
	c.nextSample = at
}
func (c *collector) resetMatch(reason string) {
	if c.pending != nil || len(c.match.Rounds) > 0 {
		c.warn(reason + "；先前回合、事件与轨迹已清除")
	}
	c.pending = nil
	c.finalized = nil
	c.match.Rounds = []RoundData{}
	c.match.Positions = []PositionSample{}
	c.match.Players = []PlayerData{}
	c.match.FinalTScore = nil
	c.match.FinalCTScore = nil
	c.positionStart = 0
	c.eventCount = 0
	c.eventSequence = 0
	c.nextSample = 0
	c.playerNames = map[string]string{}
	c.finishedMatch = false
	c.collectionStopped = false
	c.bombTimerConflict = false
	c.infernos = map[int]infernoObservation{}
	// Source-level fatal errors and warnings remain sticky across game resets.
}
func (c *collector) participate(p Participation) {
	if c.pending == nil || p.PlayerID == "" || (p.Side != "T" && p.Side != "CT") {
		return
	}
	for _, old := range c.pending.Participants {
		if old.PlayerID == p.PlayerID {
			return
		}
	}
	c.pending.Participants = append(c.pending.Participants, p)
}
func (c *collector) record(e GameEvent) {
	if c.finishedMatch || c.collectionStopped || !finite(e.Time) {
		return
	}
	var round *RoundData
	if c.pending != nil {
		round = c.pending
		e.AfterRoundEnd = nil
		if e.Time < round.StartedAt {
			return
		}
	} else if c.finalized != nil {
		round = &c.match.Rounds[c.finalized.index]
		if e.Time < round.EndedAt || !statisticsCombat(e, round) {
			return
		}
		afterRoundEnd := true
		e.AfterRoundEnd = &afterRoundEnd
	} else {
		return
	}
	if c.eventCount >= maxEvents {
		c.fatal = errors.New("比赛事件超过 200000 上限，拒绝不完整统计")
		return
	}
	c.eventSequence++
	e.ID = fmt.Sprintf("r%d-e%d", round.Number, c.eventSequence)
	if c.pending != nil {
		round.Events = append(round.Events, e)
	} else {
		c.finalized.events = append(c.finalized.events, e)
	}
	c.eventCount++
}
func (c *collector) endRound(at float64, winner string, survivors []string) {
	if c.pending == nil {
		return
	}
	if !finite(at) || at <= c.pending.StartedAt || (winner != "T" && winner != "CT") || len(c.pending.Participants) == 0 {
		c.discardPending("无可靠胜者、时钟或参与者，已排除回合")
		return
	}
	hasT, hasCT := false, false
	for _, p := range c.pending.Participants {
		hasT = hasT || p.Side == "T"
		hasCT = hasCT || p.Side == "CT"
	}
	if !hasT || !hasCT {
		c.discardPending("缺少一方参与者，已排除回合")
		return
	}
	for _, r := range c.match.Rounds {
		if r.Number == c.pending.Number {
			c.discardPending("重复或重开的计分回合，已排除")
			return
		}
	}
	c.pending.IsValid = true
	c.pending.Winner = winner
	c.pending.EndedAt = at
	for _, id := range survivors {
		for _, p := range c.pending.Participants {
			if p.PlayerID == id {
				c.pending.Survivors = append(c.pending.Survivors, id)
				break
			}
		}
	}
	c.match.Rounds = append(c.match.Rounds, *c.pending)
	c.finalized = &roundStatistics{index: len(c.match.Rounds) - 1}
	c.pending = nil
	c.infernos = map[int]infernoObservation{}
}
func (c *collector) discardPending(reason string) {
	if c.pending == nil {
		return
	}
	c.warn(fmt.Sprintf("回合 %d: %s", c.pending.Number, reason))
	c.eventCount -= len(c.pending.Events)
	c.match.Positions = c.match.Positions[:c.positionStart]
	c.pending = nil
	c.infernos = map[int]infernoObservation{}
}
func side(team common.Team) string {
	switch team {
	case common.TeamTerrorists:
		return "T"
	case common.TeamCounterTerrorists:
		return "CT"
	default:
		return ""
	}
}
func playerID(p *common.Player) *string {
	if p == nil || p.IsUnknown {
		return nil
	}
	var id string
	if p.SteamID64 != 0 {
		id = strconv.FormatUint(p.SteamID64, 10)
	} else if p.IsBot {
		id = fmt.Sprintf("bot:%d", p.UserID)
	} else {
		return nil
	}
	return &id
}
func playerSide(p *common.Player) *string {
	if p == nil {
		return nil
	}
	s := side(p.Team)
	if s == "" {
		return nil
	}
	return &s
}
func weaponName(w *common.Equipment) string {
	if w == nil {
		return ""
	}
	return w.Type.String()
}
func (c *collector) remember(p *common.Player) {
	id := playerID(p)
	if id == nil {
		return
	}
	if c.playerNames == nil {
		c.playerNames = map[string]string{}
	}
	c.playerNames[*id] = p.Name
}
func (c *collector) roster(p dem.Parser) []Participation {
	roster := []Participation{}
	for _, player := range p.GameState().Participants().Playing() {
		c.remember(player)
		id := playerID(player)
		s := playerSide(player)
		if id != nil && s != nil && player.IsConnected {
			roster = append(roster, Participation{*id, *s})
		}
	}
	sort.Slice(roster, func(i, j int) bool { return roster[i].PlayerID < roster[j].PlayerID })
	return roster
}
func finite(values ...float64) bool {
	for _, v := range values {
		if math.IsNaN(v) || math.IsInf(v, 0) {
			return false
		}
	}
	return true
}

type coordinateEntity interface {
	PropertyValue(string) (st.PropertyValue, bool)
	Position() r3.Vector
}

func entityPosition(entity coordinateEntity) (r3.Vector, bool) {
	if entity == nil {
		return r3.Vector{}, false
	}
	for _, name := range []string{"CBodyComponent.m_cellX", "CBodyComponent.m_cellY", "CBodyComponent.m_cellZ", "CBodyComponent.m_vecX", "CBodyComponent.m_vecY", "CBodyComponent.m_vecZ"} {
		value, exists := entity.PropertyValue(name)
		if !exists || value.Any == nil {
			return r3.Vector{}, false
		}
	}
	pos := entity.Position()
	return pos, finite(pos.X, pos.Y, pos.Z)
}
func applyBombPosition(event *GameEvent, recorded coordinateEntity) bool {
	v, available := entityPosition(recorded)
	if !available {
		return false
	}
	event.X = &v.X
	event.Y = &v.Y
	event.Z = &v.Z
	return true
}
func (c *collector) event(p dem.Parser, kind string, actor, target *common.Player) GameEvent {
	if (kind == "kill" || kind == "damage" || kind == "flash") && target == nil {
		c.warn("部分事件目标玩家缺失；相应事件仅保留已记录事实")
	}
	if (kind == "shot" || kind == "flash" || kind == "bomb_planted" || kind == "bomb_defused") && actor == nil {
		c.warn("部分事件发起玩家缺失；相应事件仅保留已记录事实")
	}
	c.remember(actor)
	c.remember(target)
	for _, player := range []*common.Player{actor, target} {
		if id, s := playerID(player), playerSide(player); id != nil && s != nil {
			c.participate(Participation{*id, *s})
		}
	}
	e := GameEvent{Kind: kind, Tick: p.GameState().IngameTick(), Time: p.CurrentTime().Seconds(), ActorID: playerID(actor), TargetID: playerID(target), ActorSide: playerSide(actor), TargetSide: playerSide(target)}
	if c.pending != nil && needsPlayerState(kind) {
		e.ActorState = c.playerState(actor, p)
		e.TargetState = c.playerState(target, p)
	}
	located := target
	if located == nil {
		located = actor
	}
	if located != nil && located.Entity != nil && located.PlayerPawnEntity() != nil {
		v, available := entityPosition(located.PlayerPawnEntity())
		if available {
			e.X = &v.X
			e.Y = &v.Y
			e.Z = &v.Z
		}
	}
	return e
}
func (c *collector) sample(p dem.Parser) {
	if c.pending == nil || c.finishedMatch || c.collectionStopped {
		return
	}
	now := p.CurrentTime().Seconds()
	if now < c.nextSample {
		return
	}
	c.nextSample = now + 0.5
	for _, player := range p.GameState().Participants().Playing() {
		id, s := playerID(player), playerSide(player)
		if id == nil || s == nil || !player.IsConnected || player.Entity == nil || player.PlayerPawnEntity() == nil || !player.IsAlive() {
			continue
		}
		c.remember(player)
		c.participate(Participation{*id, *s})
		pawn := player.PlayerPawnEntity()
		v, available := entityPosition(pawn)
		angles, anglesAvailable := pawn.PropertyValue("m_angEyeAngles")
		health, healthAvailable := pawn.PropertyValue("m_iHealth")
		if !available || !anglesAvailable || angles.Any == nil || !healthAvailable || health.Any == nil {
			c.warn("部分轨迹样本缺少已记录坐标、朝向或生命值，已跳过")
			continue
		}
		yaw := float64(player.ViewDirectionX())
		if !finite(now, v.X, v.Y, v.Z, yaw) {
			c.warn("缺少可靠坐标或朝向，已跳过轨迹样本")
			continue
		}
		if len(c.match.Positions) >= maxPositions {
			c.warn("轨迹达到 100000 样本上限，后续轨迹已省略；事件统计保留")
			return
		}
		c.match.Positions = append(c.match.Positions, PositionSample{Round: c.pending.Number, Tick: p.GameState().IngameTick(), Time: now, PlayerID: *id, Side: *s, X: v.X, Y: v.Y, Z: v.Z, Yaw: yaw, Health: player.Health()})
	}
}
func parseDemo(ctx context.Context, path string, w *protocol) (result MatchData, err error) {
	defer func() {
		if recovered := recover(); recovered != nil {
			err = fmt.Errorf("demo 解析异常，文件损坏或版本不兼容: %v", recovered)
		}
	}()
	path, err = filepath.Abs(path)
	if err != nil {
		return result, err
	}
	f, err := os.Open(path)
	if err != nil {
		return result, err
	}
	defer f.Close()
	stat, err := f.Stat()
	if err != nil {
		return result, err
	}
	if stat.Size() < 16 {
		return result, errors.New("demo 文件过短或损坏")
	}
	magic := make([]byte, 8)
	if _, err = io.ReadFull(f, magic); err != nil {
		return result, err
	}
	if !bytes.Equal(magic, []byte("PBDEMS2\x00")) {
		return result, errors.New("仅支持完整 CS2 Source 2 demo (PBDEMS2)，文件损坏或版本不兼容")
	}
	if _, err = f.Seek(0, io.SeekStart); err != nil {
		return result, err
	}
	hash := sha256.New()
	buffer := make([]byte, 1024*1024)
	for {
		if err = ctx.Err(); err != nil {
			return result, err
		}
		n, readErr := f.Read(buffer)
		if n > 0 {
			hash.Write(buffer[:n])
		}
		if readErr == io.EOF {
			break
		}
		if readErr != nil {
			return result, readErr
		}
	}
	if _, err = f.Seek(0, io.SeekStart); err != nil {
		return result, err
	}
	c := collector{match: newMatch()}
	c.match.ID = hex.EncodeToString(hash.Sum(nil))
	c.match.SourceSHA256 = c.match.ID
	c.match.SourcePath = path
	c.warn("轨迹为 2 Hz 降采样，上限 100000；不支持据此推导精确反应时间或可见性")
	p := dem.NewParser(f)
	defer p.Close()
	p.RegisterNetMessageHandler(func(h *msg.CDemoFileHeader) { c.match.MapName = h.GetMapName() })
	p.RegisterNetMessageHandler(func(s *msg.CSVCMsg_ServerInfo) {
		if s.GetMapName() != "" {
			c.match.MapName = s.GetMapName()
		}
	})
	p.RegisterEventHandler(func(events.AnnouncementWinPanelMatch) { c.finishMatch() })
	p.RegisterEventHandler(func(e events.GamePhaseChanged) {
		if e.NewGamePhase == common.GamePhaseGameEnded || e.NewGamePhase == common.GamePhaseGameOver {
			c.collectionStopped = true
		}
	})
	p.RegisterEventHandler(func(e events.MatchStartedChanged) {
		// Half switches preserve the cumulative count. A zero count establishes a new match epoch.
		if e.NewIsStarted && p.GameState().TotalRoundsPlayed() == 0 && (len(c.match.Rounds) > 0 || c.finishedMatch || c.collectionStopped) {
			c.resetMatch("比赛开始状态重置")
		}
	})
	p.RegisterEventHandler(func(events.POVRecordingPlayerDetected) {
		c.fatal = errors.New("POV demo 缺少完整全场事实，无法导入为完整比赛")
	})
	p.RegisterEventHandler(func(e events.ParserWarn) {
		c.warn(fmt.Sprintf("解析库警告 [%d]: %s", e.Type, e.Message))
		if e.Type == events.WarnTypePacketEntitiesPanic || e.Type == events.WarnUnknownDemoCommandMessageType {
			c.fatal = errors.New("解析库发现未知命令或实体损坏，拒绝发布不完整比赛")
		}
	})
	p.RegisterEventHandler(func(events.RoundStart) {
		gs := p.GameState()
		c.beginRound(gs.TotalRoundsPlayed()+1, p.CurrentTime().Seconds(), gs.IsMatchStarted() && !gs.IsWarmupPeriod(), c.roster(p))
	})
	p.RegisterEventHandler(func(events.RoundFreezetimeEnd) {
		gs := p.GameState()
		if c.pending == nil && gs.IsMatchStarted() && !gs.IsWarmupPeriod() {
			c.beginRound(gs.TotalRoundsPlayed()+1, p.CurrentTime().Seconds(), true, c.roster(p))
		}
		for _, player := range c.roster(p) {
			c.participate(player)
		}
		c.captureFreeze(p)
	})
	p.RegisterEventHandler(func(e events.IsWarmupPeriodChanged) {
		if e.NewIsWarmupPeriod {
			c.resetMatch("进入暖身，作废前一比赛")
		}
	})
	p.RegisterEventHandler(func(e events.RoundEnd) {
		if e.Reason == events.RoundEndReasonGameStart || p.GameState().IsWarmupPeriod() {
			c.resetMatch("暖身或比赛重开，作废前一比赛")
			return
		}
		survivors := []string{}
		for _, player := range p.GameState().Participants().Playing() {
			if id := playerID(player); id != nil && player.Entity != nil && player.IsConnected && player.IsAlive() {
				survivors = append(survivors, *id)
			}
		}
		c.endRound(p.CurrentTime().Seconds(), side(e.Winner), survivors)
	})
	p.RegisterEventHandler(func(events.RoundEndOfficial) {
		if p.GameState().IsWarmupPeriod() {
			c.discardStatistics("暖身结算事件不属于正式回合")
			return
		}
		c.endStatistics(p.CurrentTime().Seconds(), p.GameState().TotalRoundsPlayed())
	})
	p.RegisterEventHandler(func(e events.Kill) {
		if !c.acceptsCombat(p) {
			return
		}
		event := c.event(p, "kill", e.Killer, e.Victim)
		c.remember(e.Assister)
		event.AssisterID = playerID(e.Assister)
		event.Weapon = weaponName(e.Weapon)
		event.Headshot = e.IsHeadshot
		event.FlashAssist = e.AssistedFlash
		c.record(event)
	})
	p.RegisterEventHandler(func(e events.PlayerHurt) {
		c.captureHurt(p, e)
	})
	p.RegisterEventHandler(func(e events.PlayerFlashed) {
		if !c.acceptsCombat(p) {
			return
		}
		event := c.event(p, "flash", e.Attacker, e.Player)
		if e.Player != nil {
			event.Duration = e.FlashDuration().Seconds()
		}
		c.record(event)
	})
	p.RegisterEventHandler(func(e events.WeaponFire) {
		if !c.acceptsCombat(p) {
			return
		}
		event := c.event(p, "shot", e.Shooter, nil)
		event.Weapon = weaponName(e.Weapon)
		c.record(event)
	})
	p.RegisterEventHandler(func(e events.BombPlanted) { c.captureBomb(p, "bomb_planted", e.Player, bombSite(e.Site)) })
	p.RegisterEventHandler(func(e events.BombDefused) { c.captureBomb(p, "bomb_defused", e.Player, bombSite(e.Site)) })
	p.RegisterEventHandler(func(e events.BombExplode) { c.captureBomb(p, "bomb_exploded", e.Player, bombSite(e.Site)) })
	c.registerContextEvents(p)
	p.RegisterEventHandler(func(events.FrameDone) { c.sample(p) })
	progressAt := time.Time{}
	for {
		if err = ctx.Err(); err != nil {
			p.Cancel()
			return result, err
		}
		more, parseErr := p.ParseNextFrame()
		if parseErr != nil {
			return result, fmt.Errorf("demo 损坏、截断或不兼容: %w", parseErr)
		}
		if c.fatal != nil {
			return result, c.fatal
		}
		if time.Since(progressAt) >= 500*time.Millisecond {
			// CS2 playback metadata often arrives at the end; byte progress remains useful before that.
			consumed, seekErr := f.Seek(0, io.SeekCurrent)
			if seekErr != nil {
				return result, seekErr
			}
			value := float64(consumed) / float64(stat.Size())
			if !finite(value) || value < 0 {
				value = 0
			}
			if value > 0.99 {
				value = 0.99
			}
			if err = w.message("progress", "正在解析比赛事件", &value); err != nil {
				return result, err
			}
			progressAt = time.Now()
		}
		if !more {
			break
		}
	}
	if err = ctx.Err(); err != nil {
		return result, err
	}
	c.discardPending("录制结束前回合未完成，已排除")
	c.discardStatistics("录制结束前未记录可靠统计结算边界")
	if len(c.match.Rounds) == 0 {
		return result, errors.New("未找到完整有效计分回合")
	}
	if err = validateCoverage(c.match, p.GameState().TotalRoundsPlayed(), c.finishedMatch); err != nil {
		return result, err
	}
	if p.TickRate() <= 0 {
		return result, errors.New("缺少可靠游戏时钟")
	}
	gs := p.GameState()
	if gs.TeamTerrorists() != nil && gs.TeamCounterTerrorists() != nil {
		tScore, ctScore := gs.TeamTerrorists().Score(), gs.TeamCounterTerrorists().Score()
		if tScore >= 0 && ctScore >= 0 && tScore+ctScore == gs.TotalRoundsPlayed() {
			c.match.FinalTScore = &tScore
			c.match.FinalCTScore = &ctScore
		} else {
			c.warn("终局比分与计分回合不一致，比分元数据不可用")
		}
		fmt.Fprintf(os.Stderr, "diagnostic: GS rounds=%d; valid rounds=%d; final current sides T=%d CT=%d; tickRate=%g\n", gs.TotalRoundsPlayed(), len(c.match.Rounds), gs.TeamTerrorists().Score(), gs.TeamCounterTerrorists().Score(), p.TickRate())
	}
	if c.match.MapName == "" {
		c.warn("地图名称缺失")
	}
	for id, name := range c.playerNames {
		c.match.Players = append(c.match.Players, PlayerData{id, name})
	}
	sort.Slice(c.match.Players, func(i, j int) bool { return c.match.Players[i].ID < c.match.Players[j].ID })
	return c.match, nil
}
func run(ctx context.Context, args []string, out io.Writer) int {
	w := protocol{enc: json.NewEncoder(out)}
	fail := func(err error) int {
		// Library errors can contain entire goroutine stacks. Keep the reason legible and bounded.
		reason, _, _ := strings.Cut(err.Error(), "\n")
		_ = w.message("error", reason, nil)
		return 1
	}
	flags := flag.NewFlagSet("cs2-parser", flag.ContinueOnError)
	flags.SetOutput(io.Discard)
	path := flags.String("demo", "", "CS2 demo file")
	if err := flags.Parse(args); err != nil {
		return fail(err)
	}
	if *path == "" || flags.NArg() != 0 {
		return fail(errors.New("用法: cs2-parser.exe --demo PATH"))
	}
	if err := ctx.Err(); err != nil {
		return fail(err)
	}
	zero := 0.0
	if err := w.message("progress", "正在校验 demo 文件", &zero); err != nil {
		return 1
	}
	m, err := parseDemo(ctx, *path, &w)
	if err != nil {
		return fail(err)
	}
	if err = ctx.Err(); err != nil {
		return fail(err)
	}
	if err = w.success(m); err != nil {
		return fail(err)
	}
	return 0
}
func main() {
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt)
	defer stop()
	os.Exit(run(ctx, os.Args[1:], os.Stdout))
}
