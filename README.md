# ShrapnelFix

Client plugin for **SPT 4.1.6** that makes grenade fragments fly at body height instead of mostly over heads or into the floor, so grenades are properly lethal at mid range. Works standalone and with FIKA. No server mod is needed.

## What it changes

Vanilla (`EFT.ExplosionSharedMethods`) does the following:

- It only ever creates **30** fragments, whatever the grenade's `FragmentsCount` says (90 for the F-1, 100 for the VOG-17).
- It sprays them over a whole hemisphere. At 5 m, about two-thirds pass above a standing player's head.
- Fragments start about 4 cm off the floor, so any low obstacle blocks them.
- It aims 0–2 fragments per nearby player at a fixed point 0.8 m above their feet. That point is above a prone body.

This mod changes that:

| | Vanilla | ShrapnelFix (defaults) |
|---|---|---|
| Fragment origin (ground bursts) | ~4 cm | +0.35 m, clamped under tables and ceilings |
| Random fragments | `min(30, FragmentsCount)`, hemisphere | full `FragmentsCount` (cap 100), 85% inside a −5° to +25° band |
| Aimed fragments | 0–2 at feet + 0.8 m | vanilla shots plus the same share again, aimed along the feet-to-head line |
| Ground detection | Terrain + low-poly colliders only | also bullet-collision layers, so upper floors and catwalks aren't treated as mid-air bursts |
| Mid-air burst above 2 m | plain sphere (75% downward only above 4 m) | 50% of upward fragments mirrored downward |
| Blast (non-fragment) damage | unchanged | unchanged, including its origin (toggle) |

Every fragment is still a normal game shot. Armor, cover penetration, and damage overhauls such as TraumaCore and P.L.A.T.E. apply to them automatically. Landmines, artillery and scripted map explosions are never changed.

## Install

1. Copy `ShrapnelFix.dll` to `BepInEx/plugins/ShrapnelFix/`.
2. Launch once. Settings are in the F12 menu, or in `BepInEx/config/com.shrapnelfix.spt.cfg`.

**FIKA:** install the same DLL and the same `.cfg` on the host, the headless client (if used) and **every** client.
- A fragment hit only counts on the attacker's machine: the thrower's own client for human players, and the host or headless client for bots.
- If configs differ, a grenade's lethality depends on who threw it.
- No extra network traffic is added.

## Settings (F12)

| Section | Setting | Default | Notes |
|---|---|---|---|
| 1. General | Enabled | on | Off = vanilla |
| | Disabled maps | empty | Location ids, comma separated: `factory4_day, factory4_night, bigmap, Woods, Shoreline, Interchange, RezervBase, laboratory, Lighthouse, TarkovStreets, Sandbox, Sandbox_high, Labyrinth` |
| | Include 40mm/30mm launcher rounds | on | |
| | Global lethality multiplier | 1.0 | Scales random fragment count and extra aimed fragments |
| 2. Origin | Origin lift (m) | 0.35 | Ground bursts only |
| | Lift blast origin too | off | On = waist-high cover stops protecting your head |
| | Fix ground detection | on | |
| 3. Spray | Reshape random spray | on | Off = vanilla spray, but with the fixed ground detection |
| | Fragment count scale / Max fragments per explosion | 1.0 / 100 | |
| | Band min / max elevation | −5° / 25° | |
| | Fraction in band | 0.85 | |
| | Airburst down bias | 0.5 | Bursts more than 2 m up |
| | Speed factor min / max | 0.8 / 1.0 | Vanilla 0.5–0.95 |
| 4. Aimed fragments | Aimed fragment multiplier | 2.0 | 1.0 = vanilla. Max 4 extra per target |
| | Aim along feet-to-head line | on | |
| 5. Performance | Fragments per frame (per explosion / all explosions) | 30 / 90 | |
| 6. Per-grenade multipliers | F-1, RGD-5, M67, RGN, RGO, VOG-17, VOG-25, V40, 40mm VOG-25, VOG-30, M433, 40x46 HE | 1.0 | Modded or unlisted grenades use the global multiplier only |
| 7. Debug | Draw fragment rays / Log explosions and fragment hits | off | |

## Tuning

- **Start with debug on.** Throw a grenade near bots in a room. Green rays are band fragments, yellow are stray fragments, red are extra aimed fragments. `LogOutput.log` gets one line per explosion and one per fragment body hit.
- **Too lethal:** lower *Aimed fragment multiplier* first (try 1.5), then *Fraction in band* (0.7), or use the per-grenade multipliers.
- **Too weak at range:** raise *Fragment count scale* or the per-grenade multiplier rather than the lift.
- **Low cover:** an origin lift above ~0.45 m starts letting fragments clear knee-high cover. Keep *Lift blast origin too* off unless you want that.
- **TraumaCore:** its fragmentation adds wound damage per fragment hit. If grenades feel like overkill with it, lower the global multiplier to about 0.7.
- **Performance:** each fragment is a full ballistic shot. On weak CPUs, or with many grenade-happy bots, lower *Max fragments per explosion* (60) and *Fragments per frame (all explosions)* (60).

## Known issues

- If a grenade lands right at someone's feet, the lifted origin can start inside their leg hitbox, and fragments starting inside a hitbox don't hit it. The blast still does full damage at that range.
- The band is measured from world-horizontal. On slopes and stairs, the lowest band fragments can hit the slope early.
- Every FIKA machine simulates the extra fragments for visuals, so the CPU cost is paid on each machine, not only the authoritative one.
- Extra aimed fragments fire immediately and ignore the per-frame caps. There are at most 4 per target.
- Multipliers below 1 only reduce the random spray. Aimed fragments never drop below vanilla.
- Debug rays are straight lines to the first surface. Real shots also drop, ricochet and penetrate.
- If a game update renames a patched method, that patch logs an error at startup and switches itself off. The rest keeps working.

## Building

```bash
dotnet build -c Release -p:SptRoot="E:\Games\Escape From Tarkov - 4.1.6"
```

Output: `bin/Release/netstandard2.1/ShrapnelFix.dll`.
