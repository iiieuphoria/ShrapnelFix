using System;
using System.Collections;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.Ballistics;
using EFT.InventoryLogic;
using SPT.Reflection.Patching;
using UnityEngine;
using Random = UnityEngine.Random;

namespace ShrapnelFix
{
    // State for the explosion currently inside ExplosionSharedMethods.Explosion. That method runs synchronously on the
    // main thread and calls AffectPlayersShared and SpawnRandomFragments before returning, so one static slot is enough.
    internal static class Blast
    {
        internal static bool Active;
        internal static string TemplateId;
        internal static Vector3 Original;  // where the game put the explosion
        internal static Vector3 Origin;    // lifted point fragments are fired from (stays set for the hit log)
        internal static float Height;      // distance to the floor below, +inf if none within the probe
        internal static float Lift;
        internal static bool Ground;
        internal static bool HighAir;
        internal static float Multiplier;  // global x per-grenade
        internal static int ExtraAimed;
        internal static int RandomCount;
    }

    // A + B + E: filter, classify ground/air, lift the origin. D: extra aimed fragments in the postfix.
    internal class ExplosionPatch : ModulePatch
    {
        private const float GroundThreshold = 0.3f;  // vanilla: ground burst if a floor is within 0.3 m
        private const float ProbeLength = 4f;        // vanilla ground probe length
        private const float ProbeStartOffset = 0.1f; // start above the point so impact rounds sitting on a surface still see it
        private const float HighAirHeight = 2f;      // above head height: airburst down bias applies
        private const float CeilingClearance = 0.05f;
        private const int MaxExtraAimedPerTarget = 4;
        private const float AimJitter = 0.3f;

        protected override MethodBase GetTargetMethod() =>
            typeof(ExplosionSharedMethods).GetMethod(nameof(ExplosionSharedMethods.Explosion));

        [PatchPrefix]
        public static void Prefix(IExplosiveItem grenadeItem, ref Vector3 explosionPosition)
        {
            Blast.Active = false;
            if (!Plugin.Enabled.Value || grenadeItem == null || grenadeItem.IsDummy || grenadeItem.FragmentsCount <= 0) return;

            string templateId;
            if (grenadeItem is ThrowWeap grenade) templateId = grenade.TemplateId.ToString();
            else if (grenadeItem is ExplosiveAmmoComponent round && Plugin.IncludeLauncherRounds.Value) templateId = (round.Template as ItemTemplate)?._id.ToString() ?? "";
            else return; // landmines, artillery, scripted map explosions

            if (Plugin.IsMapDisabled(Singleton<GameWorld>.Instance?.LocationId)) return;

            Vector3 pos = explosionPosition;
            int solidMask = LayersMaskController.TerrainLowPoly | LayersMaskController.HighPolyWithTerrainMask;

            // B: vanilla probes only Terrain + LowPolyCollider straight from the explosion point.
            bool fix = Plugin.FixGroundDetection.Value;
            float start = fix ? ProbeStartOffset : 0f;
            int floorMask = fix ? solidMask : (int)LayersMaskController.TerrainLowPoly;
            float height = Physics.Raycast(pos + Vector3.up * start, Vector3.down, out RaycastHit floor, ProbeLength + start, floorMask, QueryTriggerInteraction.Ignore)
                ? Mathf.Max(0f, floor.distance - start)
                : float.PositiveInfinity;

            bool ground = height < GroundThreshold;

            // A: lift ground bursts, but never through a table or ceiling.
            float lift = ground ? Plugin.OriginLift.Value : 0f;
            if (lift > 0f && Physics.Raycast(pos, Vector3.up, out RaycastHit ceiling, lift + CeilingClearance, solidMask, QueryTriggerInteraction.Ignore))
            {
                lift = Mathf.Max(0f, ceiling.distance - CeilingClearance);
            }

            Blast.TemplateId = templateId;
            Blast.Original = pos;
            Blast.Origin = pos + Vector3.up * lift;
            Blast.Height = height;
            Blast.Lift = lift;
            Blast.Ground = ground;
            Blast.HighAir = height > HighAirHeight;
            Blast.Multiplier = Plugin.GlobalMultiplier.Value * Plugin.GrenadeMultiplier(templateId);
            Blast.ExtraAimed = 0;
            Blast.RandomCount = -1; // -1 = vanilla spray
            Blast.Active = true;

            // Vanilla aimed fragments, the player scan and (unless BlastOriginPatch restores it) the blast all use this point.
            explosionPosition = Blast.Origin;
        }

