'use strict';
// Chat input probe for the LocalExile 3.29.3c client.
// Load it into the client-control Frida session (see probe.py). It only reads state and logs.

const module = Process.getModuleByName('Game.patched.exe');
const at = va => module.base.add(ptr(va).sub(ptr('0x140000000')));

let chat = null; // the ChatBox/input object captured from the key handler
let lastEdit = null;
let pendingInsert = null;
const insertText = new NativeFunction(at('0x1418903f0'), 'uint64', ['pointer', 'uint64', 'pointer']);
const showChat = new NativeFunction(at('0x1401827a0'), 'void', ['pointer', 'uint8', 'uint8']);
const setVisible = new NativeFunction(at('0x1418b0a30'), 'void', ['pointer', 'uint8']);
const chatUpdate = new NativeFunction(at('0x14017fb20'), 'void', ['pointer']);
const openChatChannel = new NativeFunction(at('0x14017e5b0'), 'void', ['pointer', 'uint64']);

// The client-control script already hooks this per-frame point; queue work here to run on the game thread.
let pendingWork = null;
function onFrame(fn) {
  return new Promise((resolve, reject) => {
    if (pendingWork !== null) { reject(new Error('another frame task is pending')); return; }
    pendingWork = { fn, resolve, reject };
  });
}
Interceptor.attach(at('0x141528fbf'), {
  onEnter() {
    if (pendingWork === null) return;
    const work = pendingWork; pendingWork = null;
    try { work.resolve(work.fn()); } catch (e) { work.reject(e); }
  }
});

function readWString(addr) {
  try {
    // MSVC std::wstring: union at +0 (16 bytes), size at +16, capacity at +24.
    // Older probe code used +8/+16; keep that as a fallback for custom strings.
    let size = addr.add(16).readU64().toNumber();
    let cap = addr.add(24).readU64().toNumber();
    if (size > 8192 || cap > 8192 || cap < size) {
      size = addr.add(8).readU64().toNumber();
      cap = addr.add(16).readU64().toNumber();
    }
    if (size === 0) return '';
    let ptr = addr;
    if (cap >= 8) ptr = addr.readPointer();
    return ptr.readUtf16String(size);
  } catch (e) {
    return '<err ' + e + '>';
  }
}

// Builds a std::wstring with the client's own allocator (FUN_140130840 copy ctor).
// The result is intentionally leaked; it must not be freed by our runtime.
function makeClientString(text) {
  // A valid source std::wstring: short strings keep their characters inline.
  const src = Memory.alloc(32);
  if (text.length < 8) {
    for (let i = 0; i < text.length; i++) src.add(i * 2).writeU16(text.charCodeAt(i));
    src.add(text.length * 2).writeU16(0);
    src.add(16).writeU64(text.length);
    src.add(24).writeU64(7);
  } else {
    const buf = Memory.alloc((text.length + 1) * 2);
    for (let i = 0; i < text.length; i++) buf.add(i * 2).writeU16(text.charCodeAt(i));
    buf.add(text.length * 2).writeU16(0);
    src.writePointer(buf);
    src.add(16).writeU64(text.length);
    src.add(24).writeU64(text.length + 1);
  }
  // Copy with the client allocator so the result can be moved into client objects.
  const out = Memory.alloc(32);
  const copy = new NativeFunction(at('0x140130840'), 'pointer', ['pointer', 'pointer']);
  copy(out, src);
  return out;
}

function chatText(c) {
  try {
    const edit = c.add(0x3f0).readPointer();
    lastEdit = edit;
    // The key handler gets the editable text from (*(edit + 0x298)) + 0xa0; that is edit + 0x338.
    const input = readWString(edit.add(0x338));
    const direct = readWString(edit.add(0xb90));
    let viaPointer = '<invalid>';
    let p = null;
    try {
      p = edit.add(0xb90).readPointer();
      viaPointer = readWString(p);
    } catch (e) { viaPointer = '<err ' + e + '>'; }
    return { edit: edit.toString(), text: input, direct, viaPointer, p90: p === null ? null : p.toString() };
  } catch (e) {
    return { edit: '<err>', text: '<err ' + e + '>' };
  }
}

