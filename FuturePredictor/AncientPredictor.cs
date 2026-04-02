using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Extensions;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Random;

namespace AncientPredictor.FuturePredictor;

/// <summary>
/// Predicts Ancient identities and their 3 relic options for each Act (Act 2, Act 3).
/// Algorithm faithfully replicates the game source code logic using the same RNG seed formula:
///   eventRng = new Rng((uint)(RunState.Rng.Seed + Owner.NetId + GetDeterministicHashCode(Id.Entry)))
///
/// Important: Prediction is based on the player state at the time of invocation (deck, relics, etc.).
///            The actual Ancient options are generated when entering the Ancient room. If the deck/relics
///            change before then, conditional options (ArchaicTooth, PaelsClaw, etc.) may differ.
///            However, the RNG seed and consumption sequence are deterministic.
/// </summary>
public static class AncientPredictor
{
    // ------------------------------------------------------------------
    // Data classes for structured prediction results
    // ------------------------------------------------------------------

    public record AncientActResult(
        int ActIndex,
        string ActId,
        string AncientId,
        string AncientType,
        List<AncientOptionResult> Options
    );

    public record AncientOptionResult(
        string RelicId,
        string DisplayName,
        string? Note = null
    );

    // ------------------------------------------------------------------
    // Cache
    // ------------------------------------------------------------------
    private record CacheKey(uint RunSeed, ulong NetId, int ActIndex);
    private static readonly Dictionary<CacheKey, AncientActResult> _cache = new();

    // ------------------------------------------------------------------
    // Public API
    // ------------------------------------------------------------------

    /// <summary>
    /// Predict all Ancients for acts 1+ (skipping Act 0/Neow).
    /// Returns a list of per-act results.
    /// </summary>
    public static List<AncientActResult> PredictAllAncients(Player player)
    {
        var runState = player.RunState;
        var results = new List<AncientActResult>();
        for (int i = 1; i < runState.Acts.Count; i++)
        {
            results.Add(PredictAncientForAct(player, i));
        }
        return results;
    }

    /// <summary>
    /// Predict the Ancient and its 3 options for a specific act.
    /// actIndex: 1 = Act 2, 2 = Act 3
    /// </summary>
    public static AncientActResult PredictAncientForAct(Player player, int actIndex)
    {
        var runState = player.RunState;
        if (actIndex < 0 || actIndex >= runState.Acts.Count)
            return new AncientActResult(actIndex, "?", "?", "Error", 
                new List<AncientOptionResult> { new("?", $"Act index {actIndex} out of range") });

        var key = new CacheKey(runState.Rng.Seed, player.NetId, actIndex);
        if (_cache.TryGetValue(key, out var cached))
            return cached;

        try
        {
            var act = runState.Acts[actIndex];
            var ancient = act.Ancient;
            var rng = BuildAncientRng(player, ancient);
            var options = SimulateOptions(player, ancient, rng, actIndex);
            
            // Get localized ancient name
            var ancientId = ancient.Id.ToString();
            var ancientType = ancient.GetType().Name;
            
            var result = new AncientActResult(actIndex, act.Id.ToString(), ancientId, ancientType, options);
            _cache[key] = result;
            return result;
        }
        catch (Exception ex)
        {
            return new AncientActResult(actIndex, "?", "?", "Error",
                new List<AncientOptionResult> { new("?", $"Prediction failed: {ex.Message}") });
        }
    }

    /// <summary>
    /// Reconstruct the Ancient's RNG:
    ///   new Rng((uint)(RunState.Rng.Seed + NetId + (ulong)GetDeterministicHashCode(Id.Entry)))
    /// AncientEventModel.IsShared is false (never overridden), so NetId is added.
    /// </summary>
    private static Rng BuildAncientRng(Player player, AncientEventModel ancient)
    {
        var runSeed = player.RunState.Rng.Seed;
        var netId = player.NetId;
        var entryHash = (ulong)(uint)StringHelper.GetDeterministicHashCode(ancient.Id.Entry);
        var seed = (uint)(runSeed + netId + entryHash);
        return new Rng(seed);
    }

    // ------------------------------------------------------------------
    // Per-Ancient option simulation (mirrors game source code)
    // ------------------------------------------------------------------
    private static List<AncientOptionResult> SimulateOptions(
        Player player, AncientEventModel ancient, Rng rng, int actIndex)
    {
        return ancient switch
        {
            Orobas    => SimulateOrobas(player, rng),
            Pael      => SimulatePael(player, rng),
            Tezcatara => SimulateTezcatara(rng),
            Nonupeipe => SimulateNonupeipe(player, rng),
            Tanx      => SimulateTanx(player, rng),
            Vakuu     => SimulateVakuu(rng),
            Darv      => SimulateDarv(player, rng, actIndex),
            _         => new List<AncientOptionResult> { new("?", $"No simulator for {ancient.GetType().Name}") }
        };
    }

