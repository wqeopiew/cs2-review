package main

import (
	"fmt"

	dem "github.com/markus-wa/demoinfocs-golang/v5/pkg/demoinfocs"
	"github.com/markus-wa/demoinfocs-golang/v5/pkg/demoinfocs/common"
	"github.com/markus-wa/demoinfocs-golang/v5/pkg/demoinfocs/events"
)

// The published round preserves the win decision and its survivor snapshot.
// Post-win combat stays provisional until an actual RoundEndOfficial confirms
// the statistics window. A new round/reset/EOF discards unconfirmed combat.
type roundStatistics struct {
	index  int
	events []GameEvent
}

func statisticsCombat(e GameEvent, round *RoundData) bool {
	if e.Kind != "kill" && e.Kind != "damage" && e.Kind != "flash" {
		return false
	}
	if e.TargetID == nil {
		return false
	}
	member := func(id *string, side *string) bool {
		if id == nil {
			return true
		} // Recorded world damage/deaths have no actor.
		for _, p := range round.Participants {
			if p.PlayerID == *id {
				return side == nil || p.Side == *side
			}
		}
		return false
	}
	return member(e.TargetID, e.TargetSide) && member(e.ActorID, e.ActorSide) && member(e.AssisterID, nil)
}

func (c *collector) discardStatistics(reason string) {
	if c.finalized == nil {
		return
	}
	if len(c.finalized.events) > 0 {
		c.warn(fmt.Sprintf("回合 %d: %s；结束后的事件已排除", c.match.Rounds[c.finalized.index].Number, reason))
	}
	c.eventCount -= len(c.finalized.events)
	c.finalized = nil
}

func (c *collector) endStatistics(at float64, completedRound int) {
	if c.finalized == nil {
		return
	}
	round := &c.match.Rounds[c.finalized.index]
	if !finite(at) || at < round.EndedAt || completedRound != round.Number {
		c.discardStatistics("统计结算时钟或回合编号不可靠")
		return
	}
	round.StatisticsEndedAt = &at
	for _, e := range c.finalized.events {
		if e.Time <= at {
			round.Events = append(round.Events, e)
		} else {
			c.eventCount--
			c.warn(fmt.Sprintf("回合 %d: 事件晚于已记录统计结算边界，已排除", round.Number))
		}
	}
	c.finalized = nil
}

func (c *collector) finishMatch() {
	c.finishedMatch = true
	c.collectionStopped = true
	// Do not erase provisional evidence here: a subsequently recorded official
	// boundary may still confirm combat recorded before the match win panel.
}

func (c *collector) acceptsCombat(p dem.Parser) bool {
	gs := p.GameState()
	return !c.finishedMatch && !c.collectionStopped && gs.IsMatchStarted() && !gs.IsWarmupPeriod() && gs.GamePhase() != common.GamePhaseGameEnded && gs.GamePhase() != common.GamePhaseGameOver
}

func (c *collector) captureHurt(p dem.Parser, hurt events.PlayerHurt) {
	if !c.acceptsCombat(p) {
		return
	}
	event := c.event(p, "damage", hurt.Attacker, hurt.Player)
	// HealthDamageTaken may read stale entity HP during consecutive same-tick
	// hurts. Preserve the server event's dmg_health and post-hit health instead.
	event.Damage = hurt.HealthDamage
	if hurt.Player != nil && playerID(hurt.Player) != nil {
		event.TargetHealthAfter = integerValue(hurt.Health, 0, 100)
	}
	event.Weapon = weaponName(hurt.Weapon)
	c.record(event)
}
