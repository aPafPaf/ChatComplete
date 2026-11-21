// NativeSuggest: fills the game's own chat suggestion list with commands.
// No Win32 overlay, no manual selection.
//
// The client handles Up/Down/Esc natively in Chat_HandleKey once the suggestion list
// is visible (bit 0x800 at suggest+0x1e8). We rebuild that list in
// Chat_RebuildSuggestions and handle only Tab.
//
// Function addresses are resolved at load time from byte signatures
// (Signatures.generated.h), so a new client build only needs `tools/gen_signatures.py`
// regeneration, not source edits. The 3.29.3c RVAs are kept as a fast path / fallback.
#include <windows.h>
#include <cstdint>
#include <cstring>
#include <cwchar>
#include <cwctype>
#include <fstream>
#include <regex>
#include <string>
#include <unordered_map>
#include <vector>
#include <algorithm>
#include "Signatures.generated.h"

namespace {

constexpr uintptr_t kImageBase = 0x140000000;
// 3.29.3c fallbacks (used only if a signature cannot be found).
constexpr uintptr_t kSuggestBuilder = 0x140181010;   // Chat_RebuildSuggestions
constexpr uintptr_t kKeyHandler = 0x140180a90;       // Chat_HandleKey
constexpr uintptr_t kClearSuggestions = 0x1401808d0; // Chat_ClearSuggestions
constexpr uintptr_t kFactory = 0x1418b6d20;          // UI_GetFactory
constexpr uintptr_t kPool = 0x1418b6a00;             // UI_GetPoolBySize
constexpr uintptr_t kCtor = 0x1418c24a0;             // SuggestItem_Ctor
constexpr uintptr_t kAppendString = 0x1401b1bd0;     // WStringVector_Insert
constexpr uintptr_t kAddItem = 0x141904470;          // Suggest_AddItem
constexpr uintptr_t kSetVisible = 0x1418b0a30;       // Suggest_SetVisible
constexpr uintptr_t kCopyString = 0x140130840;       // WString_CopyCtor
constexpr uintptr_t kDestroyString = 0x140134500;    // WString_Dtor
constexpr uintptr_t kClearAllSuggestions = 0x141903830;  // Suggest_ClearAll (no hide)

struct Api {
    uintptr_t keyHandler = 0;
    uintptr_t suggestBuilder = 0;
    uintptr_t clearSuggestions = 0;
    uintptr_t clearAll = 0;
    uintptr_t factory = 0;
    uintptr_t pool = 0;
    uintptr_t ctor = 0;
    uintptr_t appendString = 0;
    uintptr_t addItem = 0;
    uintptr_t setVisible = 0;
    uintptr_t copyString = 0;
    uintptr_t destroyString = 0;
} g_api;

HMODULE g_module = nullptr;
uintptr_t g_base = 0;
uint8_t* g_suggestTrampoline = nullptr;
uint8_t* g_keyTrampoline = nullptr;
struct ArgEntry {
    std::wstring value;     // inserted
    std::wstring display;   // shown
    std::wstring valueLower;
    std::wstring displayLower;
};

struct Match {
    std::wstring insert;
    std::wstring display;
};

std::vector<std::wstring> g_commands;
std::vector<Match> g_all;              // every match for the current input
std::vector<std::wstring> g_native;    // insert text of the visible window
std::vector<std::wstring> g_display;   // display text of the visible window
std::vector<void*> g_items;            // item pointers of the visible window
size_t g_offset = 0;                   // index of the first visible match
size_t g_blankRows = 0;                // empty rows above the window (fixed height)
int g_selected = -1;                   // selected match (absolute)
int g_previewIndex = -1;
std::wstring g_previewText;
bool g_populated = false;
bool g_busy = false;
bool g_suppressRebuild = false;
void* g_chat = nullptr;
WNDPROC g_oldWndProc = nullptr;
constexpr UINT_PTR kWatchdogTimer = 0x0C4A;

std::unordered_map<std::wstring, std::vector<std::wstring>> g_cmdKinds;
std::unordered_map<std::wstring, std::vector<ArgEntry>> g_kindCatalogs;
std::unordered_map<std::wstring, bool> g_kindLoaded;
std::unordered_map<std::wstring, uint64_t> g_catalogStamps;
uint64_t g_lastCatalogCheck = 0;

constexpr size_t kWindowRows = 12;
constexpr size_t kMaxMatches = 4000;

template <typename T>
T At(uintptr_t va) {
    return reinterpret_cast<T>(g_base + (va - kImageBase));
}

std::wstring LogPath() {
    wchar_t temp[MAX_PATH]{};
    DWORD n = GetTempPathW(MAX_PATH, temp);
    if (n == 0 || n >= MAX_PATH) return L"NativeSuggest.log";
    return std::wstring(temp, n) + L"NativeSuggest.log";
}

std::wstring Hex(uint64_t v) {
    wchar_t buf[32]{};
    swprintf_s(buf, L"%llx", static_cast<unsigned long long>(v));
    return buf;
}

void Log(const std::wstring& line) {
    HANDLE file = CreateFileW(LogPath().c_str(), FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
                              nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return;
    int bytes = WideCharToMultiByte(CP_UTF8, 0, line.c_str(), (int)line.size(), nullptr, 0, nullptr, nullptr);
    if (bytes > 0) {
        std::string utf8((size_t)bytes, '\0');
        WideCharToMultiByte(CP_UTF8, 0, line.c_str(), (int)line.size(), utf8.data(), bytes, nullptr, nullptr);
        DWORD written = 0;
        WriteFile(file, utf8.data(), (DWORD)utf8.size(), &written, nullptr);
    }
    CloseHandle(file);
}

std::wstring CommandPath() {
    wchar_t temp[MAX_PATH]{};
    DWORD n = GetTempPathW(MAX_PATH, temp);
    if (n == 0 || n >= MAX_PATH) return L"ChatComplete.commands.txt";
    return std::wstring(temp, n) + L"ChatComplete.commands.txt";
}

// ---- signature-based function resolution -----------------------------------

struct ParsedSig {
    std::vector<uint8_t> bytes;
    std::vector<uint8_t> mask;
    bool ok = false;
};

int HexDigit(char c) {
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    return -1;
}

ParsedSig ParsePattern(const char* pattern) {
    ParsedSig p;
    const char* s = pattern;
    while (*s != '\0') {
        while (*s == ' ') ++s;
        if (*s == '\0') break;
        if (s[0] == '?' && s[1] == '?') {
            p.bytes.push_back(0);
            p.mask.push_back(0);
            s += 2;
            continue;
        }
        int hi = HexDigit(s[0]);
        int lo = HexDigit(s[1]);
        if (hi < 0 || lo < 0) return p;
        p.bytes.push_back(static_cast<uint8_t>((hi << 4) | lo));
        p.mask.push_back(0xFF);
        s += 2;
    }
    p.ok = !p.bytes.empty();
    return p;
}

bool MatchPattern(const uint8_t* at, const ParsedSig& p) {
    for (size_t i = 0; i < p.bytes.size(); ++i) {
        if (p.mask[i] != 0 && at[i] != p.bytes[i]) return false;
    }
    return true;
}

struct TextSection {
    uint8_t* base = nullptr;
    size_t size = 0;
};

TextSection FindTextSection() {
    auto* base = reinterpret_cast<uint8_t*>(g_module);
    auto* dos = reinterpret_cast<IMAGE_DOS_HEADER*>(base);
    auto* nt = reinterpret_cast<IMAGE_NT_HEADERS*>(base + dos->e_lfanew);
    auto* section = IMAGE_FIRST_SECTION(nt);
    for (WORD i = 0; i < nt->FileHeader.NumberOfSections; ++i) {
        if (memcmp(section[i].Name, ".text", 5) == 0) {
            TextSection t;
            t.base = base + section[i].VirtualAddress;
            t.size = section[i].Misc.VirtualSize;
            return t;
        }
    }
    TextSection t;
    t.base = base + 0x1000;
    t.size = nt->OptionalHeader.SizeOfImage - 0x1000;
    return t;
}

// Prefers the known 3.29.3c offset when its bytes still match; otherwise scans .text.
uintptr_t ResolveOne(const ChatSig& sig, const TextSection& text) {
    ParsedSig p = ParsePattern(sig.pattern);
    std::wstring name(sig.name, sig.name + strlen(sig.name));
    if (!p.ok) {
        Log(L"sig parse failed: " + name + L"\n");
        return 0;
    }
    uintptr_t offset = static_cast<uintptr_t>(sig.rva);
    if (offset < text.size && MatchPattern(text.base + offset, p)) {
        return reinterpret_cast<uintptr_t>(g_module) + offset;
    }
    const size_t n = p.bytes.size();
    if (n == 0 || n > text.size) return 0;
    for (size_t i = 0; i + n <= text.size; ++i) {
        if (p.mask[0] != 0 && text.base[i] != p.bytes[0]) continue;
        if (MatchPattern(text.base + i, p)) return reinterpret_cast<uintptr_t>(text.base) + i;
    }
    Log(L"sig not found: " + name + L"\n");
    return 0;
}

uintptr_t g_sigAddr[kChatSigCount]{};

uintptr_t SigAddr(const char* name) {
    for (uint32_t i = 0; i < kChatSigCount; ++i) {
        if (strcmp(kChatSigs[i].name, name) == 0) return g_sigAddr[i];
    }
    return 0;
}

uintptr_t Fallback(uintptr_t resolved, uintptr_t knownRva, const char* name) {
    if (resolved != 0) return resolved;
    Log(L"api fallback to 3.29.3c RVA for " + std::wstring(name, name + strlen(name)) + L"\n");
    return g_base + (knownRva - kImageBase);
}

void ResolveApi() {
    TextSection text = FindTextSection();
    for (uint32_t i = 0; i < kChatSigCount; ++i) {
        g_sigAddr[i] = ResolveOne(kChatSigs[i], text);
        uintptr_t expected = reinterpret_cast<uintptr_t>(g_module) +
                             static_cast<uintptr_t>(kChatSigs[i].rva);
        Log(L"sig " + std::wstring(kChatSigs[i].name, kChatSigs[i].name + strlen(kChatSigs[i].name)) +
            L" = 0x" + Hex(g_sigAddr[i]) + (g_sigAddr[i] == expected ? L"" : L" (shifted)") + L"\n");
    }
    g_api.keyHandler = Fallback(SigAddr("Chat_HandleKey"), kKeyHandler, "Chat_HandleKey");
    g_api.suggestBuilder = Fallback(SigAddr("Chat_RebuildSuggestions"), kSuggestBuilder, "Chat_RebuildSuggestions");
    g_api.clearSuggestions = Fallback(SigAddr("Chat_ClearSuggestions"), kClearSuggestions, "Chat_ClearSuggestions");
    g_api.clearAll = Fallback(SigAddr("Suggest_ClearAll"), kClearAllSuggestions, "Suggest_ClearAll");
    g_api.factory = Fallback(SigAddr("UI_GetFactory"), kFactory, "UI_GetFactory");
    g_api.pool = Fallback(SigAddr("UI_GetPoolBySize"), kPool, "UI_GetPoolBySize");
    g_api.ctor = Fallback(SigAddr("SuggestItem_Ctor"), kCtor, "SuggestItem_Ctor");
    g_api.appendString = Fallback(SigAddr("WStringVector_Insert"), kAppendString, "WStringVector_Insert");
    g_api.addItem = Fallback(SigAddr("Suggest_AddItem"), kAddItem, "Suggest_AddItem");
    g_api.setVisible = Fallback(SigAddr("Suggest_SetVisible"), kSetVisible, "Suggest_SetVisible");
    g_api.copyString = Fallback(SigAddr("WString_CopyCtor"), kCopyString, "WString_CopyCtor");
    g_api.destroyString = Fallback(SigAddr("WString_Dtor"), kDestroyString, "WString_Dtor");
}

void LoadCommands() {
    g_commands = {
        L"help", L"hideout", L"global", L"trade", L"party", L"guild", L"whois", L"invite", L"kick",
        L"afk", L"dnd", L"played", L"oos", L"passives", L"commands", L"atlas_complete", L"atlas_reset",
        L"reload", L"lastpackets", L"heal", L"hit", L"spawn_mob", L"tp", L"goto", L"state", L"item_give",
        L"god", L"set_level", L"set_speed", L"set_stat", L"add_xp", L"add_passive", L"reset_passives",
        L"resistances", L"defences", L"crit", L"block", L"charges", L"auras", L"suppression", L"accuracy",
        L"mobs", L"despawn", L"npc", L"npcs", L"pos", L"sockets", L"quest_flag", L"quest_preset",
        L"pantheon", L"raw", L"cosmetic", L"cc"
    };
    std::ifstream in(CommandPath());
    std::string line;
    while (std::getline(in, line)) {
        while (!line.empty() && (line.back() == '\r' || line.back() == '\n' || line.back() == ' ')) line.pop_back();
        if (line.size() > 1 && line[0] == '/') line.erase(line.begin());
        if (line.empty()) continue;
        int wide = MultiByteToWideChar(CP_UTF8, 0, line.c_str(), (int)line.size(), nullptr, 0);
        if (wide <= 0) continue;
        std::wstring w((size_t)wide, L'\0');
        MultiByteToWideChar(CP_UTF8, 0, line.c_str(), (int)line.size(), w.data(), wide);
        g_commands.push_back(std::move(w));
    }
    std::sort(g_commands.begin(), g_commands.end());
    g_commands.erase(std::unique(g_commands.begin(), g_commands.end()), g_commands.end());
    Log(L"commands loaded: " + std::to_wstring(g_commands.size()) + L"\n");
}

// The edit keeps its text as an MSVC std::wstring at edit+0x338:
// data at +0, size at +0x10, capacity at +0x18. Reading it directly is what the
// probe has always used successfully; the vtable getter returns by value and has a
// different argument order (hidden return pointer first), so it is not used here.
std::wstring ReadInputText(void* chat) {
    if (chat == nullptr) return L"";
    void* edit = *reinterpret_cast<void**>(reinterpret_cast<uint8_t*>(chat) + 0x3f0);
    if (edit == nullptr) return L"";

    auto* text = reinterpret_cast<uint8_t*>(edit) + 0x338;
    uint64_t size = *reinterpret_cast<uint64_t*>(text + 16);
    uint64_t capacity = *reinterpret_cast<uint64_t*>(text + 24);
    if (size > 8192 || capacity > 8192 || capacity < size) {
        size = *reinterpret_cast<uint64_t*>(text + 8);
        capacity = *reinterpret_cast<uint64_t*>(text + 16);
    }
    if (size > 8192 || size == 0) return L"";
    const wchar_t* data = capacity >= 8 ? *reinterpret_cast<const wchar_t**>(text)
                                        : reinterpret_cast<const wchar_t*>(text);
    if (data == nullptr) return L"";
    return std::wstring(data, data + size);
}

bool ChatVisible(void* chat) {
    return chat != nullptr &&
           *reinterpret_cast<uint8_t*>(reinterpret_cast<uint8_t*>(chat) + 0x498) != 0;
}

void ClearSuggestions(void* chat) {
    reinterpret_cast<void (*)(void*)>(g_api.clearSuggestions)(chat);
}

// Clears entries and strings without the hide path: hiding notifies the parent,
// which can already be torn down while the chat closes (crash in UI_ApplyFlagState).
void ClearEntries(void* chat) {
    if (chat == nullptr) return;
    void* suggest = *reinterpret_cast<void**>(reinterpret_cast<uint8_t*>(chat) + 0x400);
    if (suggest == nullptr) return;
    reinterpret_cast<void (*)(void*)>(g_api.clearAll)(suggest);
    auto* begin = *reinterpret_cast<uint8_t**>(reinterpret_cast<uint8_t*>(chat) + 0x408);
    auto* end = *reinterpret_cast<uint8_t**>(reinterpret_cast<uint8_t*>(chat) + 0x410);
    for (uint8_t* p = begin; p != end; p += 0x20) {
        At<void (*)(void*)>(kDestroyString)(p);
    }
    *reinterpret_cast<void**>(reinterpret_cast<uint8_t*>(chat) + 0x410) = begin;
}

bool ParentLooksValid(void* suggest) {
    void* parent = *reinterpret_cast<void**>(reinterpret_cast<uint8_t*>(suggest) + 0x148);
    if (parent == nullptr) return true;
    auto p = reinterpret_cast<uintptr_t>(parent);
    if (p < 0x10000 || p > 0x00007fffffffffffULL) return false;
    MEMORY_BASIC_INFORMATION mbi{};
    if (VirtualQuery(parent, &mbi, sizeof(mbi)) == 0) return false;
    if (mbi.State != MEM_COMMIT || (mbi.Protect & PAGE_NOACCESS) != 0) return false;
    // The hide path dereferences parent+0x208; probe it safely before calling.
    __try {
        volatile void* probe = *reinterpret_cast<void**>(reinterpret_cast<uint8_t*>(parent) + 0x208);
        (void)probe;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
    return true;
}

void SafeSetVisible(void* suggest, uint8_t visible);

// Hides the list even if the parent is gone: the visible flag is cleared first, so
// the renderer stops drawing it, then UI_ApplyFlagState runs only when it is safe.
void HideList(void* chat) {
    if (chat == nullptr) return;
    void* suggest = *reinterpret_cast<void**>(reinterpret_cast<uint8_t*>(chat) + 0x400);
    if (suggest == nullptr) return;
    *reinterpret_cast<uint32_t*>(reinterpret_cast<uint8_t*>(suggest) + 0x1e8) &= ~0x800u;
    *reinterpret_cast<uint8_t*>(reinterpret_cast<uint8_t*>(suggest) + 0x498) = 0;
    SafeSetVisible(suggest, 0);
}

void SafeSetVisible(void* suggest, uint8_t visible) {
    if (suggest == nullptr) return;
    if (!ParentLooksValid(suggest)) return;
    reinterpret_cast<void (*)(void*, uint8_t)>(g_api.setVisible)(suggest, visible);
}

HWND GameWindow() {
    struct Find {
        DWORD pid;
        HWND hwnd;
    } find{GetCurrentProcessId(), nullptr};
    EnumWindows([](HWND hwnd, LPARAM param) -> BOOL {
        auto* f = reinterpret_cast<Find*>(param);
        DWORD pid = 0;
        GetWindowThreadProcessId(hwnd, &pid);
        if (pid != f->pid || !IsWindowVisible(hwnd)) return TRUE;
        wchar_t cls[64]{};
        GetClassNameW(hwnd, cls, 64);
        if (wcscmp(cls, L"POEWindowClass") == 0) {
            f->hwnd = hwnd;
            return FALSE;
        }
        return TRUE;
    }, reinterpret_cast<LPARAM>(&find));
    return find.hwnd;
}

struct ClientString { uint64_t q[4]; };

std::wstring TempDir() {
    wchar_t temp[MAX_PATH]{};
    DWORD n = GetTempPathW(MAX_PATH, temp);
    if (n == 0 || n >= MAX_PATH) return std::wstring();
    return std::wstring(temp, n);
}

std::wstring KindPath(const std::wstring& kind) {
    return TempDir() + L"ChatComplete.kind." + kind + L".txt";
}

// The mod (LocalExile/Mods/Binaries/chatcomplete) rewrites these files: a change
// means the running client should re-read them without /cc reload.
uint64_t FileStamp(const std::wstring& path) {
    WIN32_FILE_ATTRIBUTE_DATA data{};
    if (!GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &data)) return 0;
    uint64_t ticks = (static_cast<uint64_t>(data.ftLastWriteTime.dwHighDateTime) << 32) |
                     data.ftLastWriteTime.dwLowDateTime;
    uint64_t size = (static_cast<uint64_t>(data.nFileSizeHigh) << 32) | data.nFileSizeLow;
    return ticks * 1000003ull ^ size;
}

void RecordCatalogStamps() {
    g_catalogStamps.clear();
    g_catalogStamps[L"commands"] = FileStamp(CommandPath());
    g_catalogStamps[L"args"] = FileStamp(TempDir() + L"ChatComplete.args.txt");
    for (const auto& [kind, loaded] : g_kindLoaded)
        if (loaded) g_catalogStamps[L"kind." + kind] = FileStamp(KindPath(kind));
}

bool CatalogStampChanged(const std::wstring& key, const std::wstring& path) {
    auto it = g_catalogStamps.find(key);
    return it == g_catalogStamps.end() || it->second != FileStamp(path);
}

bool CatalogsChanged() {
    if (CatalogStampChanged(L"commands", CommandPath()) ||
        CatalogStampChanged(L"args", TempDir() + L"ChatComplete.args.txt"))
        return true;
    for (const auto& [kind, loaded] : g_kindLoaded)
        if (loaded && CatalogStampChanged(L"kind." + kind, KindPath(kind))) return true;
    return false;
}

std::wstring ToLowerW(const std::wstring& s) {
    std::wstring out = s;
    for (auto& c : out) c = static_cast<wchar_t>(towlower(c));
    return out;
}

bool HasRegexMeta(const std::wstring& q) {
    return q.find_first_of(L".*+?[](){}|^$\\") != std::wstring::npos;
}

// "Metadata/Monsters/InvisibleFire/X" -> "InvisibleFire/X"
std::wstring StripMetadata(const std::wstring& id) {
    if (_wcsnicmp(id.c_str(), L"Metadata/", 9) != 0) return id;
    size_t second = id.find(L'/', 9);
    if (second == std::wstring::npos) return id.substr(9);
    return id.substr(second + 1);
}

constexpr size_t kDisplayWidth = 64;

std::wstring DisplayText(const std::wstring& value) {
    std::wstring d = StripMetadata(value);
    if (d.size() > kDisplayWidth) {
        d.resize(kDisplayWidth - 1);
        d.push_back(L'\x2026');
    }
    return d;
}

// Case-insensitive '*' wildcard: every '*' matches any run of characters.
bool WildcardMatchCI(const std::wstring& value, const std::wstring& pattern) {
    size_t pos = 0;
    size_t i = 0;
    while (i <= pattern.size()) {
        size_t star = pattern.find(L'*', i);
        std::wstring segment = pattern.substr(i, star == std::wstring::npos ? std::wstring::npos : star - i);
        if (!segment.empty()) {
            size_t found = value.find(segment, pos);
            if (found == std::wstring::npos) return false;
            pos = found + segment.size();
        }
        if (star == std::wstring::npos) break;
        i = star + 1;
    }
    return true;
}

std::wstring ToWide(const std::string& s) {
    std::wstring w;
    if (s.empty()) return w;
    int n = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), nullptr, 0);
    if (n <= 0) return w;
    w.resize((size_t)n);
    MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), w.data(), n);
    return w;
}

