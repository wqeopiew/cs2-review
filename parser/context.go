package main

import (
	"fmt"
	"math"
	"strings"

	"github.com/golang/geo/r3"
	dem "github.com/markus-wa/demoinfocs-golang/v5/pkg/demoinfocs"
	"github.com/markus-wa/demoinfocs-golang/v5/pkg/demoinfocs/common"
	"github.com/markus-wa/demoinfocs-golang/v5/pkg/demoinfocs/constants"
	"github.com/markus-wa/demoinfocs-golang/v5/pkg/demoinfocs/events"
	st "github.com/markus-wa/demoinfocs-golang/v5/pkg/demoinfocs/sendtables"
)

type PlayerState struct {
	PlayerID       string  `json:"playerId"`
	Side           string  `json:"side"`
	Tick           int     `json:"tick"`
	Time           float64 `json:"time"`
	Money          *int    `json:"money"`
	EquipmentValue *int    `json:"equipmentValue"`
	Health         *int    `json:"health"`
	Armor          *int    `json:"armor"`
	Helmet         *bool   `json:"helmet"`
	DefuseKit      *bool   `json:"defuseKit"`
	ActiveWeapon   *string `json:"activeWeapon"`
}
type FirePoint struct {
	X         float64 `json:"x"`
	Y         float64 `json:"y"`
	Z         float64 `json:"z"`
	IsBurning bool    `json:"isBurning"`
}
type propertySource interface {
	PropertyValue(string) (st.PropertyValue, bool)
}

