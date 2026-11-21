"""Generate portable byte signatures for the ChatComplete native hooks.

The signatures are built from the current 3.29.3c client and validated for
uniqueness inside .text. Relocatable/displaceable operand bytes are masked so the
pattern survives ASLR and most rebuilds.

Usage:
    python tools/gen_signatures.py gen
    python tools/gen_signatures.py selfcheck
    python tools/gen_signatures.py scan <other.exe>

Outputs:
    signatures.json
    native/Signatures.generated.h
"""
import json
import os
import struct
import sys
from pathlib import Path

try:
    import capstone
    from capstone import x86
except ImportError:  # pragma: no cover
    capstone = None

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent


def find_client() -> Path:
    """The client to read: $GAME_CLIENT_EXE, else a LocalExile beside or above this repo."""
    if env := os.environ.get("GAME_CLIENT_EXE"):
        return Path(env)
    for base in (ROOT, ROOT.parent, ROOT.parent.parent):
        candidate = base / "LocalExile" / "Client" / "Game.patched.exe"
        if candidate.exists():
            return candidate
    return ROOT / "LocalExile" / "Client" / "Game.patched.exe"


CLIENT = find_client()
OUT_JSON = ROOT / "signatures.json"
OUT_HEADER = ROOT / "native" / "Signatures.generated.h"
OUT_MARKDOWN = ROOT / "SIGNATURES.md"

# name, RVA, minimum signature length in bytes
FUNCTIONS = [
    ("Chat_RebuildSuggestions", 0x140181010, 24),
    ("Chat_HandleKey", 0x140180a90, 24),
    ("Chat_SendMessage", 0x14017f020, 24),
    ("Chat_ClearSuggestions", 0x1401808d0, 24),
    ("UI_GetFactory", 0x1418b6d20, 24),
    ("UI_GetPoolBySize", 0x1418b6a00, 24),
    ("SuggestItem_Ctor", 0x1418c24a0, 24),
    ("WStringVector_Insert", 0x1401b1bd0, 24),
    ("Suggest_AddItem", 0x141904470, 24),
    ("Suggest_ClearAll", 0x141903830, 24),
    ("Suggest_SetVisible", 0x1418b0a30, 24),
    ("WString_CopyCtor", 0x140130840, 24),
    ("WString_Dtor", 0x140134500, 24),
    ("Suggest_SelectRow", 0x141905080, 24),
    ("Suggest_ApplySelected", 0x1401805c0, 24),
    ("SuggestItem_SetText", 0x1418c2c10, 24),
    ("SuggestItem_Attach", 0x1418b5370, 24),
    ("UI_PoolAlloc", 0x1418ba930, 24),
    ("UI_PoolFree", 0x1418ba820, 24),
    ("SuggestItem_Delete", 0x1405c8ad0, 24),
    ("UI_AddChild", 0x1418b5b10, 24),
]


def load_pe(path):
    data = Path(path).read_bytes()
    e_lfanew = struct.unpack_from("<I", data, 0x3C)[0]
    if data[e_lfanew:e_lfanew + 4] != b"PE\0\0":
        raise SystemExit("not a PE file: " + str(path))
    coff = e_lfanew + 4
    num_sections = struct.unpack_from("<H", data, coff + 2)[0]
    opt_size = struct.unpack_from("<H", data, coff + 16)[0]
    opt = coff + 20
    magic = struct.unpack_from("<H", data, opt)[0]
    image_base = struct.unpack_from("<Q", data, opt + 24)[0] if magic == 0x20B else \
        struct.unpack_from("<I", data, opt + 28)[0]
    sections = []
    sec = opt + opt_size
    for i in range(num_sections):
        off = sec + i * 40
        name = data[off:off + 8].rstrip(b"\0").decode(errors="replace")
        vsize, va, rawsize, rawptr = struct.unpack_from("<IIII", data, off + 8)
        sections.append({"name": name, "va": va, "vsize": vsize, "raw": rawptr, "rawsize": rawsize})
    return data, image_base, sections


def rva_to_offset(sections, rva):
    for s in sections:
        span = max(s["vsize"], s["rawsize"])
        if s["va"] <= rva < s["va"] + span:
            return s["raw"] + (rva - s["va"])
    raise SystemExit(f"RVA 0x{rva:x} is not in any section")


def text_section(data, sections):
    for s in sections:
        if s["name"] == ".text":
            return data[s["raw"]:s["raw"] + s["rawsize"]], s["va"]
    raise SystemExit("no .text section")


def mask_instruction(insn, image_base):
    n = insn.size
    mask = [0xFF] * n
    has_imm = any(op.type == x86.X86_OP_IMM for op in insn.operands)
    for op in insn.operands:
        if op.type == x86.X86_OP_IMM:
            relative = insn.group(capstone.CS_GRP_JUMP) or insn.group(capstone.CS_GRP_CALL)
            absolute = op.imm >= 0x10000000
            if relative or absolute:
                start = max(0, n - op.size)
                for i in range(start, n):
                    mask[i] = 0
        elif op.type == x86.X86_OP_MEM:
            rip = op.mem.base == x86.X86_REG_RIP
            absolute = op.mem.base == 0 and op.mem.index == 0
            if (rip or absolute) and not has_imm:
                for i in range(n - 4, n):
                    mask[i] = 0
    return mask


def instruction_mask(code, length, image_base):
    if capstone is None:
        return [0xFF] * length
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    md.detail = True
    mask = []
    consumed = 0
    for insn in md.disasm(code[:length], 0):
        mask.extend(mask_instruction(insn, image_base))
        consumed += insn.size
        if consumed >= length:
            break
    if consumed < length:
        mask.extend([0xFF] * (length - consumed))
    return mask[:length]