// %TEMP%\ChatComplete.args.txt: "<command> <kind>[,<kind>...]" (one per line).
void LoadCommandKinds() {
    std::ifstream in(TempDir() + L"ChatComplete.args.txt");
    if (!in) return;
    std::string line;
    while (std::getline(in, line)) {
        if (!line.empty() && line.back() == '\r') line.pop_back();
        if (line.empty() || line[0] == '#') continue;
        auto sp = line.find(' ');
        if (sp == std::string::npos) continue;
        std::wstring command = ToWide(line.substr(0, sp));
        std::wstring kinds = ToWide(line.substr(sp + 1));
        std::vector<std::wstring> list;
        size_t start = 0;
        while (start <= kinds.size()) {
            size_t comma = kinds.find(L',', start);
            std::wstring kind = kinds.substr(start, comma == std::wstring::npos ? std::wstring::npos : comma - start);
            if (!kind.empty()) list.push_back(kind);
            if (comma == std::wstring::npos) break;
            start = comma + 1;
        }
        if (!command.empty() && !list.empty()) g_cmdKinds[ToLowerW(command)] = std::move(list);
    }
    Log(L"command kinds: " + std::to_wstring(g_cmdKinds.size()) + L"\n");
}

// %TEMP%\ChatComplete.kind.<kind>.txt: "value<TAB>detail" per line.
void LoadKindCatalog(const std::wstring& kind) {
    if (g_kindLoaded[kind]) return;
    g_kindLoaded[kind] = true;
    std::ifstream in(KindPath(kind));
    if (!in) {
        Log(L"catalog missing: " + kind + L"\n");
        g_catalogStamps[L"kind." + kind] = 0;
        return;
    }
    auto& list = g_kindCatalogs[kind];
    std::string line;
    while (std::getline(in, line)) {
        if (!line.empty() && line.back() == '\r') line.pop_back();
        if (line.empty() || line[0] == '#') continue;
        std::string value = line;
        auto tab = line.find('\t');
        if (tab != std::string::npos) value = line.substr(0, tab);
        ArgEntry e;
        e.value = ToWide(value);
        if (e.value.empty()) continue;
        e.displayLower = ToLowerW(StripMetadata(e.value));
        e.display = DisplayText(e.value);
        e.valueLower = ToLowerW(e.value);
        list.push_back(std::move(e));
    }
    Log(L"catalog " + kind + L": " + std::to_wstring(list.size()) + L"\n");
    g_catalogStamps[L"kind." + kind] = FileStamp(KindPath(kind));
}

