using System.Globalization;
using System.Text;

namespace Arkive.Core.Tweaks;

/// <summary>
/// An effect astrogem processing can offer (EFTable_ArkGridGemPotential) and its weight in the draw
/// (EFTable_ArkGridGemPotentialGroup). Type 0 keeps the gem as it is, 1 finishes processing, 2 Willpower Efficiency,
/// 3 Order/Chaos Points, 4 an option's level (ValueA 0 or 1 is the first or second option), 5 changes an option,
/// 6 changes the processing cost (ValueB, in hundredths of a percent), 7 adds rerolls (ValueB).
/// </summary>
public sealed record AstrogemEffect(int Id, int Type, int ValueA, int ValueB, int Weight);

/// <summary>A gem grade's processing (EFTable_ArkGridGemProcess): attempts, free rerolls, resets and cost per attempt.</summary>
public sealed record AstrogemGrade(int Attempts, int Rerolls, int Resets, int Cost);

/// <summary>The processing rules the game's tables give: the effects of the draw, the grades and the cost limits.</summary>
public sealed record AstrogemRules(IReadOnlyList<AstrogemEffect> Effects, IReadOnlyList<AstrogemGrade> Grades, int MinCostAddend, int MaxCostAddend);

/// <summary>
/// What the player wants from a gem: Willpower Efficiency at least <see cref="Willpower"/>, Order/Chaos Points at least
/// <see cref="Points"/>, and the four levels together (the total that decides the rank) at least <see cref="Total"/>.
/// </summary>
public sealed record AstrogemTarget(string Id, string Label, int Willpower, int Points, int Total)
{
    public bool Reached(int willpower, int points, int first, int second) =>
        willpower >= Willpower && points >= Points && willpower + points + first + second >= Total;
}

/// <summary>
/// The chance to reach a target and the gold it takes, for every state a gem can be in while it's processed, with the
/// best play: process, view other items (reroll the four offered effects), reset, or stop.
/// <para>
/// The draw: four different effects are offered, drawn one after another by weight from the effects that can apply,
/// and processing applies one of the four, 25% each. An effect can't apply when it would take a level past 1–5, a cost
/// change past the cost limits, and cost changes and extra rerolls aren't offered on the last attempt. Rerolls and resets
/// need one attempt done. A reset brings the gem back to all levels 1 with every attempt and reroll. The weights, limits
/// and counts come from the game's tables; the draw itself is what the game's text and community calculators describe.
/// </para>
/// </summary>
public static class AstrogemOdds
{
    private const string Tables = "EFTable_ArkGridGemProcess.db";
    public const int Levels = 5;
    public const int States = Levels * Levels * Levels * Levels;
    /// <summary>Rerolls past this many count as this many (more than four left is rare and changes little).</summary>
    public const int RerollCap = 4;
    private const int Offered = 4;
    private const double Same = 1e-9;

    public static IReadOnlyList<AstrogemTarget> Targets { get; } =
    [
        new("w4p5", "Willpower Efficiency 4+ and Points 5", 4, 5, 0),
        new("w5p5", "Willpower Efficiency 5 and Points 5", 5, 5, 0),
        new("w5p4", "Willpower Efficiency 5 and Points 4+", 5, 4, 0),
        new("w4p4", "Willpower Efficiency 4+ and Points 4+", 4, 4, 0),
        new("p5", "Points 5", 0, 5, 0),
        new("relic", "Relic rank (total 16+)", 0, 0, 16),
        new("ancient", "Ancient rank (total 19+)", 0, 0, 19),
    ];

    public static AstrogemTarget Target(string? id) => Targets.FirstOrDefault(target => target.Id == id) ?? Targets[0];