        [PatchPostfix]
        public static void Postfix(IExplosiveItem grenadeItem, string playerProfileIDWhoThrew, IBallisticsCalculator ballisticsCalculator, Item originalWeaponItem)
        {
            if (!Blast.Active) return;
            Blast.Active = false;

            try
            {
                FireExtraAimed(grenadeItem, playerProfileIDWhoThrew, ballisticsCalculator, originalWeaponItem);
            }
            catch (Exception ex)
            {
                Logger.LogError($"ShrapnelFix: extra aimed fragments failed: {ex}");
            }

            if (Plugin.DebugLog.Value)
            {
                string mode = Blast.Ground ? "ground" : Blast.HighAir ? "high-air" : "air";
                Logger.LogInfo($"[explosion] {Blast.TemplateId} {mode} height={Blast.Height:F2} lift={Blast.Lift:F2} mult={Blast.Multiplier:F2} " +
                               $"targets={ExplosionSharedMethods._playerBuffer.Count} extraAimed={Blast.ExtraAimed} random={(Blast.RandomCount < 0 ? "vanilla" : Blast.RandomCount.ToString())}");
            }
        }

        // D: vanilla gives each player in range share = FragmentsCount / (2*pi*d^2), clamped to 3, and fires 0-2 shots at
        // feet + 0.8 m from it. We add (multiplier - 1) x share more, using stochastic rounding so small shares still count.
        private static void FireExtraAimed(IExplosiveItem grenadeItem, string profileId, IBallisticsCalculator calculator, Item weapon)
        {
            float extraFactor = Plugin.AimedMultiplier.Value * Blast.Multiplier - 1f;
            if (extraFactor <= 0f) return;

            Ammo ammo = null;
            foreach (var pair in ExplosionSharedMethods._playerBuffer)
            {
                IPlayer target = pair.Key?.iPlayer;
                if (target == null) continue;

                float d = pair.Value.Distance;
                float share = Mathf.Clamp(grenadeItem.FragmentsCount / (2f * Mathf.PI * d * d), 0f, 3f);
                float expected = share * extraFactor;
                int shots = Mathf.Min(MaxExtraAimedPerTarget, (int)expected + (Random.value < expected % 1f ? 1 : 0));

                for (int i = 0; i < shots; i++)
                {
                    ammo ??= grenadeItem.CreateFragment();
                    Vector3 dir = (AimPoint(target, share) - Blast.Origin).normalized;
                    Fragments.Fire(calculator, ammo, Blast.Origin, dir, profileId, weapon, 1f, DebugLines.Aimed);
                    Blast.ExtraAimed++;
                }
            }
        }

        private static Vector3 AimPoint(IPlayer target, float share)
        {
            Vector3 feet = target.Position;
            if (Plugin.BodyAim.Value && target.PlayerBones != null)
            {
                return Vector3.Lerp(feet, target.PlayerBones.Head.position, Random.Range(0.1f, 1f)) + Random.insideUnitSphere * AimJitter;
            }
            // Vanilla aim: 0.8 m above the feet, spread shrinking as the share grows.
            return feet + Vector3.up * 0.8f + Random.onUnitSphere * (0.5f / Mathf.Clamp(share, 0.3f, 2f));
        }
    }

    // Keeps blast damage rays at the grenade's real position unless "Lift blast origin too" is on.
    internal class BlastOriginPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            typeof(ExplosionSharedMethods).GetMethod(nameof(ExplosionSharedMethods.AffectPlayersShared));

