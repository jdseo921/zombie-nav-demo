# AI architecture

The full write-up of both pursuit architectures in *Zombie Nav Demo*. The
[README](../README.md) carries the short version and the level comparison table; this is the
long one, unabridged.

Both levels share one navigation graph, one collision body and one sensor model. What changes is
*what the zombies are allowed to know* and *who decides where they go*.

## Level 1 — independent A* against a moving target

Each zombie in [`NpcController.cs`](../Assets/Scripts/NpcDemo/NpcController.cs) is autonomous and
runs a small state machine: `Patrol → Investigate → Chase → Attack`, plus `Flee` and `Follow`
for civilians. A zombie sees the player within `sightRange` when line of sight is clear, and
hears a sprinting player through walls within `hearingRange`. On acquiring a target it runs A*
to the target's **true cell**, then replans whenever that target steps onto a new cell,
rate-limited to one search per `MinRepathInterval` (0.2s); a zombie investigating a heard noise
instead replans on the slower `RepathInterval` (0.5s). This is classical replanning against a
moving target (Hart, Nilsson & Raphael 1968; Ishida & Korf, *Moving Target Search*, 1991).

Two details make it read as a pack rather than three separate chasers. Entering a chase fires a
"scream" (`AlertNearbyZombies`) that pushes patrolling zombies within `alertRadius` into
`Investigate` on the target's cell. And on losing the trail a zombie walks to the last known
position before returning to patrol, rather than snapping back instantly.

The important limitation is deliberate: **a Level 1 zombie always reads the player's true
position once it has line of sight.** Breaking line of sight only starts a grace timer
(`loseSightGrace`).

## Level 2 — occupancy-grid belief, flow field, roles, ambush

[`HordeDirector.cs`](../Assets/Scripts/NpcDemo/HordeDirector.cs) is a single central planner; the
zombies in [`HordeZombieController.cs`](../Assets/Scripts/NpcDemo/HordeZombieController.cs) only
execute assigned roles. The director ticks every `repathInterval` (0.4s) and runs four layers.

**1. Belief over where the player is.** The director keeps a probability `belief[]` over every
walkable column. Each tick is one recursive-Bayes step:

- *Collapse.* If any zombie currently sees or hears the player (`TargetConfirmed` — a sighting
  within the last 0.6s), the belief collapses to a single cell with probability 1.
- *Diffusion.* Otherwise the mass spreads to walkable neighbors at `diffusionRate`, a
  random-walk motion model for a target the horde cannot see.
- *Culling by negative observation.* Any cell a zombie can currently see (within
  `sightCullRange`, with clear line of sight) is multiplied by `negativeObservation` (0.06).
  Looking somewhere and *not* finding the player is evidence, so sweeping zombies squeeze the
  probability mass.
- *Normalize.* If the total mass falls below `1e-6`, every plausible hiding place has been
  cleared and the horde stands down.

This is the occupancy-grid idea from robotics (Moravec & Elfes 1985; Elfes 1989) adapted to game
AI for probabilistic target tracking and search (Isla 2006; Isla, *Third Eye Crime*, AIIDE
2013). The consequence that matters for play: **while you are hidden the horde never reads your
true position — it hunts the belief**, so breaking line of sight genuinely works.

**2. Shared flow field.** Once the director picks a goal — your cell when confirmed, the belief
peak when not — it calls `nav.ComputeFlowField(target)` once. Every chasing zombie then reads
its next step straight off that field in O(1) instead of running its own A* (potential fields,
Khatib 1986; continuum crowd fields, Treuille, Cooper & Popović 2006).

**3. Role allocation.** The horde is sorted by flow distance to the goal, then split by
`flankerFraction` (0.5):

- *Target confirmed* — the nearest half take `Chase` and ride the flow field. The farthest half
  take `Flank` and A* to cut-off cells arranged in a ring (`encircleRadius`) around a predicted
  intercept point, computed from your current velocity times `interceptLeadSeconds`. This is
  pursuit role allocation (Hespanha, Kim & Sastry 1999; Vidal et al. 2002; graph pursuit,
  "cops and robbers", Nowakowski & Winkler 1983).
- *Target hidden* — the nearest half converge on the belief peak while the farthest half take
  `Search` and are spread across the secondary peaks (`CollectSearchPeaks`, spaced at least 10
  cells apart), so the horde sweeps several hypotheses at once instead of clumping on one.

**4. Habit-anticipatory ambush.** A decaying visit heatmap (decay 0.98 per 0.4s tick) records
where you actually walk, and the strongest cells become `hotspots`. The heatmap does two jobs.
It biases belief diffusion toward your habitual routes (`heatBias`), and it drives an ambush
layer: whenever the target is **not** confirmed — including while the horde is otherwise calm —
up to `maxAmbushers` zombies are sent to the nearest **chokepoint** to a hotspot and hold
position there. Chokepoints are computed statically from the navigation graph in
`IsoNavGrid.ComputeChokepoints`: a cell you can pass through along one axis where the
perpendicular clearance is at most 2 cells, which is to say doorways and tight corridors. An
ambusher approaches at patrol speed, then stands still until you come within
`ambushTriggerRange`; springing the trap counts as an observation and collapses the belief.

The source comment flags this fusion — online route learning (cf. Yannakakis & Togelius 2013),
static chokepoint topology, and pre-emptive role allocation — as the project's own extension:
the cited techniques react to where the target *is* or probably is, while this layer
pre-positions for where the target will *return*. Both counters are surfaced on the HUD as
"Ambushes N set / M sprung".