bool ArgMatches(const ArgEntry& e, const std::wstring& qLower, const std::wregex* re, bool wildcard) {
    if (wildcard) {
        return WildcardMatchCI(e.valueLower, qLower) || WildcardMatchCI(e.displayLower, qLower);
    }
    if (re != nullptr) {
        return std::regex_search(e.value, *re) || std::regex_search(StripMetadata(e.value), *re);
    }
    if (qLower.empty()) return true;
    return e.valueLower.find(qLower) != std::wstring::npos ||
           e.displayLower.find(qLower) != std::wstring::npos;
}

// BGRA bytes. The client writes its own dark red here when a row is selected.
std::wstring TrimW(const std::wstring& s) {
    size_t a = s.find_first_not_of(L" \t\r\n");
    if (a == std::wstring::npos) return std::wstring();
    size_t b = s.find_last_not_of(L" \t\r\n");
    return s.substr(a, b - a + 1);
}

// Re-reads commands.txt, args.txt and forgets the kind catalogs (they reload lazily).
void ReloadCatalogs() {
    g_commands.clear();
    LoadCommands();
    g_cmdKinds.clear();
    LoadCommandKinds();
    g_kindCatalogs.clear();
    g_kindLoaded.clear();
    RecordCatalogStamps();
    Log(L"catalogs reloaded: " + std::to_wstring(g_commands.size()) + L" commands, " +
        std::to_wstring(g_cmdKinds.size()) + L" with args\n");
}