def count_matches(text, text_va, pattern, mask):
    first = pattern[0]
    length = len(pattern)
    hits = 0
    start = 0
    while True:
        idx = text.find(bytes([first]), start)
        if idx < 0:
            break
        ok = True
        for i in range(1, length):
            if mask[i] and text[idx + i] != pattern[i]:
                ok = False
                break
        if ok:
            hits += 1
        start = idx + 1
    return hits


def build_signature(name, rva, min_len, data, sections, text, text_va, image_base):
    off = rva_to_offset(sections, rva - image_base)
    code = data[off:off + 640]
    for length in range(min_len, 513, 8):
        length = min(length, len(code))
        mask = instruction_mask(code, length, image_base)
        pattern = list(code[:length])
        for i, m in enumerate(mask):
            if m == 0:
                pattern[i] = 0
        hits = count_matches(text, text_va, pattern, mask)
        if hits == 1:
            return {
                "name": name,
                "rva": rva - image_base,
                "va": rva,
                "length": length,
                "pattern": " ".join(f"{b:02X}" if mask[i] else "??" for i, b in enumerate(pattern)),
                "hits": hits,
            }
    return {"name": name, "rva": rva - image_base, "va": rva, "length": len(code), "pattern": "", "hits": hits, "error": "not unique"}


def cmd_gen():
    data, image_base, sections = load_pe(CLIENT)
    text, text_va = text_section(data, sections)
    print(f"image base 0x{image_base:x}, .text rva 0x{text_va:x}, {len(text)} bytes")
    sigs = []
    for name, rva, min_len in FUNCTIONS:
        sig = build_signature(name, rva, min_len, data, sections, text, text_va, image_base)
        sigs.append(sig)
        status = "ok" if sig["hits"] == 1 else "FAIL"
        print(f"{status:4} {name:28} rva=0x{rva:08x} len={sig['length']:3} {sig['pattern'][:90]}")
    OUT_JSON.write_text(json.dumps({"version": "3.29.3c", "imageBase": image_base, "functions": sigs}, indent=2))
    write_header(sigs)
    write_markdown(sigs)
    print("wrote", OUT_JSON)
    print("wrote", OUT_HEADER)
    print("wrote", OUT_MARKDOWN)


def write_markdown(sigs):
    lines = [
        "# ChatComplete signatures (3.29.3c)",
        "",
        "Generated by `tools/gen_signatures.py`. `??` bytes are masked (relocations,",
        "RIP-relative displacements, address immediates).",
        "",
        "| Function | RVA | Length |",
        "|---|---|---|",
    ]
    for s in sigs:
        lines.append(f"| `{s['name']}` | `0x{s['va']:X}` | {s['length']} |")
    lines += ["", "## Patterns", ""]
    for s in sigs:
        if not s["pattern"]:
            continue
        lines += [f"### {s['name']}", "", "```", s["pattern"], "```", ""]
    OUT_MARKDOWN.write_text("\n".join(lines), encoding="ascii")


def write_header(sigs):
    lines = [
        "// Generated by tools/gen_signatures.py. Do not edit.",
        '#pragma once',
        '#include <cstdint>',
        "",
        "struct ChatSig {",
        "    const char* name;",
        "    uint32_t rva;",
        "    const char* pattern;",
        "};",
        "",
        "static const ChatSig kChatSigs[] = {",
    ]
    for s in sigs:
        if not s["pattern"]:
            continue
        lines.append(f'    {{ "{s["name"]}", static_cast<uint32_t>(0x{s["rva"]:x}), "{s["pattern"]}" }},')
    lines += ["};", "", "static const uint32_t kChatSigCount = sizeof(kChatSigs) / sizeof(kChatSigs[0]);", ""]
    OUT_HEADER.write_text("\n".join(lines), encoding="ascii")


def parse_pattern(pattern):
    parts = pattern.split()
    pat = bytearray()
    mask = bytearray()
    for p in parts:
        if p == "??":
            pat.append(0)
            mask.append(0)
        else:
            pat.append(int(p, 16))
            mask.append(0xFF)
    return bytes(pat), bytes(mask)


def cmd_scan(path):
    data, image_base, sections = load_pe(path)
    text, text_va = text_section(data, sections)
    sigs = json.loads(OUT_JSON.read_text())["functions"]
    ok = 0
    for s in sigs:
        pat, mask = parse_pattern(s["pattern"])
        found = -1
        start = 0
        while True:
            idx = text.find(pat[:1], start)
            if idx < 0:
                break
            good = all(not mask[i] or text[idx + i] == pat[i] for i in range(1, len(pat)))
            if good:
                found = idx
                break
            start = idx + 1
        if found >= 0:
            ok += 1
            old = s.get("va", image_base + s["rva"])
            new = image_base + text_va + found
            delta = new - old
            print(f"ok   {s['name']:28} 0x{new:08x}  (was 0x{old:08x}, delta {delta:+d})")
        else:
            print(f"MISS {s['name']:28} signature not found")
    print(f"\n{ok}/{len(sigs)} signatures found in {path}")


def main():
    if len(sys.argv) < 2 or sys.argv[1] not in ("gen", "scan", "selfcheck"):
        print(__doc__)
        return
    if sys.argv[1] == "gen":
        cmd_gen()
    elif sys.argv[1] == "selfcheck":
        cmd_scan(CLIENT)
    else:
        cmd_scan(sys.argv[2])


if __name__ == "__main__":
    main()