func observedProperty(source propertySource, name string) (any, bool) {
	if source == nil {
		return nil, false
	}
	value, ok := source.PropertyValue(name)
	return value.Any, ok && value.Any != nil
}
func integerValue(value any, min, max int64) *int {
	var n int64
	switch value := value.(type) {
	case int:
		n = int64(value)
	case int32:
		n = int64(value)
	case int64:
		n = value
	case uint32:
		n = int64(value)
	case uint64:
		if value > math.MaxInt64 {
			return nil
		}
		n = int64(value)
	default:
		return nil
	}
	if n < min || n > max {
		return nil
	}
	converted := int(n)
	return &converted
}
func observedInt(source propertySource, name string, min, max int64) *int {
	value, ok := observedProperty(source, name)
	if !ok {
		return nil
	}
	return integerValue(value, min, max)
}
func observedBool(source propertySource, name string) *bool {
	value, ok := observedProperty(source, name)
	if !ok {
		return nil
	}
	b, ok := value.(bool)
	if !ok {
		return nil
	}
	return &b
}
func stateFromFields(id, side string, tick int, at float64, controller, pawn propertySource, weapon *string) *PlayerState {
	if id == "" || (side != "T" && side != "CT") || tick < 0 || at < 0 || !finite(at) {
		return nil
	}
	state := &PlayerState{PlayerID: id, Side: side, Tick: tick, Time: at,
		Money: observedInt(controller, "m_pInGameMoneyServices.m_iAccount", 0, 1000000),
		// A FreezeEnd snapshot is still a current-time observation. The saved freeze cache may be stale.
		EquipmentValue: observedInt(pawn, "m_unCurrentEquipmentValue", 0, 1000000),
		Health:         observedInt(pawn, "m_iHealth", 0, 1000), Armor: observedInt(pawn, "m_ArmorValue", 0, 1000),
		Helmet: observedBool(pawn, "m_pItemServices.m_bHasHelmet"), DefuseKit: observedBool(pawn, "m_pItemServices.m_bHasDefuser"),
	}
	handle := observedInt(pawn, "m_pWeaponServices.m_hActiveWeapon", 0, math.MaxUint32)
	if handle != nil && uint64(*handle) != constants.InvalidEntityHandleSource2 && weapon != nil && *weapon != "" {
		state.ActiveWeapon = weapon
	}
	return state
}
func (c *collector) playerState(player *common.Player, p dem.Parser) *PlayerState {
	id, s := playerID(player), playerSide(player)
	if id == nil || s == nil {
		return nil
	}
	var controller, pawn propertySource
	var weapon *string
	if player.Entity != nil {
		controller = player.Entity
		if entity := player.PlayerPawnEntity(); entity != nil {
			pawn = entity
			if handle := observedInt(pawn, "m_pWeaponServices.m_hActiveWeapon", 0, math.MaxUint32); handle != nil && uint64(*handle) != constants.InvalidEntityHandleSource2 {
				if active := player.ActiveWeapon(); active != nil && active.Type != common.EqUnknown {
					name := active.Type.String()
					weapon = &name
				}
			}
		}
	}
	state := stateFromFields(*id, *s, p.GameState().IngameTick(), p.CurrentTime().Seconds(), controller, pawn, weapon)
	if state != nil && (state.Money == nil || state.EquipmentValue == nil || state.Health == nil || state.Armor == nil || state.Helmet == nil || state.DefuseKit == nil) {
		c.warn("部分玩家状态字段缺失或超出支持范围；对应经济、生命或装备值保持未知")
	}
	return state
}
func (c *collector) freezeContext(at float64, states []PlayerState, timer *float64) {
	if c.pending == nil || at < c.pending.StartedAt || !finite(at) || c.pending.FreezeEndedAt != nil {
		return
	}
	c.pending.FreezeEndedAt = &at
	c.pending.EconomySnapshots = []PlayerState{}
	for _, state := range states {
		if state.PlayerID == "" || (state.Side != "T" && state.Side != "CT") || state.Tick < 0 || !finite(state.Time) || math.Abs(state.Time-at) > 0.000001 {
			continue
		}
		c.participate(Participation{state.PlayerID, state.Side})
		c.pending.EconomySnapshots = append(c.pending.EconomySnapshots, state)
	}
	if timer != nil && finite(*timer) && *timer > 0 && *timer <= 600 {
		value := *timer
		c.pending.BombTimerSeconds = &value
	} else {
		c.warn("冻结结束时炸弹规则时长未知；仅当下包创建实体记录可靠时长时才可补充")
	}
	if len(c.pending.EconomySnapshots) == 0 {
		c.warn("可靠冻结结束事件未提供玩家装备快照")
	}
}
func (c *collector) captureFreeze(p dem.Parser) {
	gs := p.GameState()
	if c.pending == nil || gs.IsWarmupPeriod() || !gs.IsMatchStarted() {
		return
	}
	states := []PlayerState{}
	for _, player := range gs.Participants().Playing() {
		if player == nil || !player.IsConnected {
			continue
		}
		c.remember(player)
		if state := c.playerState(player, p); state != nil {
			states = append(states, *state)
		}
	}
	var timer *float64
	if duration, err := gs.Rules().BombTime(); err == nil {
		seconds := duration.Seconds()
		timer = &seconds
	}
	c.freezeContext(p.CurrentTime().Seconds(), states, timer)
}
func (c *collector) bombAction(event GameEvent, site string, kit *bool) {
	if site == "A" || site == "B" {
		event.BombSite = &site
	} else {
		event.BombSite = nil
	}
	if event.Kind == "bomb_defuse_start" {
		event.HasKit = kit
		if kit == nil && c.pending != nil {
			c.warn("拆包开始时缺少已记录有钳字段；hasKit保持未知")
		}
	} else {
		event.HasKit = nil
	}
	c.record(event)
}
func (c *collector) observeBombTimer(kind string, entity propertySource) {
	if kind != "bomb_planted" || c.pending == nil || c.bombTimerConflict {
		return
	}
	raw, exists := observedProperty(entity, "m_flTimerLength")
	var value float64
	switch raw := raw.(type) {
	case float32:
		value = float64(raw)
	case float64:
		value = raw
	default:
		exists = false
	}
	if !exists || !finite(value) || value <= 0 || value > 600 {
		if c.pending.BombTimerSeconds == nil {
			c.warn("下包实体未记录可靠炸弹总时长；倒计时时钟保持未知")
		}
		return
	}
	if prior := c.pending.BombTimerSeconds; prior != nil && math.Abs(*prior-value) > 0.001 {
		c.pending.BombTimerSeconds = nil
		c.bombTimerConflict = true
		c.warn("同回合炸弹规则与下包实体时长矛盾；倒计时时钟保持未知")
		return
	}
	c.pending.BombTimerSeconds = &value
}

