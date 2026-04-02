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
        string AncientDisplayName,
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

            var ancientId = ancient.Id.Entry;
            // Use the game's localization system to get the ancient's display name
            var ancientDisplayName = ancient.Title.GetFormattedText();

            var result = new AncientActResult(actIndex, act.Id.ToString(), ancientId, ancientDisplayName, options);
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
    // Uses real RelicModel objects for correct RNG consumption and localization
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
            _         => new List<AncientOptionResult> { new("?", $"Unknown Ancient: {ancient.GetType().Name}") }
        };
    }

    // ======================== Orobas ========================
    // Source code GenerateInitialOptions order:
    //   1. rng.NextItem(otherChars)           — pick another character for SeaGlass
    //   2. rng.NextFloat()                    — decide PrismaticGem (< 0.3333333f)
    //   3. rng.NextItem(pool1, 4 items)       — pick from 3 base + 1 mixed
    //   4. rng.NextItem(OptionPool2, 3 items) — pick pool2
    //   5. rng.NextItem(OptionPool3, 1~2 items) — pick pool3 (conditional)
    private static List<AncientOptionResult> SimulateOrobas(Player player, Rng rng)
    {
        // 1. Pick another character (for SeaGlass)
        var otherChars = player.UnlockState.Characters
            .Where(c => c.Id != player.Character.Id)
            .ToList();
        _ = rng.NextItem(otherChars) ?? player.Character;

        // 2. PrismaticGem vs SeaGlass
        var pool1 = new List<RelicModel>
        {
            ModelDb.Relic<ElectricShrymp>().ToMutable(),
            ModelDb.Relic<GlassEye>().ToMutable(),
            ModelDb.Relic<SandCastle>().ToMutable()
        };
        RelicModel mixedItem;
        if (rng.NextFloat() < 0.3333333f)
            mixedItem = ModelDb.Relic<PrismaticGem>().ToMutable();
        else
            mixedItem = ModelDb.Relic<SeaGlass>().ToMutable();
        pool1.Add(mixedItem);

        // 3. Pick from pool1 (4 items)
        var slot1 = rng.NextItem(pool1)!;

        // 4. Pool2: AlchemicalCoffer / Driftwood / RadiantPearl
        var pool2 = new List<RelicModel>
        {
            ModelDb.Relic<AlchemicalCoffer>().ToMutable(),
            ModelDb.Relic<Driftwood>().ToMutable(),
            ModelDb.Relic<RadiantPearl>().ToMutable()
        };
        var slot2 = rng.NextItem(pool2)!;

        // 5. Pool3: conditional TouchOfOrobas / ArchaicTooth
        var (pool3, pool3Notes) = BuildOrobasPool3(player);
        if (pool3.Count == 0)
        {
            return new List<AncientOptionResult>
            {
                RelicToOption(slot1),
                RelicToOption(slot2),
                new AncientOptionResult("(locked)", "(locked)")
            };
        }

        var slot3 = rng.NextItem(pool3)!;
        pool3Notes.TryGetValue(slot3.Id.Entry, out var note3);

        return new List<AncientOptionResult>
        {
            RelicToOption(slot1),
            RelicToOption(slot2),
            RelicToOption(slot3, note3)
        };
    }

    private static (List<RelicModel> pool, Dictionary<string, string> notes) BuildOrobasPool3(Player player)
    {
        var pool = new List<RelicModel>();
        var notes = new Dictionary<string, string>();

        // TouchOfOrobas: player has a Starter-rarity relic
        var starterRelic = player.Relics.FirstOrDefault(r => r.Rarity == RelicRarity.Starter);
        if (starterRelic != null)
        {
            var touch = ModelDb.Relic<TouchOfOrobas>().ToMutable();
            pool.Add(touch);
            var starterName = starterRelic.Title.GetFormattedText();
            notes["TouchOfOrobas"] = $"→ {starterName}";
        }

        // ArchaicTooth: deck has a Transcendence starter card
        var transcendenceKeys = new HashSet<string> { "Bash", "Neutralize", "Unleash", "FallingStar", "Dualcast" };
        bool hasTranscendenceCard = player.Deck.Cards.Any(c => transcendenceKeys.Contains(c.Id.Entry));
        if (hasTranscendenceCard)
        {
            var tooth = ModelDb.Relic<ArchaicTooth>().ToMutable();
            pool.Add(tooth);
        }

        return (pool, notes);
    }

    // ======================== Pael ========================
    // Source code GenerateInitialOptions order:
    //   1. rng.NextItem(OptionPool1, 3 items)
    //   2. Build pool2: [PaelsWing] + conditional PaelsClaw/PaelsTooth → AddRange(self) → + PaelsGrowth
    //      rng.NextItem(pool2)
    //   3. Build pool3: [PaelsEye, PaelsBlood] + conditional PaelsLegion
    //      rng.NextItem(pool3)
    private static List<AncientOptionResult> SimulatePael(Player player, Rng rng)
    {
        // Pool1
        var pool1 = new List<RelicModel>
        {
            ModelDb.Relic<PaelsFlesh>().ToMutable(),
            ModelDb.Relic<PaelsHorn>().ToMutable(),
            ModelDb.Relic<PaelsTears>().ToMutable()
        };
        var slot1 = rng.NextItem(pool1)!;

        // Pool2
        var pool2 = new List<RelicModel> { ModelDb.Relic<PaelsWing>().ToMutable() };
        var cards = player.Deck.Cards;
        bool hasGoopy = cards.Count(c => ModelDb.Enchantment<Goopy>().CanEnchant(c)) >= 3;
        bool hasRemovable5 = cards.Count(c => c.IsRemovable) >= 5;
        if (hasGoopy) pool2.Add(ModelDb.Relic<PaelsClaw>().ToMutable());
        if (hasRemovable5) pool2.Add(ModelDb.Relic<PaelsTooth>().ToMutable());
        // Source: list.AddRange(list) — double the list, then add PaelsGrowth
        pool2.AddRange(pool2.ToList());
        pool2.Add(ModelDb.Relic<PaelsGrowth>().ToMutable());
        var slot2 = rng.NextItem(pool2)!;

        // Pool3
        var pool3 = new List<RelicModel>
        {
            ModelDb.Relic<PaelsEye>().ToMutable(),
            ModelDb.Relic<PaelsBlood>().ToMutable()
        };
        bool hasPet = player.HasEventPet();
        if (!hasPet) pool3.Add(ModelDb.Relic<PaelsLegion>().ToMutable());
        var slot3 = rng.NextItem(pool3)!;

        return new List<AncientOptionResult>
        {
            RelicToOption(slot1),
            RelicToOption(slot2),
            RelicToOption(slot3)
        };
    }

    // ======================== Tezcatara ========================
    // Source: rng.NextItem x 3 pools, simplest
    private static List<AncientOptionResult> SimulateTezcatara(Rng rng)
    {
        var pool1 = new List<RelicModel>
        {
            ModelDb.Relic<NutritiousSoup>().ToMutable(),
            ModelDb.Relic<VeryHotCocoa>().ToMutable(),
            ModelDb.Relic<YummyCookie>().ToMutable()
        };
        var pool2 = new List<RelicModel>
        {
            ModelDb.Relic<BiiigHug>().ToMutable(),
            ModelDb.Relic<Storybook>().ToMutable(),
            ModelDb.Relic<SealOfGold>().ToMutable(),
            ModelDb.Relic<ToastyMittens>().ToMutable()
        };
        var pool3 = new List<RelicModel>
        {
            ModelDb.Relic<GoldenCompass>().ToMutable(),
            ModelDb.Relic<PumpkinCandle>().ToMutable(),
            ModelDb.Relic<ToyBox>().ToMutable()
        };
        return new List<AncientOptionResult>
        {
            RelicToOption(rng.NextItem(pool1)!),
            RelicToOption(rng.NextItem(pool2)!),
            RelicToOption(rng.NextItem(pool3)!)
        };
    }

    // ======================== Nonupeipe ========================
    // Source: base pool (9) + conditional BeautifulBracelet → UnstableShuffle → Take(3)
    private static List<AncientOptionResult> SimulateNonupeipe(Player player, Rng rng)
    {
        var pool = new List<RelicModel>
        {
            ModelDb.Relic<BlessedAntler>().ToMutable(),
            ModelDb.Relic<BrilliantScarf>().ToMutable(),
            ModelDb.Relic<DelicateFrond>().ToMutable(),
            ModelDb.Relic<DiamondDiadem>().ToMutable(),
            ModelDb.Relic<FurCoat>().ToMutable(),
            ModelDb.Relic<Glitter>().ToMutable(),
            ModelDb.Relic<JewelryBox>().ToMutable(),
            ModelDb.Relic<LoomingFruit>().ToMutable(),
            ModelDb.Relic<SignetRing>().ToMutable()
        };
        // Condition: Swift enchantable cards >= 4
        int swiftCount = player.Deck.Cards.Count(c => ModelDb.Enchantment<Swift>().CanEnchant(c));
        bool canBracelet = swiftCount >= 4;
        if (canBracelet) pool.Add(ModelDb.Relic<BeautifulBracelet>().ToMutable());

        pool.UnstableShuffle(rng);
        var chosen = pool.Take(3).ToList();

        return chosen.Select(r => RelicToOption(r,
            r.Id.Entry == "BeautifulBracelet" ? $"Swift x{swiftCount} >= 4" : null)).ToList();
    }

    // ======================== Tanx ========================
    // Source: base pool (9) + conditional TriBoomerang → UnstableShuffle → Take(3)
    private static List<AncientOptionResult> SimulateTanx(Player player, Rng rng)
    {
        var pool = new List<RelicModel>
        {
            ModelDb.Relic<Claws>().ToMutable(),
            ModelDb.Relic<Crossbow>().ToMutable(),
            ModelDb.Relic<IronClub>().ToMutable(),
            ModelDb.Relic<MeatCleaver>().ToMutable(),
            ModelDb.Relic<Sai>().ToMutable(),
            ModelDb.Relic<SpikedGauntlets>().ToMutable(),
            ModelDb.Relic<TanxsWhistle>().ToMutable(),
            ModelDb.Relic<ThrowingAxe>().ToMutable(),
            ModelDb.Relic<WarHammer>().ToMutable()
        };
        // Condition: Instinct enchantable cards >= 3
        int instinctCount = player.Deck.Cards.Count(c => ModelDb.Enchantment<Instinct>().CanEnchant(c));
        bool canApex = instinctCount >= 3;
        if (canApex) pool.Add(ModelDb.Relic<TriBoomerang>().ToMutable());

        pool.UnstableShuffle(rng);
        var chosen = pool.Take(3).ToList();

        return chosen.Select(r => RelicToOption(r,
            r.Id.Entry == "TriBoomerang" ? $"Instinct x{instinctCount} >= 3" : null)).ToList();
    }

    // ======================== Vakuu ========================
    // Source: 3 pools each UnstableShuffle → pick [0]
    private static List<AncientOptionResult> SimulateVakuu(Rng rng)
    {
        var pool1 = new List<RelicModel>
        {
            ModelDb.Relic<BloodSoakedRose>().ToMutable(),
            ModelDb.Relic<WhisperingEarring>().ToMutable(),
            ModelDb.Relic<Fiddle>().ToMutable()
        };
        var pool2 = new List<RelicModel>
        {
            ModelDb.Relic<PreservedFog>().ToMutable(),
            ModelDb.Relic<SereTalon>().ToMutable(),
            ModelDb.Relic<DistinguishedCape>().ToMutable()
        };
        var pool3 = new List<RelicModel>
        {
            ModelDb.Relic<ChoicesParadox>().ToMutable(),
            ModelDb.Relic<MusicBox>().ToMutable(),
            ModelDb.Relic<LordsParasol>().ToMutable(),
            ModelDb.Relic<JeweledMask>().ToMutable()
        };

        pool1.UnstableShuffle(rng);
        pool2.UnstableShuffle(rng);
        pool3.UnstableShuffle(rng);

        return new List<AncientOptionResult>
        {
            RelicToOption(pool1[0]),
            RelicToOption(pool2[0]),
            RelicToOption(pool3[0])
        };
    }

    // ======================== Darv ========================
    // Source _validRelicSets (static, 9 sets):
    //   0: [Astrolabe]                          — always
    //   1: [BlackStar]                          — always
    //   2: [CallingBell]                        — always
    //   3: [EmptyCage]                          — always
    //   4: [PandorasBox]                        — filter: !Modifiers.Any(m => m.ClearsPlayerDeck)
    //   5: [RunicPyramid]                       — always
    //   6: [SneckoEye]                          — always
    //   7: [Ectoplasm, Sozu]                    — filter: CurrentActIndex == 1
    //   8: [PhilosophersStone, VelvetChoker]     — filter: CurrentActIndex == 2
    //
    // GenerateInitialOptions order:
    //   1. For each passing filter set: rng.NextItem(set.relics) → collect to list
    //   2. list.UnstableShuffle(rng)
    //   3. rng.NextBool() → 50% DustyTome
    //   4. If DustyTome: take first 2 + DustyTome; else take first 3
    //
    // [Fix] Uses actIndex parameter instead of player.RunState.CurrentActIndex
    //       because prediction may run before the player reaches that act
    private static List<AncientOptionResult> SimulateDarv(Player player, Rng rng, int actIndex)
    {
        var allSets = new List<(Func<bool> filter, List<RelicModel> relics)>
        {
            (() => true, new List<RelicModel> { ModelDb.Relic<Astrolabe>().ToMutable() }),
            (() => true, new List<RelicModel> { ModelDb.Relic<BlackStar>().ToMutable() }),
            (() => true, new List<RelicModel> { ModelDb.Relic<CallingBell>().ToMutable() }),
            (() => true, new List<RelicModel> { ModelDb.Relic<EmptyCage>().ToMutable() }),
            (() => !player.RunState.Modifiers.Any(m => m.ClearsPlayerDeck),
                new List<RelicModel> { ModelDb.Relic<PandorasBox>().ToMutable() }),
            (() => true, new List<RelicModel> { ModelDb.Relic<RunicPyramid>().ToMutable() }),
            (() => true, new List<RelicModel> { ModelDb.Relic<SneckoEye>().ToMutable() }),
            (() => actIndex == 1,
                new List<RelicModel> { ModelDb.Relic<Ectoplasm>().ToMutable(), ModelDb.Relic<Sozu>().ToMutable() }),
            (() => actIndex == 2,
                new List<RelicModel> { ModelDb.Relic<PhilosophersStone>().ToMutable(), ModelDb.Relic<VelvetChoker>().ToMutable() }),
        };

        // Source: (from rs in _validRelicSets where rs.filter(Owner) select ...).ToList()
        var pickedRelics = new List<RelicModel>();
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
                .Select(r => RelicToOption(r))
                .ToList();
            result.Add(RelicToOption(ModelDb.Relic<DustyTome>().ToMutable(), "50%"));
        }
        else
        {
            result = pickedRelics.Take(3)
                .Select(r => RelicToOption(r))
                .ToList();
        }

        return result;
    }

    // ------------------------------------------------------------------
    // Helper: Create an AncientOptionResult from a RelicModel
    // Uses model.Title.GetFormattedText() for proper localization
    // ------------------------------------------------------------------
    private static AncientOptionResult RelicToOption(RelicModel model, string? note = null)
    {
        var localizedName = model.Title.GetFormattedText();
        return new AncientOptionResult(model.Id.Entry, localizedName, note);
    }

    /// <summary>
    /// Clear cache (call when entering a new run or loading a save).
    /// </summary>
    public static void ClearCache() => _cache.Clear();
}