constexpr uint32_t kSelectedRowColor = 0xFFFFC800;  // yellow

// Creates one item exactly like Chat_RebuildSuggestions does.
void* CreateItem(void* chat, void* suggest, const std::wstring& displayText,
                 const std::wstring& insertText, uint64_t count, uint64_t index) {
    auto factoryPtr = reinterpret_cast<void* (*)()>(g_api.factory)();
    void* factory = *reinterpret_cast<void**>(factoryPtr);
    void* pool = reinterpret_cast<void* (*)(void*, uint64_t)>(g_api.pool)(factory, 0x6b8);
    void* poolVtable = *reinterpret_cast<void**>(pool);
    void* alloc = *reinterpret_cast<void**>(reinterpret_cast<uint8_t*>(poolVtable) + 8);
    void* mem = reinterpret_cast<void* (*)(void*, uint64_t)>(alloc)(pool, 0x6b8);
    void* item = reinterpret_cast<void* (*)(void*)>(g_api.ctor)(mem);
    *reinterpret_cast<uint64_t*>(reinterpret_cast<uint8_t*>(item) + 8) = 0x6b8;

    void* v220 = *reinterpret_cast<void**>(reinterpret_cast<uint8_t*>(item) + 0x220);
    reinterpret_cast<void (*)(void*, uint64_t)>(*reinterpret_cast<void**>(reinterpret_cast<uint8_t*>(v220) + 0x10))(
        reinterpret_cast<uint8_t*>(item) + 0x220, 1);

    void* itemVtable = *reinterpret_cast<void**>(item);
    reinterpret_cast<void (*)(void*, void*)>(*reinterpret_cast<void**>(reinterpret_cast<uint8_t*>(itemVtable) + 0x138))(
        item, reinterpret_cast<uint8_t*>(suggest) + 0x170);

    ClientString temp{};
    reinterpret_cast<void* (*)(void*, void*)>(g_api.copyString)(&temp, const_cast<std::wstring*>(&displayText));
    void* sub = reinterpret_cast<uint8_t*>(item) + 0x298;
    void* subVtable = *reinterpret_cast<void**>(sub);
    reinterpret_cast<void (*)(void*, void*)>(*reinterpret_cast<void**>(reinterpret_cast<uint8_t*>(subVtable) + 0x10))(
        sub, &temp);

    reinterpret_cast<void* (*)(void*, uint64_t, void*, uint64_t)>(g_api.addItem)(suggest, count, item, index);

    auto* vbegin = reinterpret_cast<uint64_t*>(reinterpret_cast<uint8_t*>(chat) + 0x408);
    auto* vend = reinterpret_cast<uint64_t*>(reinterpret_cast<uint8_t*>(chat) + 0x410);
    auto* vcap = reinterpret_cast<uint64_t*>(reinterpret_cast<uint8_t*>(chat) + 0x418);
    if (*vend == *vcap) {
        reinterpret_cast<void* (*)(void*, void*, void*)>(g_api.appendString)(
            vbegin, reinterpret_cast<void*>(*vend), const_cast<std::wstring*>(&insertText));
    } else {
        reinterpret_cast<void* (*)(void*, void*)>(g_api.copyString)(
            reinterpret_cast<void*>(*vend), const_cast<std::wstring*>(&insertText));
        *vend += 0x20;
    }
    return item;
}