// FUN_140180a90(this, ?, keyEvent): the chat input's key handler.
function hex(addr, n) {
  try { return Array.from(new Uint8Array(addr.readByteArray(n)), b => b.toString(16).padStart(2, '0')).join(' '); }
  catch (e) { return '<err ' + e + '>'; }
}

Interceptor.attach(at('0x140180a90'), {
  onEnter(args) {
    try {
      chat = args[0];
      this.event = args[2];
      const key = args[2].add(4).readS32();
      const flag = args[2].add(8).readU8();
      const t = chatText(chat);
      const open = chat.add(0x420).readPointer().toString();
      const channel = chat.add(0x498).readU8();
      const mode = chat.add(0x3f8).readS32();
      send({ kind: 'key', key, flag, chat: chat.toString(), edit: t.edit, text: t.text,
             open, channel, mode, ev: hex(args[2], 32), p2: args[1].toString(),
             ret: this.returnAddress.toString() });
    } catch (e) {
      send({ kind: 'key', error: String(e) });
    }
  },
  onLeave() {
    try {
      let inserted = null;
      if (pendingInsert !== null) {
        const before = chatText(chat);
        const edit = chat.add(0x3f0).readPointer();
        const ws = Memory.alloc(32);
        ws.writePointer(Memory.allocUtf16String(pendingInsert));
        ws.add(8).writeU64(pendingInsert.length);
        ws.add(16).writeU64(pendingInsert.length + 1);
        const ret = insertText(edit, before.text.length, ws);
        inserted = { text: pendingInsert, at: before.text.length, before: before.text, ret: ret.toString() };
        pendingInsert = null;
      }
      const t = chatText(chat);
      send({ kind: 'keyAfter', text: t.text, inserted, ev: hex(this.event, 32) });
    } catch (e) {
      send({ kind: 'keyAfter', error: String(e) });
    }
  }
});

// FUN_140181010(this): builds the chat suggestion/history list from the typed text.
Interceptor.attach(at('0x140181010'), {
  onEnter(args) {
    try {
      chat = args[0];
      const t = chatText(args[0]);
      send({ kind: 'buildSuggest', chat: args[0].toString(), text: t.text });
    } catch (e) {
      send({ kind: 'buildSuggest', error: String(e) });
    }
  }
});

// FUN_1418903f0(edit, position, textView): candidate text insert.
Interceptor.attach(at('0x1418903f0'), {
  onEnter(args) {
    try {
      send({ kind: 'insertFnCall', edit: args[0].toString(), position: args[1].toString(),
             view: hex(args[2], 32) });
    } catch (e) {
      send({ kind: 'insertFnCall', error: String(e) });
    }
  }
});

// FUN_14017f020(this): the chat send/click handler.
Interceptor.attach(at('0x14017f020'), {
  onEnter(args) {
    try {
      chat = args[0];
      const t = chatText(chat);
      send({ kind: 'send', chat: chat.toString(), edit: t.edit, text: t.text });
    } catch (e) {
      send({ kind: 'send', error: String(e) });
    }
  }
});

// FUN_14017cdd0(this, std::wstring* line): parse the finished line.
Interceptor.attach(at('0x14017cdd0'), {
  onEnter(args) {
    try {
      send({ kind: 'attempt', text: readWString(args[1]) });
    } catch (e) {
      send({ kind: 'attempt', error: String(e) });
    }
  }
});