// The upstream creation handler synchronously emits BombPlanted before GS.Entities registration.
// Our handler runs later in the same class-creation phase, never on later updates.
func (c *collector) completePlantEvidence(tick int, at float64, id int, entity coordinateEntity) {
	if c.pending == nil || id < 0 || !finite(at) {
		return
	}
	index := -1
	for i, e := range c.pending.Events {
		if e.Kind == "bomb_planted" && e.Tick == tick && e.Time == at && e.EntityID == nil {
			if index >= 0 {
				c.warn("同刻下包事件不唯一；实体关联保持未知")
				return
			}
			index = i
		}
	}
	if index < 0 {
		return
	}
	event := &c.pending.Events[index]
	event.EntityID = &id
	applyBombPosition(event, entity)
	if event.BombSite == nil {
		if site := observedInt(entity, "m_nBombSite", 0, 1); site != nil {
			name := []string{"A", "B"}[*site]
			event.BombSite = &name
		}
	}
	c.observeBombTimer("bomb_planted", entity)
}
func (c *collector) currentBombEntity(p dem.Parser, action GameEvent) st.Entity {
	switch action.Kind {
	case "bomb_defuse_start", "bomb_defuse_aborted", "bomb_defused", "bomb_exploded":
	default:
		return nil // Plant begin/abort precede the planted entity; planted binds its own constructor.
	}
	if c.pending == nil || !finite(action.Time) || action.Tick < 0 {
		return nil
	}
	var plant *GameEvent
	defusing := false
	for i := range c.pending.Events {
		e := &c.pending.Events[i]
		if !finite(e.Time) || e.Time > action.Time || e.Tick > action.Tick {
			continue
		}
		switch e.Kind {
		case "bomb_planted":
			if plant != nil || e.EntityID == nil || *e.EntityID < 0 {
				return nil
			}
			plant = e
		case "bomb_defuse_start":
			defusing = plant != nil
		case "bomb_defuse_aborted":
			defusing = false
		case "bomb_defused", "bomb_exploded":
			if plant != nil {
				return nil
			}
		}
	}
	if plant == nil || (action.Kind == "bomb_defuse_aborted" && !defusing) {
		return nil
	}
	entity := p.GameState().Entities()[*plant.EntityID]
	if entity == nil || entity.ID() != *plant.EntityID || entity.ServerClass() == nil || entity.ServerClass().Name() != "CPlantedC4" {
		return nil
	}
	return entity
}
func (c *collector) captureBomb(p dem.Parser, kind string, player *common.Player, site string) {
	event := c.event(p, kind, player, nil)
	if entity := c.currentBombEntity(p, event); entity != nil {
		id := entity.ID()
		if id >= 0 {
			event.EntityID = &id
		}
		if site != "A" && site != "B" {
			if n := observedInt(entity, "m_nBombSite", 0, 1); n != nil {
				site = []string{"A", "B"}[*n]
			}
		}
		applyBombPosition(&event, entity)
		c.observeBombTimer(kind, entity)
	}
	if event.X == nil {
		c.warn("部分炸弹事件缺少可靠位置；坐标保持未知")
	}
	var kit *bool
	if kind == "bomb_defuse_start" && event.ActorState != nil {
		kit = event.ActorState.DefuseKit
	}
	c.bombAction(event, site, kit)
}
func bombSite(site events.Bombsite) string {
	switch site {
	case events.BombsiteA:
		return "A"
	case events.BombsiteB:
		return "B"
	default:
		return ""
	}
}
func observedVector(source propertySource, name string) (r3.Vector, bool) {
	raw, ok := observedProperty(source, name)
	if !ok {
		return r3.Vector{}, false
	}
	var v r3.Vector
	switch raw := raw.(type) {
	case [3]float32:
		v = r3.Vector{X: float64(raw[0]), Y: float64(raw[1]), Z: float64(raw[2])}
	case []float32:
		if len(raw) != 3 {
			return v, false
		}
		v = r3.Vector{X: float64(raw[0]), Y: float64(raw[1]), Z: float64(raw[2])}
	default:
		return v, false
	}
	return v, finite(v.X, v.Y, v.Z)
}

const maxFirePoints = 128

func readFirePoints(entity coordinateEntity) ([]FirePoint, string) {
	count := observedInt(entity, "m_fireCount", 0, 4096)
	if count == nil {
		return nil, "Inferno火点数量字段缺失或超出支持范围；firePoints未知"
	}
	take := *count
	warning := ""
	if take > maxFirePoints {
		take = maxFirePoints
		warning = "Inferno火点超过128上限；仅保存前128个已验证火点"
	}
	points := make([]FirePoint, 0, take)
	for i := 0; i < take; i++ {
		suffix := fmt.Sprintf("%04d", i)
		burning := observedBool(entity, "m_bFireIsBurning."+suffix)
		if burning == nil {
			return nil, "Inferno部分火点燃烧字段缺失；firePoints未知"
		}
		position, available := observedVector(entity, "m_firePositions."+suffix)
		if !available {
			// A missing direct position may use offsets only when both the origin and all deltas were recorded.
			if _, present := observedProperty(entity, "m_firePositions."+suffix); present {
				return nil, "Inferno火点坐标格式错误或非有限；firePoints未知"
			}
			origin, originAvailable := entityPosition(entity)
			x := observedInt(entity, "m_fireXDelta."+suffix, -10000000, 10000000)
			y := observedInt(entity, "m_fireYDelta."+suffix, -10000000, 10000000)
			z := observedInt(entity, "m_fireZDelta."+suffix, -10000000, 10000000)
			if !originAvailable || x == nil || y == nil || z == nil {
				return nil, "Inferno部分火点坐标或可靠原点缺失；firePoints未知"
			}
			position = origin.Add(r3.Vector{X: float64(*x), Y: float64(*y), Z: float64(*z)})
		}
		if !finite(position.X, position.Y, position.Z) {
			return nil, "Inferno火点坐标非有限；firePoints未知"
		}
		points = append(points, FirePoint{X: position.X, Y: position.Y, Z: position.Z, IsBurning: *burning})
	}
	return points, warning
}

