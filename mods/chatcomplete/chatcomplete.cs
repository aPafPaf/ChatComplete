using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

// Local tool: injects NativeSuggest.dll into the local game process.
// M1: injection watcher. M2 will add the command/argument catalog writer.
public sealed class ChatComplete : IMod, IDisposable
{
    sealed record Settings(string DllPath, string ProcessName, int IntervalMs, bool AutoInject);

    // kernel32
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, UIntPtr size, uint type, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] buffer, UIntPtr size, out UIntPtr written);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateRemoteThread(IntPtr process, IntPtr attributes, UIntPtr stackSize, IntPtr start, IntPtr parameter, uint flags, out uint threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetExitCodeThread(IntPtr thread, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool VirtualFreeEx(IntPtr process, IntPtr address, UIntPtr size, uint type);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    static extern IntPtr GetModuleHandleA(string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    static extern IntPtr GetProcAddress(IntPtr module, string name);

    IModContext _mod = null!;
    Settings _settings = null!;
    Timer? _timer;
    readonly Dictionary<int, DateTime> _injected = [];
    readonly HashSet<int> _failed = [];
    bool _missingLogged;
    int _busy;
    // Commands whose argument kinds the server does not declare to Add(); the same
    // fallback map as tools/export_args.py.
    static readonly Dictionary<string, string[]> FallbackArgs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["spawn_mob"] = ["monster"], ["statue"] = ["monster"], ["kill"] = ["monster"],
        ["item"] = ["item"], ["gem"] = ["gem"],
        ["goto"] = ["area"], ["tp"] = ["area"],
        ["npc"] = ["npc"], ["npc_place"] = ["npc"], ["npc_face"] = ["npc"],
        ["quest_flag"] = ["quest_flag"],
        ["set_stat"] = ["stat"], ["stat"] = ["stat"],
    };

    readonly object _catalogGate = new();
    readonly HashSet<string> _knownCommands = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _knownArgs = new(StringComparer.OrdinalIgnoreCase);
    string? _valuesStamp;

    public void Load(IModContext mod)
    {
        _mod = mod;
        _settings = mod.Settings<Settings>();
        if (!_settings.AutoInject)
        {
            mod.Log("autoInject is off");
            return;
        }
        string dll = string.IsNullOrWhiteSpace(_settings.DllPath)
            ? Path.Combine(mod.Folder, "bin", "NativeSuggest.dll")
            : Path.GetFullPath(_settings.DllPath);
        int interval = Math.Clamp(_settings.IntervalMs, 250, 10000);
        _timer = new Timer(_ => Tick(dll), null, 0, interval);
        mod.Log($"inject watcher: process={_settings.ProcessName}, every {interval} ms, dll={dll}");

        // M2: the live command table of a connection contains the commands of every
        // loaded module and mod; write it where NativeSuggest.dll reads it.
        GameEvents.AreaGenerated.Add(mod.Manifest.Id, conn =>
        {
            DumpCatalog(conn, force: false);
            return Task.CompletedTask;
        });
        GameEvents.Command(mod.Manifest.Id, "ccc", "/ccc (rewrite the ChatComplete catalogs)",
            async (conn, args) =>
            {
                DumpCatalog(conn, force: true);
                await conn.Commands!.Reply("ChatComplete catalogs written.");
            });
    }

    void Tick(string dll)
    {
        if (Interlocked.Exchange(ref _busy, 1) != 0) return;
        try
        {
            if (!File.Exists(dll))
            {
                if (!_missingLogged)
                {
                    _missingLogged = true;
                    _mod.Log("dll not found: " + dll);
                }
                return;
            }
            _missingLogged = false;
            foreach (Process process in Process.GetProcessesByName(_settings.ProcessName))
            {
                using (process)
                {
                    int pid = process.Id;
                    DateTime started;
                    try { started = process.StartTime; }
                    catch { continue; }
                    if (_injected.TryGetValue(pid, out DateTime seen) && seen == started) continue;
                    if (TryInject(pid, dll))
                    {
                        _injected[pid] = started;
                        _failed.Remove(pid);
                        _mod.Log($"injected into {pid}");
                    }
                    else if (_failed.Add(pid))
                    {
                        _mod.Log($"inject failed for {pid}: {Marshal.GetLastWin32Error()}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _mod.Log("watcher: " + ex.Message);
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    void DumpCatalog(ClientState conn, bool force)
    {
        List<(string Name, string Usage, string[] Arguments)> usages;
        try { usages = conn.Commands?.Usages.ToList() ?? []; }
        catch (Exception ex) { _mod.Log("catalog: " + ex.Message); return; }
        if (usages.Count == 0) return;

        lock (_catalogGate)
        {
            foreach (var (name, _, arguments) in usages)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                _knownCommands.Add(name);
                if (arguments.Length > 0) _knownArgs.Add($"{name} {string.Join(",", arguments)}");
            }
            foreach (var (command, kinds) in FallbackArgs)
                if (_knownCommands.Contains(command))
                    _knownArgs.Add($"{command} {string.Join(",", kinds)}");

            string folder = Path.GetTempPath();
            string argsPath = Path.Combine(folder, "ChatComplete.args.txt");
            bool commands = WriteIfChanged(Path.Combine(folder, "ChatComplete.commands.txt"),
                _knownCommands.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
            bool args = WriteIfChanged(argsPath,
                _knownArgs.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
            if (commands || args)
                _mod.Log($"catalog: {_knownCommands.Count} commands, {_knownArgs.Count} with args");
            DumpValues(conn, folder, force);
            WriteIfChanged(Path.Combine(folder, "ChatComplete.usage.txt"),
                usages.OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(u => $"{u.Name}\t{u.Usage}"));
            try
            {
                UsageGrammar.Write(Path.Combine(folder, "ChatComplete.states.txt"), usages, _mod.Log,
                    name => GrammarStateLines(conn, name));
            }
            catch (Exception ex) { _mod.Log("grammar: " + ex.Message); }
        }
    }

    // Passive nodes: the command takes PassiveSkillGraphId numbers; Name is the label.
    static IEnumerable<(string Value, string Detail)> PassiveNodeValues()
    {
        var table = GameServer.Core.ClientTables.TableOrNull("PassiveSkills");
        if (table is null) yield break;
        foreach (var row in table)
        {
            int? graph = row.IntOrNull("PassiveSkillGraphId");
            if (graph is null or <= 0) continue;
            yield return (graph.Value.ToString(), row.Text("Name") ?? "");
        }
    }

    static IEnumerable<(string Value, string Detail)> QuestPresetValues()
    {
        var table = GameServer.Core.ClientTables.TableOrNull("CharacterStartQuestState");
        if (table is null) yield break;
        foreach (var row in table)
        {
            string? id = row.Text("Id");
            if (!string.IsNullOrWhiteSpace(id)) yield return (id, "");
        }
    }

    // AtlasNode.Id is what /atlas_complete accepts; Area1 points at WorldAreas.
    static IEnumerable<(string Value, string Detail)> AtlasValues()
    {
        var nodes = GameServer.Core.ClientTables.TableOrNull("AtlasNode");
        if (nodes is null) yield break;
        var areas = GameServer.Core.ClientTables.TableOrNull("WorldAreas");
        var names = new Dictionary<int, string>();
        if (areas is not null)
            foreach (var row in areas)
                names[row.Index] = row.Text("Name") ?? "";
        foreach (var row in nodes)
        {
            string? id = row.Text("Id");
            if (string.IsNullOrWhiteSpace(id)) continue;
            int area = row.IntOrNull("Area1") ?? -1;
            yield return (id, names.GetValueOrDefault(area, ""));
        }
    }

    // /tattoo takes the tattoo item's name; the PassiveSkillTattoos rows point at
    // BaseItemTypes rows.
    static IEnumerable<(string Value, string Detail)> TattooValues()
    {
        var tattoos = GameServer.Core.ClientTables.TableOrNull("PassiveSkillTattoos");
        var items = GameServer.Core.ClientTables.TableOrNull("BaseItemTypes");
        if (tattoos is null || items is null) yield break;
        var byRow = new Dictionary<int, (string Id, string Name)>();
        foreach (var row in items)
            byRow[row.Index] = (row.Text("Id") ?? "", row.Text("Name") ?? "");
        foreach (var row in tattoos)
        {
            int item = row.IntOrNull("Tattoo") ?? -1;
            if (!byRow.TryGetValue(item, out var entry) || entry.Name.Length == 0) continue;
            yield return (entry.Name, entry.Id);
        }
    }

    // Cluster themes are per size; the command takes the name with underscores.
    static IEnumerable<(string Value, string Detail)> ClusterThemeValues(int size)
    {
        foreach (var theme in ClusterJewels.Themes)
        {
            if (theme.Size != size || theme.Retired) continue;
            yield return (theme.Name.Replace(' ', '_').ToLowerInvariant(), theme.Name);
        }
    }

    // Monster affixes: Mods.Domain 3, prefix/suffix; the command takes the name with
    // underscores (of_Flames), or its Id.
    static IEnumerable<(string Value, string Detail)> MonsterModValues()
    {
        var table = GameServer.Core.ClientTables.TableOrNull("Mods");
        if (table is null) yield break;
        foreach (var row in table)
        {
            if (row.IntOrNull("Domain") != 3) continue;
            int? generation = row.IntOrNull("GenerationType");
            if (generation is not (1 or 2)) continue;
            string? name = row.Text("Name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            yield return (name.Trim().Replace(' ', '_'), row.Text("Id") ?? "");
        }
    }

    // The mods/modules this Gateway has loaded (and why the others are not).
    static IEnumerable<(string Value, string Detail)> InstalledModValues() =>
        Program.ServerMods.Mods().Select(m => (m.Id, m.Error ?? "loaded"));

    static IEnumerable<(string Value, string Detail)> InstalledModuleValues() =>
        Program.ServerModules.Mods().Select(m => (m.Id, m.Error ?? "loaded"));

    // Item mods: Mods.Domain 1. The command takes the Id; Name is the human label.
    static IEnumerable<(string Value, string Detail)> ItemModValues()
    {
        var table = GameServer.Core.ClientTables.TableOrNull("Mods");
        if (table is null) yield break;
        foreach (var row in table)
        {
            if (row.IntOrNull("Domain") != 1) continue;
            string? id = row.Text("Id");
            if (string.IsNullOrWhiteSpace(id)) continue;
            yield return (id, row.Text("Name") ?? "");
        }
    }

    // /item_tab names: the standard affinity tabs; a player's extra tabs are runtime.
    static IEnumerable<(string Value, string Detail)> StashTabValues()
    {
        var table = GameServer.Core.ClientTables.TableOrNull("StashTabAffinities");
        if (table is null) yield break;
        foreach (var row in table)
        {
            string? name = row.Text("Name");
            if (string.IsNullOrWhiteSpace(name) || name.StartsWith("UNUSED", StringComparison.OrdinalIgnoreCase))
                continue;
            yield return (name, "");
        }
    }

    // The Gateway's unique-jewel catalogue is the same list /unique_jewel resolves.
    static IEnumerable<(string Value, string Detail)> UniqueJewelValues() =>
        UniqueJewelCatalogue.Names.Select(name => (name, ""));

    // client.db's Ascendancy rows carry both families: Name is normal, LeagueName is
    // phrecia; Character is the Characters row (conn.ClassRow).
    static IEnumerable<(string Value, string Detail)> AscendancyValues(ClientState conn, string kind)
    {
        var table = GameServer.Core.ClientTables.TableOrNull("Ascendancy");
        if (table is null) yield break;
        foreach (var row in table)
        {
            if (row.Int("Character") != conn.ClassRow) continue;
            string? value = kind == "ascendancy_phrecia" ? row.Text("LeagueName") : row.Text("Name");
            if (string.IsNullOrWhiteSpace(value)) continue;
            yield return (value, row.Text("Id") ?? "");
        }
    }

    // A command's declared CommandGrammar (new LocalExile) wins over parsing its usage.
    // Reflection keeps the mod loading on a released Gateway without the type.
    static IEnumerable<string>? GrammarStateLines(ClientState conn, string name)
    {
        try
        {
            var grammar = conn.Commands?.GetType().GetMethod("Grammar")
                ?.Invoke(conn.Commands, new object[] { name });
            return grammar?.GetType().GetMethod("StateLines")?.Invoke(grammar, null) as IEnumerable<string>;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // The Gateway's framework kinds (mod, module) came in the LocalExile change beside
    // this mod; a released Gateway has only the game module's. Reflection keeps the mod
    // working with both.
    static IEnumerable<(string Value, string Detail)>? CatalogValues(string kind)
    {
        try
        {
            var method = typeof(Program).GetMethod("CompletionValues",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (method is not null)
                return (IEnumerable<(string, string)>?)method.Invoke(null, new object[] { kind });
        }
        catch (Exception)
        {
            // Older Gateway or a different signature: use the game module below.
        }
        return Program.GameLogicOrNull?.CompletionValues(kind);
    }

    // %TEMP%\ChatComplete.kind.<kind>.txt: "value<TAB>detail" per line, from the game
    // module itself (Program.GameLogicOrNull.CompletionValues) instead of client.db.
    void DumpValues(ClientState conn, string folder, bool force)
    {
        var kinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in _knownArgs)
        {
            int space = line.IndexOf(' ');
            if (space < 0) continue;
            foreach (string kind in line[(space + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                kinds.Add(kind);
        }
        // Not declared in args.txt: /ascendancy takes a family then a name, and the
        // name catalog depends on the character's class.
        kinds.Add("ascendancy_normal");
        kinds.Add("ascendancy_phrecia");
        if (_knownCommands.Contains("add_passive")) kinds.Add("passive");
        if (_knownCommands.Contains("quest_preset")) kinds.Add("quest_preset");
        if (_knownCommands.Contains("atlas_complete")) kinds.Add("atlas");
        if (_knownCommands.Contains("tattoo") || _knownCommands.Contains("tattoo_apply")) kinds.Add("tattoo");
        if (_knownCommands.Contains("unique_jewel")) kinds.Add("unique_jewel");
        if (_knownCommands.Contains("item_mod")) kinds.Add("item_mod");
        if (_knownCommands.Contains("item_tab")) kinds.Add("stash_tab");
        if (_knownCommands.Contains("cluster_jewel"))
        {
            kinds.Add("cluster_theme_small");
            kinds.Add("cluster_theme_medium");
            kinds.Add("cluster_theme_large");
        }
        if (_knownCommands.Contains("mob_mods")) kinds.Add("monster_mod");
        if (_knownCommands.Contains("mod_enable")) kinds.Add("installed_mod");
        if (_knownCommands.Contains("module_reload")) kinds.Add("installed_module");
        if (kinds.Count == 0) return;

        string stamp = $"{GameServer.Core.ClientTables.Written.Ticks}:" +
                       $"{Program.GameLogicOrNull?.GetType().Assembly.ManifestModule.ModuleVersionId}";
        if (!force && stamp == _valuesStamp) return;

        int written = 0, total = 0;
        foreach (string kind in kinds.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            IEnumerable<(string Value, string Detail)>? values;
            try
            {
                values = kind switch
                {
                    "ascendancy_normal" or "ascendancy_phrecia" => AscendancyValues(conn, kind),
                    "passive" => PassiveNodeValues(),
                    "quest_preset" => QuestPresetValues(),
                    "atlas" => AtlasValues(),
                    "tattoo" => TattooValues(),
                    "unique_jewel" => UniqueJewelValues(),
                    "item_mod" => ItemModValues(),
                    "stash_tab" => StashTabValues(),
                    "cluster_theme_small" => ClusterThemeValues(0),
                    "cluster_theme_medium" => ClusterThemeValues(1),
                    "cluster_theme_large" => ClusterThemeValues(2),
                    "monster_mod" => MonsterModValues(),
                    "installed_mod" => InstalledModValues(),
                    "installed_module" => InstalledModuleValues(),
                    _ => CatalogValues(kind),
                };
            }
            catch (Exception ex) { _mod.Log($"catalog {kind}: {ex.Message}"); continue; }
            if (values is null) continue;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var lines = new List<string>();
            foreach (var (value, detail) in values)
            {
                if (string.IsNullOrEmpty(value) || !seen.Add(value)) continue;
                string clean = (detail ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
                lines.Add(clean.Length == 0 ? value : value + "\t" + clean);
            }
            if (WriteIfChanged(Path.Combine(folder, $"ChatComplete.kind.{kind}.txt"), lines))
            {
                written++;
                total += lines.Count;
            }
        }
        _valuesStamp = stamp;
        if (written > 0) _mod.Log($"catalog values: {written} kind(s), {total} entries");
    }

    static bool WriteIfChanged(string path, IEnumerable<string> lines)
    {
        string text = string.Join("\n", lines) + "\n";
        try { if (File.Exists(path) && File.ReadAllText(path) == text) return false; }
        catch (IOException) { }
        string temp = path + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, path, overwrite: true);
        return true;
    }

    static bool TryInject(int pid, string dll)
    {
        const uint createThread = 0x0002;
        const uint queryInformation = 0x0400;
        const uint vmOperation = 0x0008;
        const uint vmWrite = 0x0020;
        const uint vmRead = 0x0010;
        IntPtr process = OpenProcess(createThread | queryInformation | vmOperation | vmWrite | vmRead, false, pid);
        if (process == IntPtr.Zero) return false;

        IntPtr remote = IntPtr.Zero;
        IntPtr thread = IntPtr.Zero;
        try
        {
            byte[] bytes = Encoding.Unicode.GetBytes(dll + "\0");
            remote = VirtualAllocEx(process, IntPtr.Zero, (UIntPtr)(uint)bytes.Length, 0x3000, 0x04);
            if (remote == IntPtr.Zero) return false;
            if (!WriteProcessMemory(process, remote, bytes, (UIntPtr)(uint)bytes.Length, out _)) return false;

            IntPtr kernel32 = GetModuleHandleA("kernel32.dll");
            IntPtr loadLibrary = GetProcAddress(kernel32, "LoadLibraryW");
            if (loadLibrary == IntPtr.Zero) return false;

            thread = CreateRemoteThread(process, IntPtr.Zero, UIntPtr.Zero, loadLibrary, remote, 0, out _);
            if (thread == IntPtr.Zero) return false;
            if (WaitForSingleObject(thread, 5000) != 0) return false;
            if (!GetExitCodeThread(thread, out uint code) || code == 0) return false;
            return true;
        }
        finally
        {
            if (thread != IntPtr.Zero) CloseHandle(thread);
            if (remote != IntPtr.Zero) VirtualFreeEx(process, remote, UIntPtr.Zero, 0x8000);
            CloseHandle(process);
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