rpc.exports = {
  useChat(address) {
    try {
      chat = ptr(address);
      const t = chatText(chat);
      return { chat: chat.toString(), edit: t.edit, text: t.text };
    } catch (e) {
      return { error: String(e) };
    }
  },
  state() {
    if (chat === null) return { chat: null };
    const t = chatText(chat);
    return { chat: chat.toString(), edit: t.edit, text: t.text };
  },
  queueInsert(text) {
    pendingInsert = text;
    return { pendingInsert };
  },
  openChat() {
    return onFrame(() => {
      if (chat === null) throw new Error('no chat object');
      openChatChannel(chat, 0);
      const edit = chat.add(0x3f0).readPointer();
      setVisible(edit, 1);
      return chatText(chat);
    });
  },
  activate() {
    return onFrame(() => {
      if (chat === null) throw new Error('no chat object');
      const edit = chat.add(0x3f0).readPointer();
      const vtable = edit.readPointer();
      const focus = new NativeFunction(vtable.add(0x3a0).readPointer(), 'void', ['pointer']);
      focus(edit);
      chatUpdate(chat);
      return chatText(chat);
    });
  },
  poolInfo() {
    return onFrame(() => {
      const factoryPtr = new NativeFunction(at('0x1418b6d20'), 'pointer', [])();
      const factory = factoryPtr.readPointer();
      const pool = new NativeFunction(at('0x1418b6a00'), 'pointer', ['pointer', 'uint64'])(factory, 0x6b8);
      return { factoryPtr: factoryPtr.toString(), factory: factory.toString(), pool: pool.toString(),
               poolVtable: pool.isNull() ? null : pool.readPointer().toString() };
    });
  },
  allocFromPool(poolAddress) {
    return onFrame(() => {
      const pool = ptr(poolAddress);
      const vtable = pool.readPointer();
      const alloc = new NativeFunction(vtable.add(8).readPointer(), 'pointer', ['pointer', 'uint64']);
      const mem = alloc(pool, 0x6b8);
      const item = new NativeFunction(at('0x1418c24a0'), 'pointer', ['pointer'])(mem);
      item.add(8).writeU64(0x6b8);
      return { pool: pool.toString(), vtable: vtable.toString(), mem: mem.toString(),
               item: item.toString(), itemVtable: item.readPointer().toString() };
    });
  },
  allocItem() {
    return onFrame(() => {
      const factoryPtr = new NativeFunction(at('0x1418b6d20'), 'pointer', [])();
      const factory = factoryPtr.readPointer();
      const pool = new NativeFunction(at('0x1418b6a00'), 'pointer', ['pointer', 'uint64'])(factory, 0x6b8);
      const alloc = new NativeFunction(pool.readPointer().add(8).readPointer(), 'pointer', ['pointer', 'uint64']);
      const mem = alloc(pool, 0x6b8);
      const item = new NativeFunction(at('0x1418c24a0'), 'pointer', ['pointer'])(mem);
      item.add(8).writeU64(0x6b8);
      const v220 = item.add(0x220).readPointer();
      const fn44 = new NativeFunction(v220.add(0x10).readPointer(), 'void', ['pointer', 'uint64']);
      fn44(item.add(0x220), 1);
      return { factory: factory.toString(), pool: pool.toString(), mem: mem.toString(),
               item: item.toString(), vtable: item.readPointer().toString(),
               v220: v220.toString(), v298: item.add(0x298).readPointer().toString() };
    });
  },
  attachItem(address) {
    return onFrame(() => {
      if (chat === null) throw new Error('no chat object');
      const item = ptr(address);
      const suggest = chat.add(0x400).readPointer();
      const fn = new NativeFunction(item.readPointer().add(0x138).readPointer(), 'void', ['pointer', 'pointer']);
      fn(item, suggest.add(0x170));
      return { ok: true, item: item.toString(), suggest: suggest.toString() };
    });
  },
  setItemText(address, text) {
    return onFrame(() => {
      const item = ptr(address);
      const sub = item.add(0x298);
      const vtable = sub.readPointer();
      const setter = new NativeFunction(vtable.add(0x10).readPointer(), 'void', ['pointer', 'pointer']);
      const ws = makeClientString(text);
      setter(sub, ws);
      return { ok: true, item: item.toString(), text };
    });
  },
  showSuggest() {
    return onFrame(() => {
      if (chat === null) throw new Error('no chat object');
      const suggest = chat.add(0x400).readPointer();
      const visible = new NativeFunction(at('0x1418b0a30'), 'void', ['pointer', 'uint8']);
      visible(suggest, 1);
      const show = new NativeFunction(at('0x141905080'), 'pointer', ['pointer', 'uint64']);
      show(suggest, 0);
      return { visible: suggest.add(0x498).readU8(), flags: suggest.add(0x1e8).readU32() };
    });
  },
  addItem(address, text) {
    return onFrame(() => {
      if (chat === null) throw new Error('no chat object');
      const item = ptr(address);
      const suggest = chat.add(0x400).readPointer();
      const begin = suggest.add(0x4a8).readPointer();
      const end = suggest.add(0x4b0).readPointer();
      const count = end.sub(begin).toInt32() / 0x18;
      const strings = chat.add(0x408);
      const send = chat.add(0x410).readPointer();
      const sbegin = strings.readPointer();
      const index = send.sub(sbegin).toInt32() / 0x20;
      const ws = makeClientString(text);
      const append = new NativeFunction(at('0x1401b1bd0'), 'pointer', ['pointer', 'pointer', 'pointer']);
      append(strings, send, ws);
      const add = new NativeFunction(at('0x141904470'), 'pointer', ['pointer', 'uint64', 'pointer', 'uint64']);
      add(suggest, count, item, index);
      const visible = new NativeFunction(at('0x1418b0a30'), 'void', ['pointer', 'uint8']);
      visible(suggest, 1);
      const show = new NativeFunction(at('0x141905080'), 'pointer', ['pointer', 'uint64']);
      show(suggest, 0);
      return { count, index, suggest: suggest.toString(), item: item.toString() };
    });
  },
  populate(texts) {
    return onFrame(() => {
      if (chat === null) throw new Error('no chat object');
      const clear = new NativeFunction(at('0x1401808d0'), 'void', ['pointer']);
      clear(chat);
      const suggest = chat.add(0x400).readPointer();
      const factoryPtr = new NativeFunction(at('0x1418b6d20'), 'pointer', [])();
      const factory = factoryPtr.readPointer();
      const pool = new NativeFunction(at('0x1418b6a00'), 'pointer', ['pointer', 'uint64'])(factory, 0x6b8);
      const alloc = new NativeFunction(pool.readPointer().add(8).readPointer(), 'pointer', ['pointer', 'uint64']);
      const ctor = new NativeFunction(at('0x1418c24a0'), 'pointer', ['pointer']);
      const add = new NativeFunction(at('0x141904470'), 'pointer', ['pointer', 'uint64', 'pointer', 'uint64']);
      const append = new NativeFunction(at('0x1401b1bd0'), 'pointer', ['pointer', 'pointer', 'pointer']);
      const visible = new NativeFunction(at('0x1418b0a30'), 'void', ['pointer', 'uint8']);
      const show = new NativeFunction(at('0x141905080'), 'pointer', ['pointer', 'uint64']);
      const strings = chat.add(0x408);
      let count = 0;
      for (const text of texts) {
        const mem = alloc(pool, 0x6b8);
        const item = ctor(mem);
        item.add(8).writeU64(0x6b8);
        const v220 = item.add(0x220).readPointer();
        const fn44 = new NativeFunction(v220.add(0x10).readPointer(), 'void', ['pointer', 'uint64']);
        fn44(item.add(0x220), 1);
        const attach = new NativeFunction(item.readPointer().add(0x138).readPointer(), 'void', ['pointer', 'pointer']);
        attach(item, suggest.add(0x170));
        const sub = item.add(0x298);
        const setter = new NativeFunction(sub.readPointer().add(0x10).readPointer(), 'void', ['pointer', 'pointer']);
        const ws = makeClientString(text);
        setter(sub, ws);
        const send = chat.add(0x410).readPointer();
        const sbegin = strings.readPointer();
        const index = send.sub(sbegin).toInt32() / 0x20;
        append(strings, send, ws);
        add(suggest, count, item, index);
        count++;
      }
      visible(suggest, 1);
      show(suggest, 0);
      return { count, visible: suggest.add(0x498).readU8(), flags: suggest.add(0x1e8).readU32() };
    });
  },
  readOnFrame() {
    return onFrame(() => chatText(chat));
  },
  insertOnFrame(text, position) {
    return onFrame(() => {
      if (chat === null) throw new Error('no chat object yet');
      const before = chatText(chat);
      const edit = chat.add(0x3f0).readPointer();
      const at = position < 0 ? before.text.length : position;
      const ws = Memory.alloc(32);
      ws.writePointer(Memory.allocUtf16String(text));
      ws.add(8).writeU64(text.length);
      ws.add(16).writeU64(text.length + 1);
      const ret = insertText(edit, at, ws);
      return { before: before.text, position: at, inserted: text, ret: ret.toString(),
               after: chatText(chat).text };
    });
  },
  // Calls the client's own key handler, exactly as the input system does.
  // One frame: set the text, rebuild, then send Down (first press + repeat) and
  // report the input after each, without any external interleaving.
  repeatTest(text) {
    return onFrame(() => {
      if (chat === null) throw new Error('no chat object');
      const edit = chat.add(0x3f0).readPointer();
      const label = edit.add(0x298);
      const setter = new NativeFunction(label.readPointer().add(0x10).readPointer(), 'void', ['pointer', 'pointer']);
      setter(label, makeClientString(text));
      new NativeFunction(at('0x140181010'), 'void', ['pointer'])(chat);
      const handler = new NativeFunction(at('0x140180a90'), 'void', ['pointer', 'pointer', 'pointer']);
      const ev = Memory.alloc(16);
      for (let i = 0; i < 16; i++) ev.add(i).writeU8(0);
      ev.add(4).writeU32(0x28);
      const after = [];
      handler(chat, ptr(0), ev);
      after.push({ flag: ev.add(8).readU8(), text: chatText(chat).text });
      ev.add(8).writeU8(1);  // auto-repeat flag
      handler(chat, ptr(0), ev);
      after.push({ flag: ev.add(8).readU8(), text: chatText(chat).text });
      handler(chat, ptr(0), ev);
      after.push({ flag: ev.add(8).readU8(), text: chatText(chat).text });
      return { start: text, after };
    });
  },
  simulateKey(vk) {
    return onFrame(() => {
      if (chat === null) throw new Error('no chat object');
      const ev = Memory.alloc(16);
      for (let i = 0; i < 16; i++) ev.add(i).writeU8(0);
      ev.add(4).writeU32(vk);
      ev.add(8).writeU8(0);
      const fn = new NativeFunction(at('0x140180a90'), 'void', ['pointer', 'pointer', 'pointer']);
      fn(chat, ptr(0), ev);
      const t = chatText(chat);
      return { vk, handled: ev.readU8(), text: t.text };
    });
  },
  // Same call with the auto-repeat flag the client uses for repeated keydowns.
  simulateKeyRepeat(vk) {
    return onFrame(() => {
      if (chat === null) throw new Error('no chat object');
      const ev = Memory.alloc(16);
      for (let i = 0; i < 16; i++) ev.add(i).writeU8(0);
      ev.add(4).writeU32(vk);
      ev.add(8).writeU8(1);
      const fn = new NativeFunction(at('0x140180a90'), 'void', ['pointer', 'pointer', 'pointer']);
      fn(chat, ptr(0), ev);
      const t = chatText(chat);
      return { vk, handled: ev.readU8(), text: t.text };
    });
  },
  suggestInfo() {
    try {
      if (chat === null) return { error: 'no chat' };
      const suggest = chat.add(0x400).readPointer();
      const begin = suggest.add(0x4a8).readPointer();
      const end = suggest.add(0x4b0).readPointer();
      const cap = suggest.add(0x4b8).readPointer();
      const count = end.sub(begin).toInt32() / 0x18;
      const texts = [];
      for (let i = 0; i < Math.min(count, 8); i++) {
        const item = begin.add(i * 0x18).readPointer();
        if (item.isNull()) { texts.push(null); continue; }
        texts.push({ item: item.toString(), text: readWString(item.add(0x338)),
                     index: begin.add(i * 0x18 + 8).readU64().toString(),
                     flags: item.add(0x1e8).readU32().toString(16) });
      }
      return {
        suggest: suggest.toString(),
        count,
        capacity: cap.sub(begin).toInt32() / 0x18,
        flags1e8: suggest.add(0x1e8).readU32().toString(16),
        visible800: (suggest.add(0x1e8).readU32() >>> 11) & 1,
        vis498: suggest.add(0x498).readU8(),
        sel490: suggest.add(0x490).readU64().toString(),
        strings: chat.add(0x410).readPointer().sub(chat.add(0x408).readPointer()).toInt32() / 0x20,
        parent488: suggest.add(0x488).readPointer().toString(),
        texts
      };
    } catch (e) { return { error: String(e) }; }
  },
  dump() {
    if (chat === null) return { error: 'no chat object yet' };
    try {
      const e = chat.add(0x3f0).readPointer();
      const sub = e.add(0x298);
      const sug = chat.add(0x400).readPointer();
      const begin = sug.add(0x4a8).readPointer();
      const end = sug.add(0x4b0).readPointer();
      const count = end.sub(begin).toInt32() / 0x18;
      return {
        chat: chat.toString(), edit: e.toString(), suggest: sug.toString(),
        editHead: hex(e, 0x40),
        editB80: hex(e.add(0xb80), 0x80),
        sub: hex(sub, 0x40),
        subVtable: hex(sub.readPointer(), 0x40),
        chat400: hex(chat.add(0x400), 0x40),
        vec408: hex(chat.add(0x408), 0x20),
        vec448: hex(chat.add(0x448), 0x20),
        vec468: hex(chat.add(0x468), 0x20),
        suggestHead: hex(sug, 0x40),
        suggestIndex: sug.add(0x490).readU64().toString(),
        suggestVisible: sug.add(0x498).readU8(),
        suggestVector: hex(sug.add(0x4a8), 0x30),
        suggestEntries: count,
        suggestFirst: count > 0 ? hex(begin, Math.min(count, 4) * 0x18) : ''
      };
    } catch (e) { return { error: String(e) }; }
  },
  // Sets the edit text through the client's own label setter, then rebuilds suggestions.
  setAndRebuild(text) {
    return onFrame(() => {
      if (chat === null) throw new Error('no chat object');
      const edit = chat.add(0x3f0).readPointer();
      const sub = edit.add(0x298);
      const vtable = sub.readPointer();
      const setter = new NativeFunction(vtable.add(0x10).readPointer(), 'void', ['pointer', 'pointer']);
      setter(sub, makeClientString(text));
      const rebuild = new NativeFunction(at('0x140181010'), 'void', ['pointer']);
      rebuild(chat);
      return { text: chatText(chat).text };
    });
  },
  forceText(text) {
    return onFrame(() => {
      if (chat === null) throw new Error('no chat object');
      const edit = chat.add(0x3f0).readPointer();
      const sub = edit.add(0x298);
      const vtable = sub.readPointer();
      const setter = new NativeFunction(vtable.add(0x10).readPointer(), 'void', ['pointer', 'pointer']);
      setter(sub, makeClientString(text));
      return { text: chatText(chat).text };
    });
  },
  setText(text) {
    return onFrame(() => {
      if (chat === null) throw new Error('no chat object yet: press Enter in the game once');
      const before = chatText(chat);
      const edit = chat.add(0x3f0).readPointer();
      const ws = Memory.alloc(32);
      ws.writePointer(Memory.allocUtf16String(text));
      ws.add(8).writeU64(text.length);
      ws.add(16).writeU64(text.length + 1);
      const ret = insertText(edit, 0, ws);
      return { before: before.text, inserted: text, ret: ret.toString(), after: chatText(chat).text };
    });
  }
};

send({ kind: 'loaded', base: module.base.toString() });