type infernoObservation struct {
	time      float64
	actorID   *string
	actorSide *string
}

func (c *collector) recordInferno(event GameEvent, entity coordinateEntity) {
	if c.pending == nil || event.Time < c.pending.StartedAt {
		return
	}
	if event.EntityID == nil || *event.EntityID < 0 {
		c.warn("Inferno缺少稳定实体ID；无法关联生命周期")
		return
	}
	id := *event.EntityID
	if event.Kind == "inferno_expired" {
		prior, ok := c.infernos[id]
		if !ok {
			c.warn("Inferno结束缺少当前回合开始证据；该生命周期事件已排除")
			return
		}
		if event.Time < prior.time {
			c.warn("Inferno生命周期时钟顺序不可靠；结束事件已排除")
			return
		}
		event.Duration = event.Time - prior.time
		if event.ActorID == nil {
			event.ActorID = prior.actorID
			event.ActorSide = prior.actorSide
		}
		delete(c.infernos, id)
	} else if event.Kind == "inferno_start" {
		if c.infernos == nil {
			c.infernos = map[int]infernoObservation{}
		}
		c.infernos[id] = infernoObservation{event.Time, event.ActorID, event.ActorSide}
	} else {
		return
	}
	points, warning := readFirePoints(entity)
	event.FirePoints = points
	if warning != "" {
		c.warn(warning)
	}
	c.record(event)
}
func (c *collector) captureInferno(p dem.Parser, kind string, inferno *common.Inferno) {
	if c.pending == nil {
		return
	}
	if inferno == nil || inferno.Entity == nil {
		c.warn("Inferno生命周期缺少实体；火焰证据不可用")
		return
	}
	entity := inferno.Entity
	var thrower *common.Player
	if handle := observedInt(entity, "m_hOwnerEntity", 0, math.MaxUint32); handle != nil && uint64(*handle) != constants.InvalidEntityHandleSource2 {
		thrower = p.GameState().Participants().FindByPawnHandle(uint64(*handle))
	}
	event := c.event(p, kind, thrower, nil)
	event.X = nil
	event.Y = nil
	event.Z = nil
	if origin, ok := entityPosition(entity); ok {
		event.X = &origin.X
		event.Y = &origin.Y
		event.Z = &origin.Z
	}
	id := entity.ID()
	event.EntityID = &id
	c.recordInferno(event, entity)
}
func (c *collector) registerContextEvents(p dem.Parser) {
	p.RegisterEventHandler(func(events.DataTablesParsed) {
		if class := p.ServerClasses().FindByName("CPlantedC4"); class != nil {
			class.OnEntityCreated(func(entity st.Entity) {
				if entity != nil {
					c.completePlantEvidence(p.GameState().IngameTick(), p.CurrentTime().Seconds(), entity.ID(), entity)
				}
			})
		}
	})
	p.RegisterEventHandler(func(e events.BombPlantBegin) { c.captureBomb(p, "bomb_plant_begin", e.Player, bombSite(e.Site)) })
	p.RegisterEventHandler(func(e events.BombPlantAborted) { c.captureBomb(p, "bomb_plant_aborted", e.Player, "") })
	p.RegisterEventHandler(func(e events.BombDefuseStart) { c.captureBomb(p, "bomb_defuse_start", e.Player, "") })
	p.RegisterEventHandler(func(e events.BombDefuseAborted) { c.captureBomb(p, "bomb_defuse_aborted", e.Player, "") })
	p.RegisterEventHandler(func(e events.InfernoStart) { c.captureInferno(p, "inferno_start", e.Inferno) })
	p.RegisterEventHandler(func(e events.InfernoExpired) { c.captureInferno(p, "inferno_expired", e.Inferno) })
}
func needsPlayerState(kind string) bool {
	return kind == "kill" || kind == "damage" || strings.HasPrefix(kind, "bomb_")
}