    /// <summary>Reads the rules from data2.lpk: the gold-cost grades and the effect group they draw from.</summary>
    public static AstrogemRules Read(string data2Path, string workDirectory) =>
        CombatPowerTable.WithDatabases(data2Path, workDirectory,
            [Tables, "EFTable_ArkGridGemPotential.db", "EFTable_ArkGridGemPotentialGroup.db"], connection =>
            {
                var grades = new SortedDictionary<int, AstrogemGrade>();
                var groups = new Dictionary<int, int>();
                int minAddend = 0, maxAddend = 0;
                using (var command = connection.CreateCommand())
                {
                    // Cost type 2 is gold, the gems players process; type 1 rows are a test currency.
                    command.CommandText = "SELECT ProcessCount, PotentialRerollCount, ResetCount, ProcessCostAmount, PotentialGroupId, " +
                                          "MinProcessCostAddendRatio, MaxProcessCostAddendRatio FROM ArkGridGemProcess WHERE ProcessCostType = 2";
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        grades[reader.GetInt32(0)] = new(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3));
                        groups[reader.GetInt32(4)] = groups.GetValueOrDefault(reader.GetInt32(4)) + 1;
                        minAddend = Math.Min(minAddend, reader.GetInt32(5));
                        maxAddend = Math.Max(maxAddend, reader.GetInt32(6));
                    }
                }
                if (groups.Count == 0) throw new InvalidDataException("ArkGridGemProcess has no gold-cost rows.");
                var group = groups.MaxBy(pair => pair.Value).Key;
                var effects = new List<AstrogemEffect>();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT p.PrimaryKey, p.Type, p.ValueA, p.ValueB, g.PickupRatio FROM db2.ArkGridGemPotentialGroup g " +
                                          "JOIN db1.ArkGridGemPotential p ON p.PrimaryKey = g.PotentialId WHERE g.PrimaryKey = $group ORDER BY g.SecondaryKey";
                    command.Parameters.AddWithValue("$group", group);
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                        effects.Add(new(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4)));
                }
                return new AstrogemRules(effects, grades.Values.ToArray(), minAddend, maxAddend);
            });

    /// <summary>
    /// Writes the target's table for the tweak (see <see cref="Encode"/>) into <paramref name="folder"/> and returns its
    /// path. Kept per data2.lpk size and time and per target, since working it out takes a few seconds.
    /// </summary>
    public static string WriteTable(string data2Path, string workDirectory, string folder, AstrogemTarget target)
    {
        var file = new FileInfo(data2Path);
        var path = Path.Combine(folder, $"{target.Id}-{file.Length}-{file.LastWriteTimeUtc.Ticks}.txt");
        if (File.Exists(path)) return path;
        var table = Compute(Read(data2Path, workDirectory), target);
        Directory.CreateDirectory(folder);
        foreach (var stale in Directory.EnumerateFiles(folder, $"{target.Id}-*.txt")) File.Delete(stale);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, Encode(table), new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
        return path;
    }

    /// <summary>The chance and gold for one target. Index a table with <see cref="AstrogemTable.Index"/>.</summary>
    public static AstrogemTable Compute(AstrogemRules rules, AstrogemTarget target)
    {
        var model = new Model(rules, target);
        var maxAttempts = rules.Grades.Max(grade => grade.Attempts);
        var noReset = model.NewTable(maxAttempts);
        for (var n = 1; n <= maxAttempts; n++)
            model.Fill(n, [noReset], [(0, 0)]);
        var grades = rules.Grades.Select(grade =>
        {
            var (chance, gold) = model.Fresh(grade, noReset);
            return new AstrogemGradeOdds(grade, chance, gold, grade.Resets > 0 ? model.NewTable(grade.Attempts) : null);
        }).ToArray();
        for (var n = 1; n <= maxAttempts; n++)
        {
            var open = grades.Where(grade => grade.WithReset is not null && grade.Grade.Attempts >= n).ToArray();
            if (open.Length > 0)
                model.Fill(n, open.Select(grade => grade.WithReset!).ToArray(), open.Select(grade => (grade.FreshChance, grade.FreshGold)).ToArray());
        }
        return new AstrogemTable(target, rules, model.CostStates, model.CostLow, model.CostStep, noReset, grades);
    }

    private const string Digits = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz-_";
    public const int ChanceScale = 4095;
    /// <summary>Gold is kept in attempts at the base cost, in steps of 1/200 (up to 20 attempts).</summary>
    public const int GoldScale = 200;

    /// <summary>
    /// The table as the tweak reads it: lines "arkive-astrogem 1", "target id W P total", "rules levels rerollCap
    /// costStates costLow costStep" (cost addends in hundredths of a percent: state s is costLow + s × costStep),
    /// "grade attempts rerolls resets cost freshChance freshGold" per grade, then "table none|attempts" lines whose
    /// text holds 4 characters per state from <see cref="AstrogemTable.Index"/>: chance × 4095 and gold × 200 (in attempts
    /// at the base cost), each 2 digits of the 64 in "0-9A-Za-z-_".
    /// </summary>
    public static string Encode(AstrogemTable table)
    {
        var text = new StringBuilder();
        text.Append("arkive-astrogem 1\n");
        text.Append(CultureInfo.InvariantCulture, $"target {table.Target.Id} {table.Target.Willpower} {table.Target.Points} {table.Target.Total}\n");
        text.Append(CultureInfo.InvariantCulture, $"rules {Levels} {RerollCap} {table.CostStates} {table.CostLow} {table.CostStep}\n");
        foreach (var grade in table.Grades)
            text.Append(CultureInfo.InvariantCulture,
                $"grade {grade.Grade.Attempts} {grade.Grade.Rerolls} {grade.Grade.Resets} {grade.Grade.Cost} {grade.FreshChance:0.#####} {grade.FreshGold:0.###}\n");
        Write(text, "none", table.NoReset);
        foreach (var grade in table.Grades)
            if (grade.WithReset is { } withReset)
                Write(text, grade.Grade.Attempts.ToString(CultureInfo.InvariantCulture), withReset);
        return text.ToString();
    }

    private static void Write(StringBuilder text, string name, AstrogemStateTable table)
    {
        text.Append("table ").Append(name).Append(' ');
        for (var i = 0; i < table.Chance.Length; i++)
        {
            Pair(text, (int)Math.Round(Math.Clamp(table.Chance[i], 0, 1) * ChanceScale));
            Pair(text, (int)Math.Round(Math.Clamp(table.Gold[i] * GoldScale, 0, ChanceScale)));
        }
        text.Append('\n');
    }

    private static void Pair(StringBuilder text, int value) => text.Append(Digits[value >> 6]).Append(Digits[value & 63]);

    /// <summary>The search: one instance per target.</summary>
    private sealed class Model
    {
        private readonly AstrogemTarget target;
        private readonly Effect[] effects;
        public readonly int CostStates;
        public readonly int CostLow;
        public readonly int CostStep;
        private readonly bool[] reached = new bool[States];

        internal readonly record struct Effect(int Stat, int Delta, int Cost, int Rerolls, bool Finish, int Weight);

        public Model(AstrogemRules rules, AstrogemTarget target)
        {
            this.target = target;
            effects = rules.Effects.Where(effect => effect.Weight > 0).Select(effect => effect.Type switch
            {
                2 => new Effect(0, effect.ValueB, 0, 0, false, effect.Weight),
                3 => new Effect(1, effect.ValueB, 0, 0, false, effect.Weight),
                4 => new Effect(2 + Math.Clamp(effect.ValueA, 0, 1), effect.ValueB, 0, 0, false, effect.Weight),
                6 => new Effect(-1, 0, effect.ValueB, 0, false, effect.Weight),
                7 => new Effect(-1, 0, 0, effect.ValueB, false, effect.Weight),
                1 => new Effect(-1, 0, 0, 0, true, effect.Weight),
                _ => new Effect(-1, 0, 0, 0, false, effect.Weight),
            }).ToArray();
            var costChanges = effects.Where(effect => effect.Cost != 0).Select(effect => Math.Abs(effect.Cost)).ToArray();
            CostStep = costChanges.Length == 0 ? 10000 : costChanges.Aggregate(Gcd);
            CostLow = rules.MinCostAddend / CostStep;
            CostStates = rules.MaxCostAddend / CostStep - CostLow + 1;
            effects = effects.Select(effect => effect with { Cost = effect.Cost / CostStep }).ToArray();
            for (var state = 0; state < States; state++)
                reached[state] = target.Reached(Level(state, 0), Level(state, 1), Level(state, 2), Level(state, 3));
        }

        private static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);

        public static int Level(int state, int stat) => state / Power(stat) % Levels + 1;

        private static int Power(int stat) => stat switch { 0 => 1, 1 => Levels, 2 => Levels * Levels, _ => Levels * Levels * Levels };

        public AstrogemStateTable NewTable(int attempts)
        {
            var size = (attempts + 1) * (RerollCap + 1) * CostStates * States;
            return new AstrogemStateTable(attempts, new double[size], new double[size]);
        }

        private int Index(int attempts, int rerolls, int cost) => ((attempts * (RerollCap + 1) + rerolls) * CostStates + cost) * States;

        /// <summary>The cost of one attempt, in attempts at the base cost.</summary>
        private double Price(int cost) => 1 + (CostLow + cost) * CostStep / 10000.0;

        /// <summary>
        /// Fills <paramref name="attempts"/> of each table; alternative i is the reset each table offers (chance and gold
        /// of a fresh gem), (0, 0) when it has none. The tables for fewer attempts must be filled already.
        /// </summary>
        public void Fill(int attempts, AstrogemStateTable[] tables, (double Chance, double Gold)[] resets)
        {
            var last = attempts == 1;
            Parallel.For(0, CostStates * States, () => new Draws(), (job, _, draws) =>
            {
                var cost = job / States;
                var state = job % States;
                if (reached[state])
                {
                    foreach (var table in tables)
                        for (var rerolls = 0; rerolls <= RerollCap; rerolls++)
                            table.Chance[Index(attempts, rerolls, cost) + state] = 1;
                    return draws;
                }
                draws.Build(effects, state, cost, CostStates, last);
                for (var t = 0; t < tables.Length; t++)
                {
                    var table = tables[t];
                    for (var rerolls = 0; rerolls <= RerollCap; rerolls++)
                    {
                        draws.Next(this, table, state, attempts, rerolls, cost);
                        var (bestChance, bestGold) = (0.0, 0.0);
                        if (rerolls > 0)
                            Better(ref bestChance, ref bestGold, table.Chance[Index(attempts, rerolls - 1, cost) + state],
                                table.Gold[Index(attempts, rerolls - 1, cost) + state]);
                        Better(ref bestChance, ref bestGold, resets[t].Chance, resets[t].Gold);
                        var (chance, gold) = draws.Expect(Price(cost), bestChance, bestGold);
                        table.Chance[Index(attempts, rerolls, cost) + state] = chance;
                        table.Gold[Index(attempts, rerolls, cost) + state] = gold;
                    }
                }
                return draws;
            }, _ => { });
        }

        /// <summary>A fresh gem of the grade: all levels 1, nothing done yet, so no reroll or reset until it's processed once.</summary>
        public (double Chance, double Gold) Fresh(AstrogemGrade grade, AstrogemStateTable after)
        {
            var draws = new Draws();
            var cost = -CostLow;
            if (reached[0]) return (1, 0);
            draws.Build(effects, 0, cost, CostStates, grade.Attempts == 1);
            draws.Next(this, after, 0, grade.Attempts, Math.Min(grade.Rerolls, RerollCap), cost);
            return draws.Expect(Price(cost), 0, 0);
        }

        private static void Better(ref double chance, ref double gold, double otherChance, double otherGold)
        {
            if (otherChance > chance + Same || (otherChance > chance - Same && otherGold < gold))
                (chance, gold) = (otherChance, otherGold);
        }

        /// <summary>The chance and gold after one effect applies, taking an attempt.</summary>
        public (double Chance, double Gold) After(AstrogemStateTable table, int state, int attempts, int rerolls, int cost, int effectIndex)
        {
            var effect = effects[effectIndex];
            if (effect.Stat >= 0) state += effect.Delta * Power(effect.Stat);
            if (reached[state]) return (1, 0);
            if (attempts == 1 || effect.Finish) return (0, 0);
            var index = Index(attempts - 1, Math.Min(RerollCap, rerolls + effect.Rerolls), cost + effect.Cost) + state;
            return (table.Chance[index], table.Gold[index]);
        }

        public static bool CanApply(Effect effect, int state, int cost, int costStates, bool last)
        {
            if (effect.Stat >= 0)
            {
                var level = Level(state, effect.Stat) + effect.Delta;
                return level is >= 1 and <= Levels;
            }
            if (effect.Cost != 0) return !last && cost + effect.Cost >= 0 && cost + effect.Cost < costStates;
            if (effect.Rerolls != 0) return !last;
            return true;
        }

        /// <summary>The offers one state can draw (every set of four different effects and its chance), per thread.</summary>
        private sealed class Draws
        {
            private readonly int[] offer = new int[32];
            private readonly double[] weights = new double[32];
            private readonly double[] chance = new double[32];
            private readonly double[] gold = new double[32];
            private int count;
            private readonly List<(int A, int B, int C, int D, double P)> sets = new(17550);

            public void Build(Effect[] effects, int state, int cost, int costStates, bool last)
            {
                count = 0;
                for (var i = 0; i < effects.Length; i++)
                    if (CanApply(effects[i], state, cost, costStates, last))
                    {
                        weights[count] = effects[i].Weight;
                        offer[count++] = i;
                    }
                sets.Clear();
                var total = 0.0;
                for (var i = 0; i < count; i++) total += weights[i];
                if (count <= Offered)
                {
                    sets.Add((0, Math.Min(1, count - 1), Math.Min(2, count - 1), Math.Min(3, count - 1), 1));
                    return;
                }
                Span<double> w = stackalloc double[4];
                for (var a = 0; a < count; a++)
                for (var b = a + 1; b < count; b++)
                for (var c = b + 1; c < count; c++)
                for (var d = c + 1; d < count; d++)
                {
                    w[0] = weights[a]; w[1] = weights[b]; w[2] = weights[c]; w[3] = weights[d];
                    sets.Add((a, b, c, d, Ordered(w, total)));
                }
            }

            /// <summary>The chance that four draws without replacement give exactly these four, in any order.</summary>
            private static double Ordered(Span<double> w, double total)
            {
                var sum = 0.0;
                for (var i = 0; i < 4; i++)
                for (var j = 0; j < 4; j++)
                {
                    if (j == i) continue;
                    for (var k = 0; k < 4; k++)
                    {
                        if (k == i || k == j) continue;
                        var l = 6 - i - j - k;
                        var t1 = total - w[i];
                        var t2 = t1 - w[j];
                        var t3 = t2 - w[k];
                        sum += w[i] / total * (w[j] / t1) * (w[k] / t2) * (w[l] / t3);
                    }
                }
                return sum;
            }

            public void Next(Model model, AstrogemStateTable table, int state, int attempts, int rerolls, int cost)
            {
                for (var i = 0; i < count; i++)
                    (chance[i], gold[i]) = model.After(table, state, attempts, rerolls, cost, offer[i]);
            }

            /// <summary>Over every offer: process (25% each of the four), or the best other move when it's better.</summary>
            public (double Chance, double Gold) Expect(double price, double otherChance, double otherGold)
            {
                if (count < Offered)
                {
                    var c = 0.0;
                    var g = 0.0;
                    for (var i = 0; i < count; i++) { c += chance[i]; g += gold[i]; }
                    c /= count;
                    g = price + g / count;
                    return c > otherChance + Same || (c > otherChance - Same && g < otherGold) ? (c, g) : (otherChance, otherGold);
                }
                double totalChance = 0, totalGold = 0;
                foreach (var (a, b, c, d, p) in sets)
                {
                    var processChance = (chance[a] + chance[b] + chance[c] + chance[d]) * 0.25;
                    var processGold = price + (gold[a] + gold[b] + gold[c] + gold[d]) * 0.25;
                    if (processChance > otherChance + Same || (processChance > otherChance - Same && processGold < otherGold))
                    {
                        totalChance += p * processChance;
                        totalGold += p * processGold;
                    }
                    else
                    {
                        totalChance += p * otherChance;
                        totalGold += p * otherGold;
                    }
                }
                return (totalChance, totalGold);
            }
        }
    }
}

