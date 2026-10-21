"""Keys and mouse events as bytes, the way a terminal sends them.

Two flavours of keyboard:

- legacy (default): what iTerm2, Terminal.app or xterm send — control
  bytes, ESC-prefixed Alt, CSI sequences with a modifier parameter for
  arrows and the like. Combinations that legacy terminals cannot express
  (Ctrl+Shift+letter, Cmd+key) go out as kitty CSI u, as kitty would.
- kitty: the kitty keyboard protocol that ltt asks for (flags 1|4|8|16):
  every key, plain text included, as CSI u with alternate keys and text.

A key is written like "ctrl+s", "Ctrl+Shift+N", "alt+1", "shift+down",
"esc", "a", "Ф" or "+" (case of the modifiers does not matter).
"""

SHIFT, ALT, CTRL, SUPER = 1, 2, 4, 8
_MOD_NAMES = {"shift": SHIFT, "alt": ALT, "option": ALT, "opt": ALT, "ctrl": CTRL, "control": CTRL,
              "cmd": SUPER, "super": SUPER}

# Keys with a cursor-style legacy sequence: CSI [1;m] X
_CURSOR = {"up": "A", "down": "B", "right": "C", "left": "D", "home": "H", "end": "F"}
# Keys with a tilde-style legacy sequence: CSI n [;m] ~
_TILDE = {"insert": 2, "ins": 2, "delete": 3, "del": 3, "pgup": 5, "pageup": 5, "pgdn": 6, "pagedown": 6}
# Keys with a code of their own (kitty) and a byte (legacy)
_NAMED = {"enter": (13, "\r"), "return": (13, "\r"), "tab": (9, "\t"), "backspace": (127, "\x7f"),
          "esc": (27, "\x1b"), "escape": (27, "\x1b"), "space": (32, " ")}

ESC = "\x1b"
PASTE_START, PASTE_END = b"\x1b[200~", b"\x1b[201~"


def parse(spec):
    """'ctrl+shift+n' -> (mods, key) with key lower-cased for named keys, as typed for characters."""
    if spec in ("+",):
        return 0, "+"
    parts = spec.split("+")
    if spec.endswith("++"):  # "ctrl++"
        parts = parts[:-2] + ["+"]
    *mods, key = parts
    m = 0
    for name in mods:
        try:
            m |= _MOD_NAMES[name.strip().lower()]
        except KeyError:
            raise ValueError(f"unknown modifier {name!r} in key {spec!r}") from None
    if len(key) > 1:
        key = key.lower()
        if key not in _CURSOR and key not in _TILDE and key not in _NAMED:
            raise ValueError(f"unknown key {key!r} in {spec!r}")
    return m, key


def kitty(code, mods=0, shifted=None, base=None, text=None, event=None):
    """A raw kitty CSI u sequence: CSI code[:shifted[:base]] [; 1+mods[:event]] [; text codepoints] u."""
    key = str(code)
    if shifted is not None or base is not None:
        key += ":" + ("" if shifted is None else str(shifted))
        if base is not None:
            key += ":" + str(base)
    fields = [key]
    if mods or event is not None or text:
        fields.append(str(1 + mods) + ("" if event is None else f":{event}"))
    if text:
        fields.append(":".join(str(ord(c)) for c in text))
    return f"\x1b[{';'.join(fields)}u".encode()


def _mod_param(mods):
    return 1 + mods


def legacy(spec):
    """Bytes a legacy (xterm-style) terminal sends for the key."""
    mods, key = parse(spec)
    if key in _CURSOR:
        final = _CURSOR[key]
        return (f"\x1b[1;{_mod_param(mods)}{final}" if mods else f"\x1b[{final}").encode()
    if key in _TILDE:
        n = _TILDE[key]
        return (f"\x1b[{n};{_mod_param(mods)}~" if mods else f"\x1b[{n}~").encode()
    if mods & SUPER:
        return kitty_key(spec)
    if key in _NAMED:
        code, byte = _NAMED[key]
        if key == "tab" and mods == SHIFT:
            return b"\x1b[Z"
        if key == "backspace" and mods == CTRL:
            return b"\x08"
        if key == "space" and mods == CTRL:
            return b"\x00"
        if mods == ALT:
            return (ESC + byte).encode()
        if mods:
            return kitty_key(spec)
        return byte.encode()
    # a character
    if mods & CTRL and mods & SHIFT:
        return kitty_key(spec)
    if mods & CTRL:
        c = key.lower()
        if "a" <= c <= "z":
            ctrl = chr(ord(c) - 96)
            return ((ESC if mods & ALT else "") + ctrl).encode()
        return kitty_key(spec)
    if mods == ALT:
        return (ESC + key).encode()
    if mods == SHIFT:
        return key.upper().encode()
    if mods:
        return kitty_key(spec)
    return key.encode()


def kitty_key(spec, base=None):
    """Bytes a kitty-protocol terminal sends for the key (flags 1|4|8|16, as ltt asks).

    base: the US-layout letter of the physical key when the layout differs
    (AZERTY 'a' sits on US 'q': kitty_key("ctrl+a", base="q")).
    """
    mods, key = parse(spec)
    if key in _CURSOR:
        final = _CURSOR[key]
        return (f"\x1b[1;{_mod_param(mods)}{final}" if mods else f"\x1b[{final}").encode()
    if key in _TILDE:
        n = _TILDE[key]
        return (f"\x1b[{n};{_mod_param(mods)}~" if mods else f"\x1b[{n}~").encode()
    if key in _NAMED:
        return kitty(_NAMED[key][0], mods)
    ch = key
    lower = ch.lower()
    shifted = ord(ch.upper()) if (mods & SHIFT or ch != lower) and ch.upper() != lower else None
    if ch != lower:
        mods |= SHIFT
    text = None
    if not mods & (CTRL | ALT | SUPER):
        text = ch.upper() if mods & SHIFT else ch
    return kitty(ord(lower), mods, shifted=shifted, base=None if base is None else ord(base), text=text)


def encode(spec, mode="legacy"):
    """The bytes of a key spec in the given keyboard mode; bytes pass through as they are."""
    if isinstance(spec, (bytes, bytearray)):
        return bytes(spec)
    return kitty_key(spec) if mode == "kitty" else legacy(spec)


def is_lone_esc(data):
    return data == b"\x1b"


# ---- Mouse (SGR 1006); x, y are 0-based screen cells ----

def mouse(button, x, y, pressed=True):
    return f"\x1b[<{button};{x + 1};{y + 1}{'M' if pressed else 'm'}".encode()


LEFT, MIDDLE, RIGHT = 0, 1, 2
WHEEL_UP, WHEEL_DOWN, WHEEL_LEFT, WHEEL_RIGHT = 64, 65, 66, 67
MOTION = 32  # added to the button while it is held and the mouse moves
MOUSE_SHIFT, MOUSE_ALT, MOUSE_CTRL = 4, 8, 16
