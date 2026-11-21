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
            // The server only declares the argument kinds a command passes to Add(); keep
            // the kinds another writer added (export_args.py) for commands it still knows.
            string argsPath = Path.Combine(folder, "ChatComplete.args.txt");
            try
            {
                if (File.Exists(argsPath))
                    foreach (string raw in File.ReadAllLines(argsPath))
                    {
                        string line = raw.Trim();
                        int space = line.IndexOf(' ');
                        if (line.Length == 0 || line[0] == '#' || space < 0) continue;
                        if (_knownCommands.Contains(line[..space])) _knownArgs.Add(line);
                    }
            }
            catch (IOException) { }
            bool commands = WriteIfChanged(Path.Combine(folder, "ChatComplete.commands.txt"),
                _knownCommands.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
            bool args = WriteIfChanged(argsPath,
                _knownArgs.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
            if (commands || args)
                _mod.Log($"catalog: {_knownCommands.Count} commands, {_knownArgs.Count} with args");
            DumpValues(folder, force);
        }
    }

    // %TEMP%\ChatComplete.kind.<kind>.txt: "value<TAB>detail" per line, from the game
    // module itself (Program.GameLogicOrNull.CompletionValues) instead of client.db.
    void DumpValues(string folder, bool force)
    {
        var kinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in _knownArgs)
        {
            int space = line.IndexOf(' ');
            if (space < 0) continue;
            foreach (string kind in line[(space + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                kinds.Add(kind);
        }
        if (kinds.Count == 0) return;

        var logic = Program.GameLogicOrNull;
        if (logic is null) return;
        string stamp = $"{GameServer.Core.ClientTables.Written.Ticks}:{logic.GetType().Assembly.ManifestModule.ModuleVersionId}";
        if (!force && stamp == _valuesStamp) return;

        int written = 0, total = 0;
        foreach (string kind in kinds.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            IEnumerable<(string Value, string Detail)>? values;
            try { values = logic.CompletionValues(kind); }
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