/// <summary>Chance and gold per state; see <see cref="AstrogemTable.Index"/>.</summary>
public sealed record AstrogemStateTable(int Attempts, double[] Chance, double[] Gold);

/// <summary>A grade's odds from a fresh gem (without its reset) and, when it has a reset, its table with the reset unused.</summary>
public sealed record AstrogemGradeOdds(AstrogemGrade Grade, double FreshChance, double FreshGold, AstrogemStateTable? WithReset);

public sealed record AstrogemTable(
    AstrogemTarget Target,
    AstrogemRules Rules,
    int CostStates,
    int CostLow,
    int CostStep,
    AstrogemStateTable NoReset,
    IReadOnlyList<AstrogemGradeOdds> Grades)
{
    /// <summary>
    /// A state's place: attempts left, rerolls left (at most <see cref="AstrogemOdds.RerollCap"/>), cost state (0 is the
    /// lowest addend) and levels (Willpower Efficiency + 5 × Points + 25 × first option + 125 × second option, each level
    /// minus 1). The chance and gold are before the four offered effects are seen, with the best play from there.
    /// </summary>
    public int Index(int attempts, int rerolls, int cost, int willpower, int points, int first, int second) =>
        ((attempts * (AstrogemOdds.RerollCap + 1) + rerolls) * CostStates + cost) * AstrogemOdds.States +
        (willpower - 1) + AstrogemOdds.Levels * (points - 1) + 25 * (first - 1) + 125 * (second - 1);
}