    // ======================== Orobas ========================
    private static List<AncientOptionResult> SimulateOrobas(Player player, Rng rng)
    {
        // 1. Pick another character (for SeaGlass)
        var otherChars = player.UnlockState.Characters
            .Where(c => c.Id != player.Character.Id)
            .ToList();
        var seaGlassChar = rng.NextItem(otherChars) ?? player.Character;

        // 2. PrismaticGem vs SeaGlass
        var pool1 = new List<string> { "ElectricShrymp", "GlassEye", "SandCastle" };
        string mixedItem;
        if (rng.NextFloat() < 0.3333333f)
            mixedItem = "PrismaticGem";
        else
            mixedItem = $"SeaGlass ({seaGlassChar.Id.Entry})";
        pool1.Add(mixedItem);

        // 3. Pick from pool1 (4 items)
        var slot1 = rng.NextItem(pool1)!;

        // 4. Pool2: AlchemicalCoffer / Driftwood / RadiantPearl
        var pool2 = new List<string> { "AlchemicalCoffer", "Driftwood", "RadiantPearl" };
        var slot2 = rng.NextItem(pool2)!;

        // 5. Pool3: conditional TouchOfOrobas / ArchaicTooth
        var pool3 = BuildOrobasPool3(player);
        var slot3 = rng.NextItem(pool3)!;

        return new List<AncientOptionResult>
        {
            MakeOption(slot1),
            MakeOption(slot2),
            MakeOption(slot3)
        };
    }

    private static List<string> BuildOrobasPool3(Player player)
    {
        var pool3 = new List<string>();

        // TouchOfOrobas: player has a Starter-rarity relic
        var starterRelic = player.Relics.FirstOrDefault(r => r.Rarity == RelicRarity.Starter);
        if (starterRelic != null)
            pool3.Add($"TouchOfOrobas (transforms {starterRelic.Id.Entry})");

        // ArchaicTooth: deck has a Transcendence starter card
        var transcendenceKeys = new HashSet<string> { "Bash", "Neutralize", "Unleash", "FallingStar", "Dualcast" };
        bool hasTranscendenceCard = player.Deck.Cards.Any(c => transcendenceKeys.Contains(c.Id.Entry));
        if (hasTranscendenceCard)
            pool3.Add("ArchaicTooth");

        if (pool3.Count == 0)
            pool3.Add("(locked - no valid options)");

        return pool3;
    }

    // ======================== Pael ========================
    private static List<AncientOptionResult> SimulatePael(Player player, Rng rng)
    {
        // Pool1
        var pool1 = new List<string> { "PaelsFlesh", "PaelsHorn", "PaelsTears" };
        var slot1 = rng.NextItem(pool1)!;

        // Pool2
        var pool2 = new List<string> { "PaelsWing" };
        var cards = player.Deck.Cards;
        bool hasGoopy = cards.Count(c => ModelDb.Enchantment<Goopy>().CanEnchant(c)) >= 3;
        bool hasRemovable5 = cards.Count(c => c.IsRemovable) >= 5;
        if (hasGoopy) pool2.Add("PaelsClaw");
        if (hasRemovable5) pool2.Add("PaelsTooth");
        // Source code: list.AddRange(list) -- double the list, then add PaelsGrowth
        pool2.AddRange(pool2.ToList());
        pool2.Add("PaelsGrowth");
        var slot2 = rng.NextItem(pool2)!;

        // Pool3
        var pool3 = new List<string> { "PaelsEye", "PaelsBlood" };
        bool hasPet = player.HasEventPet();
        if (!hasPet) pool3.Add("PaelsLegion");
        var slot3 = rng.NextItem(pool3)!;

        string note2 = $"[Goopy>=3:{hasGoopy}, Removable>=5:{hasRemovable5}]";
        string note3 = hasPet ? "[has pet: PaelsLegion excluded]" : "[no pet: PaelsLegion included]";

        return new List<AncientOptionResult>
        {
            MakeOption(slot1),
            MakeOption(slot2, note2),
            MakeOption(slot3, note3)
        };
    }

    // ======================== Tezcatara ========================
    private static List<AncientOptionResult> SimulateTezcatara(Rng rng)
    {
        var pool1 = new List<string> { "NutritiousSoup", "VeryHotCocoa", "YummyCookie" };
        var pool2 = new List<string> { "BiiigHug", "Storybook", "SealOfGold", "ToastyMittens" };
        var pool3 = new List<string> { "GoldenCompass", "PumpkinCandle", "ToyBox" };
        return new List<AncientOptionResult>
        {
            MakeOption(rng.NextItem(pool1)!),
            MakeOption(rng.NextItem(pool2)!),
            MakeOption(rng.NextItem(pool3)!)
        };
    }