void SetInputText(void* chat, const std::wstring& text) {
    void* edit = *reinterpret_cast<void**>(reinterpret_cast<uint8_t*>(chat) + 0x3f0);
    if (edit == nullptr) return;
    void* label = reinterpret_cast<uint8_t*>(edit) + 0x298;
    void* vtable = *reinterpret_cast<void**>(label);
    void* setter = *reinterpret_cast<void**>(reinterpret_cast<uint8_t*>(vtable) + 0x10);
    ClientString temp{};
    reinterpret_cast<void* (*)(void*, void*)>(g_api.copyString)(&temp, const_cast<std::wstring*>(&text));
    g_suppressRebuild = true;
    reinterpret_cast<void (*)(void*, void*)>(setter)(label, &temp);
    g_suppressRebuild = false;
    // Collapse the caret at the end (edit+0x6e0 start, +0x6e8 end).
    *reinterpret_cast<uint64_t*>(reinterpret_cast<uint8_t*>(edit) + 0x6e0) = text.size();
    *reinterpret_cast<uint64_t*>(reinterpret_cast<uint8_t*>(edit) + 0x6e8) = text.size();
}

void PaintRows(int selected) {
    for (size_t i = 0; i < g_items.size(); ++i) {
        uint32_t color = (static_cast<int>(i) == selected) ? kSelectedRowColor : 0;
        *reinterpret_cast<uint32_t*>(reinterpret_cast<uint8_t*>(g_items[i]) + 0x288) = color;
    }
}