        [PatchPrefix]
        public static void Prefix(ref Vector3 grenadePosition)
        {
            if (Blast.Active && !Plugin.LiftBlast.Value && grenadePosition == Blast.Origin) grenadePosition = Blast.Original;
        }
    }

    // C: replaces vanilla's random spray (min(30, FragmentsCount) minus aimed, uniform hemisphere) with ours.
    internal class SpawnRandomFragmentsPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            typeof(ExplosionSharedMethods).GetMethod(nameof(ExplosionSharedMethods.SpawnRandomFragments));

        [PatchPrefix]
        public static bool Prefix(ref IEnumerator __result, ref bool groundExpl, ref bool skyExpl, IBallisticsCalculator calculator,
            IExplosiveItem grenadeItem, Vector3 grenadePosition, string playerProfileID, Item originalWeaponItem)
        {
            if (!Blast.Active || grenadePosition != Blast.Origin) return true;

            if (!Plugin.ReshapeSpray.Value)
            {
                // Vanilla spray, but our classification: vanilla's own 0.3 m check ran from the lifted point.
                groundExpl = Blast.Ground;
                skyExpl = float.IsPositiveInfinity(Blast.Height);
                return true;
            }

            int count = Mathf.Clamp(Mathf.RoundToInt(grenadeItem.FragmentsCount * Plugin.CountScale.Value * Blast.Multiplier), 0, Plugin.MaxFragments.Value);
            Blast.RandomCount = count;
            __result = Fragments.Spray(count, Blast.Ground, Blast.HighAir, calculator, grenadeItem, grenadePosition, playerProfileID, originalWeaponItem);
            return false;
        }
    }

    internal static class Fragments
    {
        private static int _budgetFrame = -1;
        private static int _budgetUsed;

        internal static void Fire(IBallisticsCalculator calculator, Ammo ammo, Vector3 origin, Vector3 dir, string profileId, Item weapon, float speedFactor, Color debugColor)
        {
            calculator.Shoot(ammo, origin, dir, 1, profileId, weapon, speedFactor, 0);
            if (Plugin.DebugDraw.Value) DebugLines.AddRay(origin, dir, debugColor);
        }

        // Blast state is passed in, not read live: the coroutine outlives this explosion.
        internal static IEnumerator Spray(int count, bool ground, bool highAir, IBallisticsCalculator calculator, IExplosiveItem grenadeItem,
            Vector3 origin, string profileId, Item weapon)
        {
            Ammo ammo = grenadeItem.CreateFragment();
            float sinMin = Mathf.Sin(Plugin.MinElevation.Value * Mathf.Deg2Rad);
            float sinMax = Mathf.Sin(Plugin.MaxElevation.Value * Mathf.Deg2Rad);
            float speedMin = Mathf.Min(Plugin.SpeedMin.Value, Plugin.SpeedMax.Value);
            float speedMax = Mathf.Max(Plugin.SpeedMin.Value, Plugin.SpeedMax.Value);

            int fired = 0, firedThisFrame = 0;
            while (fired < count)
            {
                if (firedThisFrame >= Plugin.FragmentsPerFrame.Value || !TryTakeFrameBudget())
                {
                    firedThisFrame = 0;
                    yield return null;
                    if (Singleton<GameWorld>.Instance == null) yield break; // raid ended
                    continue;
                }

                // Sampling sin(elevation) uniformly gives an even spread per solid angle, same as vanilla's sphere.
                float sinEl;
                bool inBand = ground && Random.value < Plugin.BandFraction.Value;
                if (inBand) sinEl = Random.Range(sinMin, sinMax);
                else if (ground) sinEl = Random.value; // upper hemisphere, like vanilla ground bursts
                else
                {
                    sinEl = Random.Range(-1f, 1f);
                    if (highAir && sinEl > 0f && Random.value < Plugin.AirburstDownBias.Value) sinEl = -sinEl;
                }

                float azimuth = Random.value * 2f * Mathf.PI;
                float horizontal = Mathf.Sqrt(1f - sinEl * sinEl);
                var dir = new Vector3(horizontal * Mathf.Cos(azimuth), sinEl, horizontal * Mathf.Sin(azimuth));

                Fire(calculator, ammo, origin, dir, profileId, weapon, Random.Range(speedMin, speedMax), inBand ? DebugLines.Band : DebugLines.Stray);
                fired++;
                firedThisFrame++;
            }
        }

        // Shared per-frame cap across all explosions' sprays.
        private static bool TryTakeFrameBudget()
        {
            if (Time.frameCount != _budgetFrame)
            {
                _budgetFrame = Time.frameCount;
                _budgetUsed = 0;
            }
            if (_budgetUsed >= Plugin.GlobalPerFrame.Value) return false;
            _budgetUsed++;
            return true;
        }
    }
}
