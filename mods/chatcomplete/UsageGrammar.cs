using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

// Turns the live command table's human usage strings into a small state machine:
// for every literal prefix after a command, the tokens that may come next.
//
//   command<TAB>space-separated literal prefix<TAB>one|two|@catalog
//
// Literals are typed words; @kind means "values of that catalog". The walk stops at a
// free-text or numeric placeholder, so only finite choices are emitted.
public static class UsageGrammar
{
    sealed class Step
    {
        public string? Literal;
        public List<List<Step>>? Choices;
        public bool Optional;
        public string? Placeholder;
        public string? Catalog;
        public bool Variadic;
    }

    // A single optional word that names a value, not a keyword: [speed], [count], [name].
    static readonly HashSet<string> ValueWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "name", "id", "count", "n", "level", "quality", "seed", "radius", "speed",
        "filter", "max", "amount", "damage", "index", "type", "section", "row", "node",
        "value", "values", "text", "message", "seconds", "percent", "chance", "number",
        "part", "path", "time", "x", "y", "a", "b", "degrees", "mod", "rarity", "base",
        "item", "gem", "area", "monster", "stat", "npc", "quest", "flag", "mode",
        "channel", "page", "skill", "passive", "tab", "stash", "slot", "sockets",
        "theme", "passives", "effectid",
    };

    static readonly HashSet<string> KeywordWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "on", "off", "all", "none", "add", "set", "remove", "clear", "list", "show",
        "open", "refund", "boss", "near", "portal", "ranged", "freeze", "start", "stop",
        "reset", "reload", "enable", "disable", "create", "delete", "info", "status",
        "magic", "rare", "normal", "unique", "implicit", "explicit", "enchant",
        "scourge", "hidden", "small", "medium", "large", "physical", "fire", "cold",
        "lightning", "chaos", "attack", "spell", "both", "endurance", "frenzy", "power",
        "cruel", "merciless", "eternal",
    };

    // Usage prose does not spell out a few enums: fill the obvious ones by hand.
    static readonly Dictionary<string, (string Prefix, string Tokens)[]> Overrides = new(StringComparer.OrdinalIgnoreCase)
    {
        ["charges"] = [("add", "endurance|frenzy|power")],
        ["item_mod"] =
        [
            ("set", "implicit|explicit|enchant|scourge|hidden"),
            ("remove", "implicit|explicit|enchant|scourge|hidden"),
            ("clear", "implicit|explicit|enchant|scourge|hidden"),
        ],
        ["descendancy"] = [("", "name")],
        ["ascendancy"] = [("normal", "@ascendancy_normal"), ("phrecia", "@ascendancy_phrecia")],
        ["add_passive"] = [("", "@passive")],
        ["quest_preset"] = [("", "@quest_preset")],
        ["atlas_complete"] = [("", "@atlas")],
        ["tattoo"] = [("", "@tattoo")],
        ["tattoo_apply"] = [("", "@tattoo")],
        ["unique_jewel"] = [("", "@unique_jewel")],
        ["item_tab"] = [("", "@stash_tab")],
        ["cluster_jewel"] =
        [
            ("small", "@cluster_theme_small"),
            ("small *", "@cluster_theme_small"),
            ("small * *", "@cluster_theme_small"),
            ("medium", "@cluster_theme_medium"),
            ("medium *", "@cluster_theme_medium"),
            ("medium * *", "@cluster_theme_medium"),
            ("large", "@cluster_theme_large"),
            ("large *", "@cluster_theme_large"),
            ("large * *", "@cluster_theme_large"),
        ],
        ["mod_enable"] = [("", "@installed_mod")],
        ["mod_disable"] = [("", "@installed_mod")],
        ["mod_reload"] = [("", "@installed_mod")],
        ["module_reload"] = [("", "@installed_module")],
        // The scripted boss fights' own skills (gm-commands' /entity): the actor's GrantedEffects Ids, from the kinds the
        // mod writes per area, so no name is repeated here.
        ["entity"] = [("skill", "@entity_skill")],
        ["item_mod"] =
        [
            ("", "@item_mod"),
            ("set", "@item_mod"), ("set implicit", "@item_mod"), ("set explicit", "@item_mod"),
            ("set enchant", "@item_mod"), ("set scourge", "@item_mod"), ("set hidden", "@item_mod"),
            ("remove", "@item_mod"), ("remove implicit", "@item_mod"), ("remove explicit", "@item_mod"),
            ("remove enchant", "@item_mod"), ("remove scourge", "@item_mod"), ("remove hidden", "@item_mod"),
            ("implicit", "@item_mod"), ("explicit", "@item_mod"), ("enchant", "@item_mod"),
            ("scourge", "@item_mod"), ("hidden", "@item_mod"),
        ],
    };

    // Finite arguments that follow a catalog value. The state machine's prefix may
    // start with @kind; the DLL matches the typed words before the literal tail against
    // an exact value of that kind (a multi-word monster name, a tattoo name, ...).
    static readonly Dictionary<string, (string Prefix, string Tokens)[]> AfterValues = new(StringComparer.OrdinalIgnoreCase)
    {
        ["spawn_mob"] =
        [
            ("@monster", "friendly|normal|magic|rare|unique"),
            ("@monster friendly", "normal|magic|rare|unique"),
        ],
        ["item"] =
        [
            ("@item", "magic|rare|max"),
            ("@item *", "magic|rare|max"),
            ("@item * *", "max"),
        ],
        ["tattoo_apply"] = [("@tattoo", "@passive")],
        ["mob_mods"] = [("*", "@monster_mod")],
        ["hit"] = [("* *", "ranged|freeze")],
        // The entity id comes from this area (ChatComplete writes the kind from the connection's monsters), then the
        // verb, then a scripted boss's own skill names.
        ["entity"] =
        [
            ("@entity", "skills|skill|kill|life|anim|list"),
            ("@entity skill", "@entity_skill"),
        ],
    };

    public static (int Commands, int States) Write(
        string path,
        IEnumerable<(string Name, string Usage, string[] Arguments)> usages,
        Action<string> log,
        Func<string, IEnumerable<string>?>? declared = null)
    {
        var lines = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        int parsed = 0;

        foreach (var (name, usage, arguments) in usages)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;

            // The command declared its grammar: use its states instead of parsing the prose.
            IEnumerable<string>? declaredLines = null;
            try { declaredLines = declared?.Invoke(name); }
            catch (Exception ex) { log("grammar " + name + ": " + ex.Message); }
            if (declaredLines is not null)
            {
                bool any = false;
                foreach (string line in declaredLines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    int tab = line.IndexOf('\t');
                    if (tab < 0 || tab == line.Length - 1) continue;
                    lines.Add($"{name}\t{line}");
                    any = true;
                }
                if (any) parsed++;
                continue;
            }

            if (string.IsNullOrWhiteSpace(usage)) continue;
            var alternatives = ParseAlternatives(usage);
            if (alternatives.Count == 0) continue;
            var map = new SortedDictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var alternative in alternatives)
            {
                AssignFirstCatalog(alternative, arguments);
                WalkSequence(alternative, [], map, depth: 0);
            }
            if (Overrides.TryGetValue(name, out var overrides))
            {
                foreach (var (prefix, tokens) in overrides)
                {
                    var prefixPath = prefix.Length == 0 ? new List<string>() : prefix.Split(' ').ToList();
                    foreach (string token in tokens.Split('|', StringSplitOptions.RemoveEmptyEntries))
                        Add(map, prefixPath, token);
                }
            }
            if (AfterValues.TryGetValue(name, out var afterValues))
                foreach (var (prefix, tokens) in afterValues)
                    lines.Add($"{name}\t{prefix}\t{tokens}");
            if (map.Count == 0) continue;
            parsed++;
            foreach (var (prefix, suggestions) in map)
            {
                if (suggestions.Count == 0) continue;
                lines.Add($"{name}\t{prefix}\t{string.Join("|", suggestions)}");
            }
        }

        var text = new StringBuilder();
        text.AppendLine("# command<TAB>literal prefix<TAB>next tokens (| separated; @kind = catalog)");
        foreach (string line in lines) text.AppendLine(line);

        string content = text.ToString();
        bool changed;
        try { changed = !File.Exists(path) || File.ReadAllText(path) != content; }
        catch (IOException) { changed = true; }
        if (changed)
        {
            string temp = path + ".tmp";
            File.WriteAllText(temp, content);
            File.Move(temp, path, overwrite: true);
        }
        log($"grammar: {parsed} commands, {lines.Count} states");
        return (parsed, lines.Count);
    }

    static List<List<Step>> ParseAlternatives(string usage)
    {
        string text = StripProse(usage).Replace('…', '.').Trim();
        if (text.Length == 0) return [];
        var result = new List<List<Step>>();
        foreach (string raw in SplitTop(text, '|'))
        {
            string alternative = raw.Trim();
            if (alternative.Length == 0) continue;
            if (alternative[0] == '/')
            {
                int space = alternative.IndexOf(' ');
                alternative = space < 0 ? "" : alternative[(space + 1)..].Trim();
            }
            if (alternative.Length == 0) continue;
            var steps = Tokenize(alternative);
            if (steps.Count > 0) result.Add(steps);
        }
        return result;
    }

    // "(the modules loaded ...)" is prose; "<name, number or ...>" is a placeholder.
    static string StripProse(string usage)
    {
        var text = new StringBuilder(usage.Length);
        int depth = 0;
        for (int i = 0; i < usage.Length; i++)
        {
            char c = usage[i];
            if (c == '<' || c == '[') depth++;
            else if (c == '>' || c == ']') depth = Math.Max(0, depth - 1);
            if (c == '(' && depth == 0)
            {
                int end = usage.IndexOf(')', i + 1);
                if (end > i) { i = end; continue; }
            }
            text.Append(c);
        }
        return text.ToString();
    }

    static List<Step> Tokenize(string text)
    {
        var steps = new List<Step>();
        int i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i >= text.Length) break;
            char c = text[i];
            if (c == '[' || c == '<')
            {
                int end = MatchGroup(text, i);
                if (end < 0) break;
                string inner = text[(i + 1)..end];
                steps.Add(c == '[' ? ParseBracket(inner) : ParseAngle(inner));
                i = end + 1;
                continue;
            }
            int start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '[' && text[i] != '<')
                i++;
            string word = text[start..i].Trim(',', ';', '.', ':');
            if (word.Length == 0) continue;
            if (word is "..." or ".." || word.EndsWith("...", StringComparison.Ordinal))
            {
                steps.Add(new Step { Variadic = true });
                continue;
            }
            steps.Add(new Step { Literal = word });
        }
        return steps;
    }

    static int MatchGroup(string text, int open)
    {
        int depth = 0;
        char close = text[open] == '[' ? ']' : '>';
        for (int i = open; i < text.Length; i++)
        {
            if (text[i] == text[open]) depth++;
            else if (text[i] == close)
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    // <normal|cruel|all> is a choice; <area id or name> is one free value.
    static Step ParseAngle(string inner)
    {
        inner = inner.Trim();
        var parts = SplitTop(inner, '|');
        if (parts.Count > 1)
        {
            var choices = new List<List<Step>>();
            foreach (string raw in parts)
            {
                var steps = ParseChoicePart(raw);
                if (steps.Count > 0) choices.Add(steps);
            }
            return choices.Count > 0 ? new Step { Choices = choices } : new Step { Placeholder = inner };
        }
        return new Step { Placeholder = inner };
    }

    // [on|off] is an optional choice; [speed] a value; [ranged] a keyword; [...]
    // a variadic tail; [monster level] a value phrase.
    static Step ParseBracket(string inner)
    {
        inner = inner.Trim();
        if (inner.Length == 0 || inner is "..." or ".." ||
            inner.EndsWith("...", StringComparison.Ordinal))
            return new Step { Variadic = true, Optional = true };
        if (TryNumericChoices(inner, out var numeric))
            return new Step { Choices = numeric, Optional = true };
        if (LooksNumeric(inner)) return new Step { Placeholder = inner, Optional = true };

        var parts = SplitTop(inner, '|');
        if (parts.Count > 1)
        {
            var choices = new List<List<Step>>();
            foreach (string raw in parts)
            {
                var steps = ParseChoicePart(raw);
                if (steps.Count > 0) choices.Add(steps);
            }
            return choices.Count > 0
                ? new Step { Choices = choices, Optional = true }
                : new Step { Placeholder = inner, Optional = true };
        }

        var single = Tokenize(inner);
        if (single.Count == 0) return new Step { Placeholder = inner, Optional = true };
        if (single.Count == 1)
        {
            Step one = single[0];
            if (one.Placeholder != null || one.Variadic)
                return new Step { Choices = [single], Optional = true };
            if (one.Literal is { } word)
            {
                if (IsDateValue(word))
                    return new Step { Placeholder = word, Optional = true };
                if (ValueWords.Contains(word) && !KeywordWords.Contains(word))
                    return new Step { Placeholder = word, Optional = true };
                return new Step { Choices = [single], Optional = true };
            }
        }
        // More than one word and no alternatives: a value phrase such as [monster level].
        if (inner.Contains(' ') && !inner.Contains('<') && !inner.Contains('[') &&
            !inner.EndsWith("...", StringComparison.Ordinal))
            return new Step { Placeholder = inner, Optional = true };
        return new Step { Choices = [single], Optional = true };
    }

    // One alternative of a <...> or [...] choice: a value word (name, amount, node)
    // becomes a free placeholder, a keyword (normal, all, near) stays a literal.
    static List<Step> ParseChoicePart(string part)
    {
        part = part.Trim();
        if (part.Length == 0) return [];
        if (part.Contains(' ') && !part.Contains('<') && !part.Contains('['))
            return [new Step { Placeholder = part }];
        if (IsDateValue(part) || (ValueWords.Contains(part) && !KeywordWords.Contains(part)))
            return [new Step { Placeholder = part }];
        return Tokenize(part);
    }

    static bool IsDateValue(string text) =>
        text.Contains("yyyy", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("mm-dd", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("hh:", StringComparison.OrdinalIgnoreCase);

    // [created 0|1], [type 1-4], [model 0-2]: a small numeric set worth showing.
    static bool TryNumericChoices(string inner, out List<List<Step>> choices)
    {
        choices = [];
        var parts = SplitTop(inner, '|');
        var values = new List<string>();
        if (parts.Count > 1)
        {
            for (int i = 0; i < parts.Count; i++)
            {
                string part = parts[i].Trim();
                if (i == 0)
                {
                    int space = part.LastIndexOf(' ');
                    if (space >= 0) part = part[(space + 1)..];
                }
                if (!int.TryParse(part, out int value)) return false;
                values.Add(value.ToString());
                if (values.Count > 16) return false;
            }
            choices = values.Select(v => new List<Step> { new Step { Literal = v } }).ToList();
            return true;
        }

        string range = inner.Trim();
        int lastSpace = range.LastIndexOf(' ');
        if (lastSpace >= 0) range = range[(lastSpace + 1)..];
        int dash = range.IndexOf('-');
        if (dash <= 0) return false;
        if (!int.TryParse(range[..dash], out int from) || !int.TryParse(range[(dash + 1)..], out int to))
            return false;
        if (to < from || to - from > 15) return false;
        choices = Enumerable.Range(from, to - from + 1)
            .Select(v => new List<Step> { new Step { Literal = v.ToString() } }).ToList();
        return true;
    }

    static bool LooksNumeric(string text)
    {
        text = text.Trim();
        if (text.All(char.IsDigit)) return true;
        int dash = text.IndexOf('-');
        return dash > 0 && text[..dash].All(char.IsDigit) && text[(dash + 1)..].All(char.IsDigit);
    }

    static List<string> SplitTop(string text, char separator)
    {
        var parts = new List<string>();
        int start = 0, depth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '<' || c == '[') depth++;
            else if (c == '>' || c == ']') depth = Math.Max(0, depth - 1);
            else if (c == separator && depth == 0)
            {
                parts.Add(text[start..i]);
                start = i + 1;
            }
        }
        parts.Add(text[start..]);
        return parts;
    }

    // The first placeholder of an alternative gets the command's first catalog kind;
    // a placeholder after a literal keyword (near [radius]) stays free.
    static void AssignFirstCatalog(List<Step> steps, string[] arguments)
    {
        if (arguments.Length == 0 || steps.Count == 0) return;
        Step first = steps[0];
        if (first.Placeholder != null)
        {
            first.Catalog = arguments[0];
            return;
        }
        if (first.Choices is { } choices)
        {
            foreach (var alternative in choices)
                AssignFirstCatalog(alternative, arguments);
        }
    }

    static void Add(IDictionary<string, SortedSet<string>> map, List<string> prefix, string suggestion)
    {
        string key = string.Join(" ", prefix);
        if (!map.TryGetValue(key, out var set))
        {
            set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            map[key] = set;
        }
        set.Add(suggestion);
    }

    static List<string> With(List<string> prefix, string literal)
    {
        var next = new List<string>(prefix) { literal };
        return next;
    }

    // Walks a sequence and returns the literal prefixes reached after it.
    static List<List<string>> WalkSequence(
        List<Step> steps,
        List<string> prefix,
        IDictionary<string, SortedSet<string>> map,
        int depth)
    {
        var paths = new List<List<string>> { prefix };
        if (depth > 24) return paths;

        foreach (Step step in steps)
        {
            var next = new List<List<string>>();
            foreach (var path in paths)
            {
                if (step.Literal is { } literal)
                {
                    Add(map, path, literal);
                    next.Add(With(path, literal));
                }
                else if (step.Placeholder is { } placeholder)
                {
                    _ = placeholder;
                    if (step.Catalog is { } catalog) Add(map, path, "@" + catalog);
                }
                else if (step.Variadic)
                {
                    // No finite suggestions.
                }
                else if (step.Choices is { } choices)
                {
                    foreach (var alternative in choices)
                        foreach (string suggestion in FirstSuggestions(alternative))
                            Add(map, path, suggestion);
                    foreach (var alternative in choices)
                        next.AddRange(WalkSequence(alternative, [.. path], map, depth + 1));
                    if (step.Optional) next.Add(path);
                }
            }
            paths = next;
            if (paths.Count == 0) break;
            if (paths.Count > 200) paths = paths.Take(200).ToList();
        }
        return paths;
    }

    static IEnumerable<string> FirstSuggestions(List<Step> steps)
    {
        if (steps.Count == 0) yield break;
        Step first = steps[0];
        if (first.Literal is { } literal)
        {
            yield return literal;
        }
        else if (first.Catalog is { } catalog)
        {
            yield return "@" + catalog;
        }
        else if (first.Choices is { } choices)
        {
            foreach (var alternative in choices)
                foreach (string suggestion in FirstSuggestions(alternative))
                    yield return suggestion;
        }
    }
}