    // ======================== Nonupeipe ========================
    private static List<AncientOptionResult> SimulateNonupeipe(Player player, Rng rng)
    {
        var pool = new List<string>
        {
            "BlessedAntler", "BrilliantScarf", "DelicateFrond",
            "DiamondDiadem", "FurCoat", "Glitter",
            "JewelryBox", "LoomingFruit", "SignetRing"
        };
        int swiftCount = player.Deck.Cards.Count(c => ModelDb.Enchantment<Swift>().CanEnchant(c));
        bool canBracelet = swiftCount >= 4;
        if (canBracelet) pool.Add("BeautifulBracelet");

        pool.UnstableShuffle(rng);
        var chosen = pool.Take(3).ToList();

        return chosen.Select(r => MakeOption(r,
            r == "BeautifulBracelet" ? $"[apex: Swift x{swiftCount}>=4]" : null)).ToList();
    }

    // ======================== Tanx ========================
    private static List<AncientOptionResult> SimulateTanx(Player player, Rng rng)
    {
        var pool = new List<string>
        {
            "Claws", "Crossbow", "IronClub", "MeatCleaver", "Sai",
            "SpikedGauntlets", "TanxsWhistle", "ThrowingAxe", "WarHammer"
        };
        int instinctCount = player.Deck.Cards.Count(c => ModelDb.Enchantment<Instinct>().CanEnchant(c));
        bool canApex = instinctCount >= 3;
        if (canApex) pool.Add("TriBoomerang");

        pool.UnstableShuffle(rng);
        var chosen = pool.Take(3).ToList();

        return chosen.Select(r => MakeOption(r,
            r == "TriBoomerang" ? $"[apex: Instinct x{instinctCount}>=3]" : null)).ToList();
    }

    // ======================== Vakuu ========================
    private static List<AncientOptionResult> SimulateVakuu(Rng rng)
    {
        var pool1 = new List<string> { "BloodSoakedRose", "WhisperingEarring", "Fiddle" };
        var pool2 = new List<string> { "PreservedFog", "SereTalon", "DistinguishedCape" };
        var pool3 = new List<string> { "ChoicesParadox", "MusicBox", "LordsParasol", "JeweledMask" };

        pool1.UnstableShuffle(rng);
        pool2.UnstableShuffle(rng);
        pool3.UnstableShuffle(rng);

        return new List<AncientOptionResult>
        {
            MakeOption(pool1[0]),
            MakeOption(pool2[0]),
            MakeOption(pool3[0])
        };
    }

    // ======================== Darv ========================
    private static List<AncientOptionResult> SimulateDarv(Player player, Rng rng, int actIndex)
    {
        // Build relic sets matching source code static Darv() order
        var allSets = new List<(Func<bool> filter, List<string> relics, string? note)>
        {
            (() => true, new List<string> { "Astrolabe" }, null),
            (() => true, new List<string> { "BlackStar" }, null),
            (() => true, new List<string> { "CallingBell" }, null),
            (() => true, new List<string> { "EmptyCage" }, null),
            (() => !player.RunState.Modifiers.Any(m => m.ClearsPlayerDeck),
                new List<string> { "PandorasBox" }, "requires no ClearsPlayerDeck modifier"),
            (() => true, new List<string> { "RunicPyramid" }, null),
            (() => true, new List<string> { "SneckoEye" }, null),
            // Use actIndex parameter instead of player.RunState.CurrentActIndex for forward prediction
            (() => actIndex == 1, new List<string> { "Ectoplasm", "Sozu" }, "act2 only"),
            (() => actIndex == 2, new List<string> { "PhilosophersStone", "VelvetChoker" }, "act3 only"),
        };

        // Source: (from rs in _validRelicSets where rs.filter(Owner) select ...).ToList()
        var pickedRelics = new List<string>();
        foreach (var set in allSets)
        {
            if (set.filter())
            {
                var picked = rng.NextItem(set.relics)!;
                pickedRelics.Add(picked);
            }
        }

        // Shuffle
        pickedRelics.UnstableShuffle(rng);

        // 50% DustyTome
        bool hasDustyTome = rng.NextBool();

        List<AncientOptionResult> result;
        if (hasDustyTome)
        {
            result = pickedRelics.Take(2)
                .Select(r => MakeOption(r))
                .ToList();
            result.Add(MakeOption("DustyTome", "50% branch"));
        }
        else
        {
            result = pickedRelics.Take(3)
                .Select(r => MakeOption(r))
                .ToList();
        }

        return result;
    }

    // ------------------------------------------------------------------
    // Helper: Create an AncientOptionResult with localized name lookup
    // ------------------------------------------------------------------
    private static AncientOptionResult MakeOption(string relicName, string? note = null)
    {
        var relic = ModelDb.AllRelics
            .FirstOrDefault(r => string.Equals(r.Id.Entry, relicName, StringComparison.OrdinalIgnoreCase));
        var localizedName = relic?.Title?.GetFormattedText() ?? relicName;
        return new AncientOptionResult(relicName, localizedName, note);
    }

    /// <summary>
    /// Clear cache (call when entering a new run or loading a save).
    /// </summary>
    public static void ClearCache() => _cache.Clear();
}