// Put the list just above the input instead of below it (there is no room below).
// NOTE: suggest+0x150 is X (not height). The client writes the list height there,
// which is why the list drifted right as it grew. +0x26c is the height.
void PositionListAboveInput(void* chat, void* suggest) {
    if (!ChatVisible(chat)) return;
    void* edit = *reinterpret_cast<void**>(reinterpret_cast<uint8_t*>(chat) + 0x3f0);
    if (edit == nullptr || suggest == nullptr || g_items.empty()) return;
    float rowHeight = 33.0f;
    float measured = *reinterpret_cast<float*>(reinterpret_cast<uint8_t*>(g_items[0]) + 0x154);
    if (measured > 4.0f && measured < 200.0f) rowHeight = measured;
    float height = rowHeight * static_cast<float>(g_items.size());
    float editX = *reinterpret_cast<float*>(reinterpret_cast<uint8_t*>(edit) + 0x150);
    float editY = *reinterpret_cast<float*>(reinterpret_cast<uint8_t*>(edit) + 0x154);
    *reinterpret_cast<float*>(reinterpret_cast<uint8_t*>(suggest) + 0x150) = editX;
    *reinterpret_cast<float*>(reinterpret_cast<uint8_t*>(suggest) + 0x154) = editY - height - 2.0f;
    *reinterpret_cast<float*>(reinterpret_cast<uint8_t*>(suggest) + 0x26c) = height;
}

// Materialises the visible window of g_all into the client's list.
void RebuildWindow(void* chat) {
    if (!ChatVisible(chat)) return;
    ClearEntries(chat);
    g_native.clear();
    g_display.clear();
    g_items.clear();
    g_previewIndex = -1;
    g_previewText.clear();
    g_populated = false;

    if (g_all.empty() || g_offset >= g_all.size()) {
        HideList(chat);
        return;
    }

    void* suggest = *reinterpret_cast<void**>(reinterpret_cast<uint8_t*>(chat) + 0x400);
    size_t count = std::min(kWindowRows, g_all.size() - g_offset);
    g_blankRows = 0;
    uint64_t row = 0;
    for (size_t i = 0; i < count; ++i) {
        const Match& m = g_all[g_offset + i];
        uint64_t index = (*reinterpret_cast<uint64_t*>(reinterpret_cast<uint8_t*>(chat) + 0x410) -
                           *reinterpret_cast<uint64_t*>(reinterpret_cast<uint8_t*>(chat) + 0x408)) / 0x20;
        void* item = CreateItem(chat, suggest, m.display, m.insert, row, index);
        g_items.push_back(item);
        g_native.push_back(m.insert);
        g_display.push_back(m.display);
        ++row;
    }
    SafeSetVisible(suggest, 1);
    g_populated = true;
    PositionListAboveInput(chat, suggest);
    PaintRows(static_cast<int>(g_blankRows) + (g_selected - static_cast<int>(g_offset)));
}

void EnsureWatchdog();
void ForgetList();

