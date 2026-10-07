using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using SPT.Reflection.Patching;

namespace ShrapnelFix
{
    [BepInPlugin("com.shrapnelfix.spt", "ShrapnelFix", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;

        // 1. General
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<string> DisabledMaps;
        internal static ConfigEntry<bool> IncludeLauncherRounds;
        internal static ConfigEntry<float> GlobalMultiplier;

        // 2. Origin
        internal static ConfigEntry<float> OriginLift;
        internal static ConfigEntry<bool> LiftBlast;
        internal static ConfigEntry<bool> FixGroundDetection;

        // 3. Spray
        internal static ConfigEntry<bool> ReshapeSpray;
        internal static ConfigEntry<float> CountScale;
        internal static ConfigEntry<int> MaxFragments;
        internal static ConfigEntry<float> MinElevation;
        internal static ConfigEntry<float> MaxElevation;
        internal static ConfigEntry<float> BandFraction;
        internal static ConfigEntry<float> AirburstDownBias;
        internal static ConfigEntry<float> SpeedMin;
        internal static ConfigEntry<float> SpeedMax;

        // 4. Aimed fragments
        internal static ConfigEntry<float> AimedMultiplier;
        internal static ConfigEntry<bool> BodyAim;

        // 5. Performance
        internal static ConfigEntry<int> FragmentsPerFrame;
        internal static ConfigEntry<int> GlobalPerFrame;

        // 7. Debug
        internal static ConfigEntry<bool> DebugDraw;
        internal static ConfigEntry<bool> DebugLog;

        // Template ids from SPT_Data/database/templates/items.json. Unlisted (e.g. modded) grenades use the global multiplier only.
        private static readonly (string Name, string[] Ids)[] Grenades =
        {
            ("F-1", new[] { "5710c24ad2720bc3458b45a3", "67b49e7335dec48e3e05e057" }),
            ("RGD-5", new[] { "5448be9a4bdc2dfd2f8b456a" }),
            ("M67", new[] { "58d3db5386f77426186285a0" }),
            ("RGN", new[] { "617fd91e5539a84ec44ce155" }),
            ("RGO", new[] { "618a431df1eb8e24b8741deb" }),
            ("VOG-17 Khattabka", new[] { "5e32f56fcb6d5863cc5e5ee4" }),
            ("VOG-25 Khattabka", new[] { "5e340dcdcb6d5863cc5e5efb" }),
            ("V40 Mini", new[] { "66dae7cbeb28f0f96809f325" }),
            ("40mm VOG-25", new[] { "5656eb674bdc2d35148b457c" }),
            ("30x29mm VOG-30", new[] { "5d70e500a4b9364de70d38ce" }),
            ("40x46mm M433 HEDP", new[] { "5f0c892565703e5c461894e9" }),
            ("40x46mm HE (M381, M386, M406, M441)", new[] { "5ede474b0c226a66f5402622", "5ede475b549eed7c6d5c18fb", "5ede4739e0350d05467f73e8", "5ede47405b097655935d7d16" }),
        };

        private static readonly Dictionary<string, ConfigEntry<float>> PerGrenade = new Dictionary<string, ConfigEntry<float>>();

        private void Awake()
        {
            Log = Logger;

            const string general = "1. General";
            Enabled = Config.Bind(general, "Enabled", true,
                "Master switch. Off = 100% vanilla grenade behaviour.");
            DisabledMaps = Config.Bind(general, "Disabled maps", "",
                "Comma-separated location ids where the mod does nothing, e.g. factory4_day,factory4_night,laboratory");
            IncludeLauncherRounds = Config.Bind(general, "Include 40mm/30mm launcher rounds", true,
                "Also apply to under-barrel and launcher grenades (VOG-25, M433, M381...). Mines and artillery are never touched.");
            GlobalMultiplier = Config.Bind(general, "Global lethality multiplier", 1f, new ConfigDescription(
                "Scales the random spray count and the extra aimed fragments for every grenade. Multiplies the per-grenade value.",
                new AcceptableValueRange<float>(0f, 5f)));

            const string origin = "2. Origin";
            OriginLift = Config.Bind(origin, "Origin lift (m)", 0.35f, new ConfigDescription(
                "Ground bursts: fragments start this far above the grenade instead of ~4 cm off the floor. Clamped below tables/ceilings.",
                new AcceptableValueRange<float>(0f, 0.6f)));
            LiftBlast = Config.Bind(origin, "Lift blast origin too", false,
                "Also lift the blast (non-fragment) damage rays. Makes waist-high cover much weaker; off keeps vanilla blast geometry.");
            FixGroundDetection = Config.Bind(origin, "Fix ground detection", true,
                "Detect floors on bullet-collision layers too, so grenades on catwalks/upper floors aren't treated as mid-air bursts (which vanilla sprays into the floor).");

            const string spray = "3. Spray";
            ReshapeSpray = Config.Bind(spray, "Reshape random spray", true,
                "Replace vanilla's 30-fragment hemisphere spray with the settings below. Off = vanilla spray (with the fixed ground detection).");
            CountScale = Config.Bind(spray, "Fragment count scale", 1f, new ConfigDescription(
                "Random fragments = grenade FragmentsCount x this x lethality multipliers. Vanilla always caps at 30.",
                new AcceptableValueRange<float>(0f, 3f)));
            MaxFragments = Config.Bind(spray, "Max fragments per explosion", 100, new ConfigDescription(
                "Hard cap on random fragments per explosion.", new AcceptableValueRange<int>(0, 300)));
            MinElevation = Config.Bind(spray, "Band min elevation (deg)", -5f, new ConfigDescription(
                "Ground bursts: lowest angle of the human-height band. Negative = slightly downward (hits legs before the floor).",
                new AcceptableValueRange<float>(-30f, 0f)));
            MaxElevation = Config.Bind(spray, "Band max elevation (deg)", 25f, new ConfigDescription(
                "Ground bursts: highest angle of the human-height band.", new AcceptableValueRange<float>(5f, 60f)));
            BandFraction = Config.Bind(spray, "Fraction in band", 0.85f, new ConfigDescription(
                "Ground bursts: share of fragments inside the band. The rest spray over the upper hemisphere like vanilla.",
                new AcceptableValueRange<float>(0f, 1f)));
            AirburstDownBias = Config.Bind(spray, "Airburst down bias", 0.5f, new ConfigDescription(
                "Bursts more than 2 m up: chance an upward fragment is mirrored downward. Lower bursts stay a plain sphere. Vanilla: 0.75 only above 4 m.",
                new AcceptableValueRange<float>(0f, 1f)));
            SpeedMin = Config.Bind(spray, "Speed factor min", 0.8f, new ConfigDescription(
                "Random fragments launch at a random fraction of the fragment ammo's speed between min and max. Vanilla: 0.5-0.95.",
                new AcceptableValueRange<float>(0.3f, 1f)));
            SpeedMax = Config.Bind(spray, "Speed factor max", 1f, new ConfigDescription(
                "See Speed factor min.", new AcceptableValueRange<float>(0.3f, 1f)));

            const string aimed = "4. Aimed fragments";
            AimedMultiplier = Config.Bind(aimed, "Aimed fragment multiplier", 2f, new ConfigDescription(
                "Vanilla fires a distance-based share of fragments straight at each nearby player (max 2). " +
                "This adds (multiplier - 1) x that share as extra aimed fragments, max 4 extra per target. 1 = vanilla.",
                new AcceptableValueRange<float>(1f, 5f)));
            BodyAim = Config.Bind(aimed, "Aim along feet-to-head line", true,
                "Extra aimed fragments target a random point between the feet and head (works standing, crouched, prone). Off = vanilla aim (0.8 m above the feet).");

            const string perf = "5. Performance";
            FragmentsPerFrame = Config.Bind(perf, "Fragments per frame (per explosion)", 30, new ConfigDescription(
                "Random fragments fired per frame by one explosion. Vanilla: 9.", new AcceptableValueRange<int>(5, 200)));
            GlobalPerFrame = Config.Bind(perf, "Fragments per frame (all explosions)", 90, new ConfigDescription(
                "Shared per-frame cap across simultaneous explosions. Extra fragments wait for the next frame.",
                new AcceptableValueRange<int>(10, 500)));

            foreach (var (name, ids) in Grenades)
            {
                var entry = Config.Bind("6. Per-grenade multipliers", name, 1f, new ConfigDescription(
                    "Lethality multiplier for this grenade (random spray count and extra aimed fragments).",
                    new AcceptableValueRange<float>(0f, 5f)));
                foreach (var id in ids) PerGrenade[id] = entry;
            }

            DebugDraw = Config.Bind("7. Debug", "Draw fragment rays", false,
                "Draw each fragment's path for 3 s, visible through walls. Green = band, yellow = stray, red = extra aimed.");
            DebugLog = Config.Bind("7. Debug", "Log explosions and fragment hits", false,
                "Log one line per explosion and one per fragment body hit to BepInEx/LogOutput.log.");

            ModulePatch[] patches = { new ExplosionPatch(), new BlastOriginPatch(), new SpawnRandomFragmentsPatch(), new FragmentHitLogPatch() };
            foreach (var patch in patches)
            {
                try { patch.Enable(); }
                catch (Exception ex) { Log.LogError($"{patch.GetType().Name} failed to apply, that part of ShrapnelFix is off: {ex}"); }
            }
        }

        internal static float GrenadeMultiplier(string templateId) =>
            PerGrenade.TryGetValue(templateId, out var entry) ? entry.Value : 1f;

        internal static bool IsMapDisabled(string locationId)
        {
            if (string.IsNullOrEmpty(locationId)) return false;
            foreach (var map in DisabledMaps.Value.Split(','))
            {
                if (string.Equals(map.Trim(), locationId, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }
}