void Populate(void* chat) {
    if (chat == nullptr || g_busy) return;
    g_chat = chat;
    EnsureWatchdog();
    if (!ChatVisible(chat)) {
        // The chat is closing or hidden: hide our list and forget the rows.
        HideList(chat);
        ClearEntries(chat);
        g_all.clear();
        g_native.clear();
        g_display.clear();
        g_items.clear();
        g_populated = false;
        g_selected = -1;
        g_offset = 0;
        g_blankRows = 0;
        return;
    }
    // The mod writes the catalogs while the client runs: pick a change up on the
    // next rebuild, at most twice a second.
    uint64_t now = GetTickCount64();
    if (now - g_lastCatalogCheck >= 500) {
        g_lastCatalogCheck = now;
        if (CatalogsChanged()) ReloadCatalogs();
    }
    g_busy = true;

    std::wstring text = ReadInputText(chat);
    g_all.clear();
    std::vector<Match> matches;
    bool suggestMode = false;
    bool commandMode = true;
    std::wstring argCommand;
    std::wstring argKind;

    if (!text.empty() && (text[0] == L'/' || text[0] == L'*')) {
        suggestMode = true;
        // Split after the slash, keeping an empty trailing token ("/cmd " -> "cmd", "").
        // A bare '*' lists every command.
        std::vector<std::wstring> tokens;
        size_t start = (text[0] == L'/') ? 1 : 0;
        while (true) {
            size_t sp = text.find(L' ', start);
            if (sp == std::wstring::npos) {
                tokens.push_back(text.substr(start));
                break;
            }
            tokens.push_back(text.substr(start, sp - start));
            start = sp + 1;
            if (start > text.size()) {
                tokens.push_back(L"");
                break;
            }
        }

        if (!tokens.empty()) {
            const std::wstring& first = tokens[0];
            if (tokens.size() == 1) {
                // Command name: '*' lists everything; otherwise prefix hits first,
                // then substring hits on any part.
                std::wstring qLower = ToLowerW(first);
                bool wildcard = first.find(L'*') != std::wstring::npos;
                std::vector<std::wstring> prefixHits;
                std::vector<std::wstring> substringHits;
                for (const auto& name : g_commands) {
                    if (wildcard) {
                        if (WildcardMatchCI(ToLowerW(name), qLower)) prefixHits.push_back(name);
                        continue;
                    }
                    if (name.size() < first.size()) continue;
                    if (_wcsnicmp(name.c_str(), first.c_str(), first.size()) == 0) {
                        prefixHits.push_back(name);
                    } else if (ToLowerW(name).find(qLower) != std::wstring::npos) {
                        substringHits.push_back(name);
                    }
                }
                for (const auto& n : prefixHits) {
                    matches.push_back({L"/" + n, DisplayText(L"/" + n)});
                    if (matches.size() >= kMaxMatches) break;
                }
                for (const auto& n : substringHits) {
                    if (matches.size() >= kMaxMatches) break;
                    matches.push_back({L"/" + n, DisplayText(L"/" + n)});
                }
            } else {
                std::wstring command = ToLowerW(first);
                commandMode = false;
                argCommand = command;
                auto kit = g_cmdKinds.find(command);
                if (kit != g_cmdKinds.end()) {
                    const auto& kinds = kit->second;
                    int argIndex = 0;
                    std::wstring query;
                    if (kinds.size() <= 1) {
                        // One argument: the whole rest, so values may contain spaces.
                        query = text.substr(text.find(L' ') + 1);
                    } else {
                        argIndex = static_cast<int>(tokens.size()) - 2;
                        query = tokens.back();
                    }
                    if (argIndex >= 0 && static_cast<size_t>(argIndex) < kinds.size()) {
                        const std::wstring& kind = kinds[static_cast<size_t>(argIndex)];
                        argKind = kind;
                        LoadKindCatalog(kind);
                        auto cit = g_kindCatalogs.find(kind);
                        if (cit != g_kindCatalogs.end()) {
                            std::wstring qLower = ToLowerW(query);
                            bool wildcard = query.find(L'*') != std::wstring::npos;
                            std::wregex re;
                            std::wregex* rePtr = nullptr;
                            if (!wildcard && HasRegexMeta(query)) {
                                try {
                                    re = std::wregex(query, std::regex_constants::ECMAScript |
                                                                std::regex_constants::icase);
                                    rePtr = &re;
                                } catch (...) {
                                    rePtr = nullptr;
                                }
                            }
                            for (const auto& e : cit->second) {
                                if (ArgMatches(e, qLower, rePtr, wildcard)) {
                                    matches.push_back({L"/" + command + L" " + e.value, e.display});
                                    if (matches.size() >= kMaxMatches) break;
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    g_all = std::move(matches);
    g_offset = 0;
    g_selected = -1;
    RebuildWindow(chat);
    Log(L"populated " + std::to_wstring(g_all.size()) + L" for " + text + L"\n");
    g_busy = false;
}

void SuggestHook(void* self) {
    if (g_suppressRebuild) return;
    if (g_suggestTrampoline != nullptr) reinterpret_cast<void (*)(void*)>(g_suggestTrampoline)(self);
    Populate(self);
}

// Up/Down scroll the whole match list (the window follows the selection).
// Tab accepts the selected row, adds a space and puts the caret at the end.
void KeyHook(void* chat, void* edit, void* event) {
    if (chat != nullptr && event != nullptr) {
        int k = *reinterpret_cast<int*>(reinterpret_cast<uint8_t*>(event) + 4);
        bool down = *reinterpret_cast<uint8_t*>(reinterpret_cast<uint8_t*>(event) + 8) == 0;
        if (k == 0x0D && down) {
            std::wstring line = ToLowerW(TrimW(ReadInputText(chat)));
            if (line == L"/cc" || line == L"/cc reload" || line == L"/chatcomplete reload") {
                ReloadCatalogs();
                SetInputText(chat, L"");
                ClearEntries(chat);
                HideList(chat);
                ForgetList();
                *reinterpret_cast<uint8_t*>(event) = 1;
                Log(L"/cc reload consumed\n");
                return;
            }
        }
    }
    if (chat != nullptr && !ChatVisible(chat)) {
        // The chat closed behind our back (Esc): its rows are gone.
        g_all.clear();
        g_native.clear();
        g_display.clear();
        g_items.clear();
        g_populated = false;
        g_selected = -1;
        g_offset = 0;
        g_blankRows = 0;
    }
    int key = -1;
    bool isDown = false;
    if (event != nullptr) {
        key = *reinterpret_cast<int*>(reinterpret_cast<uint8_t*>(event) + 4);
        isDown = *reinterpret_cast<uint8_t*>(reinterpret_cast<uint8_t*>(event) + 8) == 0;
    }

    if (chat != nullptr && event != nullptr && g_populated && !g_all.empty() && ChatVisible(chat)) {
        void* suggest = *reinterpret_cast<void**>(reinterpret_cast<uint8_t*>(chat) + 0x400);
        bool visible = suggest != nullptr &&
                       (*reinterpret_cast<uint32_t*>(reinterpret_cast<uint8_t*>(suggest) + 0x1e8) & 0x800) != 0;

        // Up/Down repeats arrive with event+8 = 1 (the client's repeat flag),
        // so they must be handled on every call, not only on the first press.
        if (visible && (key == 0x26 || key == 0x28)) {
            int selected = g_selected;
            if (key == 0x28) {
                selected = (selected < 0) ? 0 : std::min(selected + 1, static_cast<int>(g_all.size()) - 1);
            } else {
                selected = (selected < 0) ? static_cast<int>(g_all.size()) - 1 : std::max(selected - 1, 0);
            }

            bool scrolled = selected < static_cast<int>(g_offset) ||
                            selected >= static_cast<int>(g_offset + kWindowRows);
            if (scrolled) {
                if (selected < static_cast<int>(g_offset)) {
                    g_offset = static_cast<size_t>(selected);
                } else {
                    g_offset = static_cast<size_t>(selected - static_cast<int>(kWindowRows) + 1);
                }
                RebuildWindow(chat);
                suggest = *reinterpret_cast<void**>(reinterpret_cast<uint8_t*>(chat) + 0x400);
            }

            g_selected = selected;
            PaintRows(static_cast<int>(g_blankRows) + (selected - static_cast<int>(g_offset)));
            SetInputText(chat, g_all[static_cast<size_t>(selected)].insert);
            *reinterpret_cast<uint64_t*>(reinterpret_cast<uint8_t*>(suggest) + 0x490) =
                static_cast<uint64_t>(g_blankRows + (selected - static_cast<int>(g_offset)));
            *reinterpret_cast<uint8_t*>(reinterpret_cast<uint8_t*>(suggest) + 0x498) = 1;
            *reinterpret_cast<uint8_t*>(event) = 1;
            Log(L"preview " + g_all[static_cast<size_t>(selected)].insert + L"\n");
            return;
        }

        // Enter with the list open is consumed by the client to "apply the suggestion"
        // (a no-op for our rows), so the message is never sent. Hide our list first,
        // then let the client handle Enter on its normal send path.
        if (visible && isDown && key == 0x0D) {
            ClearEntries(chat);
            HideList(chat);
            ForgetList();
        }

        if (visible && isDown && key == 9) {
            int selected = g_selected >= 0 ? g_selected : 0;
            if (selected < static_cast<int>(g_all.size())) {
                std::wstring accepted = g_all[static_cast<size_t>(selected)].insert;
                SetInputText(chat, accepted);
                ClearEntries(chat);
                HideList(chat);
                g_all.clear();
                g_native.clear();
                g_display.clear();
                g_items.clear();
                g_populated = false;
                g_selected = -1;
                g_offset = 0;
                g_previewIndex = -1;
                g_previewText.clear();
                *reinterpret_cast<uint8_t*>(event) = 1;
                Log(L"tab accepted " + accepted + L"\n");
                return;
            }
        }
    }

    if (g_keyTrampoline != nullptr) {
        reinterpret_cast<void (*)(void*, void*, void*)>(g_keyTrampoline)(chat, edit, event);
    }
}

void ForgetList() {
    g_all.clear();
    g_native.clear();
    g_display.clear();
    g_items.clear();
    g_populated = false;
    g_selected = -1;
    g_offset = 0;
    g_blankRows = 0;
}

// The suggestion list is drawn outside the chat panel, so closing the chat does not
// hide it by itself. A 50 ms timer on the game window hides it whenever the chat is
// no longer visible (whichever way it was closed).
void WatchdogTick() {
    if (g_chat == nullptr) return;
    if (!g_populated) {
        // A stale 0x800 flag makes the client eat Enter ("apply suggestion" with an
        // empty list). Clear it whenever we are not tracking a list.
        void* suggest = *reinterpret_cast<void**>(reinterpret_cast<uint8_t*>(g_chat) + 0x400);
        if (suggest != nullptr &&
            (*reinterpret_cast<uint32_t*>(reinterpret_cast<uint8_t*>(suggest) + 0x1e8) & 0x800) != 0) {
            HideList(g_chat);
        }
        return;
    }
    if (!ChatVisible(g_chat)) {
        if (g_populated) {
            HideList(g_chat);
            ClearEntries(g_chat);
            ForgetList();
        }
        return;
    }
    if (g_populated) {
        void* suggest = *reinterpret_cast<void**>(reinterpret_cast<uint8_t*>(g_chat) + 0x400);
        if (suggest == nullptr || !ParentLooksValid(suggest)) {
            HideList(g_chat);
            ForgetList();
        }
    }
}

LRESULT CALLBACK GameWndProc(HWND hwnd, UINT msg, WPARAM wparam, LPARAM lparam) {
    if (msg == WM_TIMER && wparam == kWatchdogTimer) {
        WatchdogTick();
        return 0;
    }
    return CallWindowProcW(g_oldWndProc, hwnd, msg, wparam, lparam);
}

void EnsureWatchdog() {
    if (g_oldWndProc != nullptr) return;
    HWND hwnd = GameWindow();
    if (hwnd == nullptr) return;
    g_oldWndProc = reinterpret_cast<WNDPROC>(
        SetWindowLongPtrW(hwnd, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(&GameWndProc)));
    if (g_oldWndProc != nullptr) {
        SetTimer(hwnd, kWatchdogTimer, 50, nullptr);
        Log(L"watchdog installed\n");
    }
}

void InstallHook(uintptr_t target, void* hook, uint8_t** trampoline, size_t stolen) {
    *trampoline = reinterpret_cast<uint8_t*>(VirtualAlloc(nullptr, 64, MEM_COMMIT | MEM_RESERVE,
                                                          PAGE_EXECUTE_READWRITE));
    if (*trampoline == nullptr) return;
    memcpy(*trampoline, reinterpret_cast<void*>(target), stolen);
    uint8_t* back = *trampoline + stolen;
    back[0] = 0xFF;
    back[1] = 0x25;
    *reinterpret_cast<uint32_t*>(back + 2) = 0;
    *reinterpret_cast<uint64_t*>(back + 6) = target + stolen;

    DWORD old = 0;
    if (!VirtualProtect(reinterpret_cast<void*>(target), stolen, PAGE_EXECUTE_READWRITE, &old)) return;
    uint8_t* code = reinterpret_cast<uint8_t*>(target);
    code[0] = 0xFF;
    code[1] = 0x25;
    *reinterpret_cast<uint32_t*>(code + 2) = 0;
    *reinterpret_cast<uint64_t*>(code + 6) = reinterpret_cast<uint64_t>(hook);
    VirtualProtect(reinterpret_cast<void*>(target), stolen, old, &old);
    FlushInstructionCache(GetCurrentProcess(), reinterpret_cast<void*>(target), stolen);
}

}  // namespace

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(instance);
        g_module = GetModuleHandleA("Game.patched.exe");
        if (g_module == nullptr) g_module = GetModuleHandleA("PathOfExile.exe");
        if (g_module == nullptr) return FALSE;
        g_base = reinterpret_cast<uintptr_t>(g_module);
        LoadCommands();
        LoadCommandKinds();
        RecordCatalogStamps();
        ResolveApi();
        InstallHook(g_api.suggestBuilder, &SuggestHook, &g_suggestTrampoline, 14);
        InstallHook(g_api.keyHandler, &KeyHook, &g_keyTrampoline, 15);
        Log(L"NativeSuggest loaded (list, preview, args), base=0x" + std::to_wstring(g_base) + L"\n");
    }
    return TRUE;
}
