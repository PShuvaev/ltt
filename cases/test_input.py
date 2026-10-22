"""Input other than plain typing: escape sequences cut short, too long or unknown, terminal replies,
broken UTF-8, control bytes, the keypad and the function keys, keys as other terminals send them."""
import signal
import time

import pytest

from harness import keys as K

TEXT = "START\nsecond\nthird\n"  # four lines: the last one is empty


def open_file(ltt, ws, text=TEXT):
    ws.write("a.txt", text)
    return ltt(ws.path("a.txt"))


def then_mark(app, data, mark="|"):
    """Send a complete sequence (it may draw nothing), then a key that draws; the frame after both."""
    app.write(data)
    return app.type(mark)


def kitty(code, *rest):
    return f"\x1b[{';'.join([str(code), *rest])}u".encode()


# ---------------------------------------------------------------- escape sequences

def test_esc_esc_arrow_is_alt_arrow_not_text(ltt, ws):  # T8
    app = open_file(ltt, ws)
    s = app.press(b"\x1b\x1b[B")  # how meta-mode terminals send Option+Down
    assert s.editor_lines[:3] == ["START", "second", "third"]
    moved = s.pos
    app.press("ctrl+home")
    assert app.press("alt+down").pos == moved != (1, 4, 1, 0)


def test_esc_esc_alone_is_two_escapes(ltt, ws):
    app = open_file(ltt, ws)
    app.type("X")
    assert app.press("ctrl+q").mode == "CONFIRM"
    app.write(b"\x1b\x1b")  # the first cancels the question, the second leaves the editor
    s = app.wait(lambda s: s.mode == "TREE", what="both Escs")
    assert s.status.startswith("File panel.") and s.editor_lines[0] == "XSTART" and app.alive


def test_long_csi_is_never_typed(ltt, ws):  # T8: one over 32 characters had its tail typed
    app = open_file(ltt, ws)
    s = then_mark(app, b"\x1b[" + b"1;" * 20 + b"1A")
    assert s.editor_lines[0] == "|START" and s.pos == (1, 4, 2, 0)


@pytest.mark.parametrize("pairs", [200, 2500])  # 400 bytes; 5000 bytes, more than one read
def test_overlong_csi_is_dropped_whole(ltt, ws, pairs):  # T8
    app = open_file(ltt, ws)
    s = then_mark(app, b"\x1b[" + b"1;" * pairs + b"B")  # not even read as Down
    assert s.editor_lines[0] == "|START" and s.pos == (1, 4, 2, 0)


@pytest.mark.parametrize("seq", [
    pytest.param(b"\x1b[5x", id="unknown final"),
    pytest.param(b"\x1b[99~", id="unknown tilde key"),
    pytest.param(b"\x1b[201~", id="paste end alone"),
    pytest.param(b"\x1b[2 q", id="intermediate byte"),
    pytest.param(b"\x1bOz", id="unknown SS3"),
    pytest.param(b"\x1b[57441;2u", id="kitty lone Shift"),
    pytest.param(b"\x1b[97;1:3u", id="kitty key release"),
    pytest.param(b"\x1b[57344u", id="kitty private-use key"),
    pytest.param(b"\x1b[M +&", id="X10 mouse report"),
])
def test_unknown_sequences_are_ignored(ltt, ws, seq):
    app = open_file(ltt, ws)
    s = then_mark(app, seq)
    assert s.editor_lines[:2] == ["|START", "second"] and s.pos == (1, 4, 2, 0) and s.mode == "EDITOR"


@pytest.mark.parametrize("reply", [
    pytest.param(b"\x1b[12;40R", id="cursor position report"),
    pytest.param(b"\x1b[?62;22c", id="device attributes"),
    pytest.param(b"\x1b[?29u", id="kitty flags"),
    pytest.param(b"\x1b[?2026;2$y", id="mode report"),
    pytest.param(b"\x1b]11;rgb:1e1e/1e1e/1e1e\x07", id="OSC 11 colour, BEL"),
    pytest.param(b"\x1b]11;rgb:0000/0000/0000\x1b\\", id="OSC 11 colour, ST"),
    pytest.param(b"\x1bP>|kitty(0.30)\x1b\\", id="DCS version"),
])
def test_terminal_replies_are_not_typed(ltt, ws, reply):  # D12
    app = open_file(ltt, ws)
    s = then_mark(app, reply)
    assert s.editor_lines[0] == "|START" and s.pos == (1, 4, 2, 0)


def test_sequence_split_across_reads_is_one_key(ltt, ws):
    app = open_file(ltt, ws)
    app.write(b"\x1b[")
    time.sleep(0.01)  # on purpose: the rest arrives a moment later, within ltt's 40 ms
    app.write(b"B")
    s = app.type("|")
    assert s.editor_lines[:2] == ["START", "|second"]


def test_sequence_cut_short_is_dropped_and_the_next_key_counts(ltt, ws):
    app = open_file(ltt, ws)
    app.write(b"\x1b[1;")
    time.sleep(0.15)  # on purpose: longer than ltt waits for the rest
    s = app.type("x")
    assert s.editor_lines[0] == "xSTART" and s.pos == (1, 4, 2, 0)


def test_key_that_cuts_a_sequence_short_still_counts(ltt, ws):
    app = open_file(ltt, ws)
    s = app.press(b"\x1b[1;\r")  # Enter right after a cut-off sequence
    assert s.editor_lines[:3] == ["", "START", "second"] and s.pos[:2] == (2, 5)


# ---------------------------------------------------------------- UTF-8, C1 controls, surrogates

@pytest.mark.parametrize("seq, lines, pos", [
    pytest.param(b"\x80\x1b[B", ["START", "second"], (2, 4, 1, 0), id="stray continuation, then Down"),
    pytest.param(b"\xe2\x1b[B", ["START", "second"], (2, 4, 1, 0), id="cut 3-byte character, then Down"),
    pytest.param(b"\xc3x", ["xSTART", "second"], (1, 4, 2, 0), id="cut 2-byte character, then x"),
    pytest.param(b"\xf0\x9f\x91x", ["xSTART", "second"], (1, 4, 2, 0), id="cut 4-byte character, then x"),
])
def test_broken_utf8_does_not_swallow_the_next_key(ltt, ws, seq, lines, pos):  # V11
    app = open_file(ltt, ws)
    s = app.press(seq)
    assert s.editor_lines[:2] == lines and s.pos == pos


def test_alt_with_a_non_ascii_letter_types_nothing(ltt, ws):  # V11: Alt+ö typed "�ö"
    app = open_file(ltt, ws)
    s = then_mark(app, b"\x1b\xc3\xb6\x1b\xc3\xb6")
    assert s.editor_lines[0] == "|START"


@pytest.mark.parametrize("seq", [
    pytest.param(b"\xff\xfe", id="never UTF-8"),
    pytest.param(b"\xc0\xaf", id="overlong 2-byte"),
    pytest.param(b"\xe0\x80\xaf", id="overlong 3-byte"),
    pytest.param(b"\xf4\x90\x80\x80", id="above U+10FFFF"),
    pytest.param(b"\xf8\x88\x80\x80\x80", id="5-byte form"),
])
def test_invalid_utf8_types_nothing(ltt, ws, seq):  # V11
    app = open_file(ltt, ws)
    s = then_mark(app, seq)
    assert s.editor_lines[0] == "|START" and s.pos == (1, 4, 2, 0)


@pytest.mark.parametrize("seq", [
    pytest.param("\u0085".encode(), id="NEL"),
    pytest.param("\u009b".encode(), id="CSI"),
    pytest.param("\u009d".encode(), id="OSC"),
    pytest.param("\u0090".encode(), id="DCS"),
    pytest.param(kitty(97, "", "155"), id="kitty text CSI"),
    pytest.param(kitty(0, "", "133"), id="kitty text NEL"),
])
def test_c1_controls_are_never_typed(ltt, ws, seq):  # V11
    app = open_file(ltt, ws)
    s = then_mark(app, seq)
    assert s.editor_lines[0] == "|START" and s.pos == (1, 4, 2, 0)


def test_lone_surrogates_are_never_typed(ltt, ws):  # V11
    app = open_file(ltt, ws)
    then_mark(app, b"\xed\xa0\x80" + b"\xed\xbf\xbf"  # surrogates encoded as UTF-8
              + kitty(55296) + kitty(57343)          # ... as kitty key codes
              + kitty(0, "", "56320"))              # ... as kitty text
    app.press("ctrl+s")
    app.wait_status("Saved: a.txt")
    assert ws.read_bytes("a.txt") == ("|" + TEXT).encode()


def test_long_kitty_text_event_is_typed_whole(ltt, ws):  # T8: a CSI over 32 characters had its tail typed
    app = open_file(ltt, ws)
    then_mark(app, kitty(0, "", "128104:8205:128105:8205:128103"))  # man, ZWJ, woman, ZWJ, girl
    app.press("ctrl+s")
    app.wait_status("Saved: a.txt")
    assert ws.read("a.txt") == "\U0001F468\u200d\U0001F469\u200d\U0001F467|" + TEXT


def test_control_characters_in_kitty_text_are_not_typed(ltt, ws):
    app = open_file(ltt, ws)
    s = then_mark(app, kitty(97, "1", "27:7:8"))  # the A key with ESC, BEL, BS as its text
    assert s.editor_lines[0] == "a|START"


def test_multibyte_character_split_across_reads_is_typed_whole(ltt, ws):
    app = open_file(ltt, ws)
    app.write("ф".encode()[:1])
    time.sleep(0.01)  # on purpose: the second byte arrives a moment later, within ltt's 50 ms
    app.write("ф".encode()[1:])
    s = app.type("|")
    assert s.editor_lines[0] == "ф|START"


# ---------------------------------------------------------------- control bytes, lone modifiers (T4)

@pytest.mark.parametrize("byte", [b"\x00", b"\x1c", b"\x1d", b"\x1e", b"\x1f"],
                         ids=["Ctrl+Space", "Ctrl+Backslash", "Ctrl+]", "Ctrl+^", "Ctrl+_"])
def test_stray_control_bytes_are_ignored(ltt, ws, byte):
    app = open_file(ltt, ws)
    s = then_mark(app, byte)
    assert s.editor_lines[:2] == ["|START", "second"] and s.mode == "EDITOR" and app.alive


@pytest.mark.parametrize("seq", [
    pytest.param(b"\x00", id="Ctrl+Space"),
    pytest.param(b"\x80", id="stray UTF-8 byte"),
    pytest.param(b"\x1b[57441;2u", id="kitty Shift press"),
    pytest.param(b"\x1b[57441;1:3u", id="kitty Shift release"),
    pytest.param(b"\x1b[57444;9u", id="kitty Cmd press"),
])
def test_input_that_means_nothing_does_not_stop_the_screen_updates(ltt, ws, seq):  # T4
    app = open_file(ltt, ws, "old text\n")
    app.write(seq)
    ws.write("a.txt", "new text from outside\n")  # no key after it: only the main loop's own tick can show this
    s = app.wait(lambda s: s.editor_lines[0] == "new text from outside", what="the outside change")
    assert s.status.startswith("Reloaded a.txt")


def test_lone_modifier_press_does_not_stop_a_resize(ltt, ws):  # T4
    app = open_file(ltt, ws)
    app.write(b"\x1b[57442;5u")  # a lone Ctrl
    s = app.resize(80, 24)
    assert s.left_w == 25 and s.editor_lines[0] == "START"


# ---------------------------------------------------------------- keypad (T10) and function keys

def test_application_keypad_types_digits_and_operators(ltt, ws):  # T10
    app = open_file(ltt, ws)
    s = app.press(b"".join(b"\x1bO" + bytes([c]) for c in b"pqrstuvwxyjkmnoX"))
    assert s.editor_lines[0] == "0123456789*+-./=START"


def test_application_keypad_enter_breaks_the_line(ltt, ws):  # T10
    app = open_file(ltt, ws)
    s = app.press(b"\x1bOM")
    assert s.editor_lines[:2] == ["", "START"] and s.pos[:2] == (2, 5)


def test_application_cursor_keys_move(ltt, ws):
    # The runtime turns on application cursor keys: arrows, Home and End arrive as ESC O x.
    app = open_file(ltt, ws)
    assert app.press(b"\x1bOB").pos[:3] == (2, 4, 1)
    assert app.press(b"\x1bOF").pos[:3] == (2, 4, 7)
    assert app.press(b"\x1bOD").pos[:3] == (2, 4, 6)
    assert app.press(b"\x1bOH").pos[:3] == (2, 4, 1)
    assert app.press(b"\x1bOC").pos[:3] == (2, 4, 2)
    assert app.press(b"\x1bOA").pos[:3] == (1, 4, 2)
    assert app.snap().editor_lines[:3] == ["START", "second", "third"]


def test_kitty_keypad_digits_type_their_text(ltt, ws):  # T10
    app = open_file(ltt, ws)
    s = app.press(kitty(57400, "129", "49") + kitty(57399, "129", "48") + kitty(57409, "129", "46")
                  + kitty(57411, "129", "42"))
    assert s.editor_lines[0] == "10.*START"


def test_kitty_keypad_navigation_keys_move(ltt, ws):  # T10
    app = open_file(ltt, ws)
    assert app.press(kitty(57420)).pos[:2] == (2, 4)    # KP_DOWN
    assert app.press(kitty(57424)).pos[:3] == (2, 4, 7)  # KP_END
    assert app.press(kitty(57417)).pos[:3] == (2, 4, 6)  # KP_LEFT
    assert app.press(kitty(57423)).pos[:3] == (2, 4, 1)  # KP_HOME
    assert app.press(kitty(57418)).pos[:3] == (2, 4, 2)  # KP_RIGHT
    assert app.press(kitty(57419)).pos[:3] == (1, 4, 2)  # KP_UP
    assert app.snap().editor_lines[:3] == ["START", "second", "third"]


def test_kitty_keypad_page_keys_scroll(ltt, ws):  # T10
    app = open_file(ltt, ws, "".join(f"{i}\n" for i in range(1, 100)))
    s = app.press(kitty(57422))  # KP_PAGE_DOWN
    assert s.pos[0] > 20 and s.editor_lines[0] != "1"
    s = app.press(kitty(57421))  # KP_PAGE_UP
    assert s.pos[0] == 1 and s.editor_lines[0] == "1"


def test_kitty_keypad_enter_and_delete_edit(ltt, ws):  # T10
    app = open_file(ltt, ws)
    s = app.press(kitty(57426))  # KP_DELETE
    assert s.editor_lines[0] == "TART"
    s = app.press(kitty(57414))  # KP_ENTER
    assert s.editor_lines[:2] == ["", "TART"]


FUNCTION_KEYS = {
    "F1": b"\x1bOP", "F2": b"\x1bOQ", "F3": b"\x1bOR", "F4": b"\x1bOS", "F1 (CSI)": b"\x1b[11~",
    "F5": b"\x1b[15~", "F6": b"\x1b[17~", "F7": b"\x1b[18~", "F8": b"\x1b[19~", "F9": b"\x1b[20~",
    "F10": b"\x1b[21~", "F11": b"\x1b[23~", "F12": b"\x1b[24~", "Shift+F1": b"\x1b[1;2P",
    "Ctrl+F5": b"\x1b[15;5~", "Alt+F12": b"\x1b[24;3~", "kitty F1": b"\x1b[P", "kitty F13": b"\x1b[57376u",
    "kitty F35": b"\x1b[57398u",
}


@pytest.mark.parametrize("name", FUNCTION_KEYS)
def test_function_keys_do_nothing_in_the_editor(ltt, ws, name):
    app = open_file(ltt, ws)
    s = then_mark(app, FUNCTION_KEYS[name])
    assert s.editor_lines[:2] == ["|START", "second"] and s.pos == (1, 4, 2, 0)
    assert s.mode == "EDITOR" and s.tab_names == ["a.txt"] and app.alive


@pytest.mark.parametrize("seq", [
    pytest.param(b"\x1b[[A", id="linux F1"), pytest.param(b"\x1b[[E", id="linux F5"),
    pytest.param(b"\x1bO2P", id="SS3 Shift+F1"), pytest.param(b"\x1bO1;2P", id="SS3 1;2 Shift+F1"),
    pytest.param(b"\x1bO5R", id="SS3 Ctrl+F3"),
])
def test_function_keys_of_other_terminals_type_nothing(ltt, ws, seq):  # dte's test_linux_parse_key, Kakoune's parse_ss3
    app = open_file(ltt, ws)
    s = then_mark(app, seq)
    assert s.editor_lines[:2] == ["|START", "second"] and s.pos == (1, 4, 2, 0)


def test_function_keys_do_nothing_in_the_tree(ltt, ws):
    ws.write("proj/a.txt", "alpha\n")
    ws.write("proj/sub/b.txt", "beta\n")
    app = ltt(ws.path("proj"))
    before = app.press("down")
    s = app.press(b"".join(FUNCTION_KEYS.values()))
    assert s.tree_names == before.tree_names and s.tree_selected.name == before.tree_selected.name
    assert s.mode == "TREE" and s.tabs == [] and app.alive


# ---------------------------------------------------------------- keys as other terminals send them
# Ideas from dte's terminal tests, Kakoune's terminal_ui.cc and Neovim's tui_spec.

def effect(ltt, ws, key):
    """What a key does at line 2, column 3 of a.txt, right after typing X there (b.txt is the next tab):
    the lines, the cursor, the active tab, the mode, the status, the file on disk. The session is killed after."""
    ws.write("a.txt", TEXT)
    ws.write("b.txt", "other\n")
    app = ltt(ws.path("a.txt"), ws.path("b.txt"))
    app.press("down", "right", "right")
    app.type("X")
    if isinstance(key, bytes):
        app.write(key)
        app.settle(frame=False)
    else:
        app.press(key)
    s = app.snap()
    seen = (s.editor_lines[:3], s.pos, s.active_tab.name, s.mode, s.status, ws.read("a.txt"))
    app.signal(signal.SIGKILL)  # no backup of the dirty tab for the next session to restore
    app.wait_exit()
    return seen


OTHER_TERMINALS = {
    "rxvt Shift+Up": (b"\x1b[a", "shift+up"), "rxvt Shift+Down": (b"\x1b[b", "shift+down"),
    "rxvt Shift+Right": (b"\x1b[c", "shift+right"), "rxvt Shift+Left": (b"\x1b[d", "shift+left"),
    "rxvt Ctrl+Up": (b"\x1bOa", "ctrl+up"), "rxvt Ctrl+Down": (b"\x1bOb", "ctrl+down"),
    "rxvt Ctrl+Right": (b"\x1bOc", "ctrl+right"), "rxvt Ctrl+Left": (b"\x1bOd", "ctrl+left"),
    "rxvt Shift+Home": (b"\x1b[7$", "shift+home"), "rxvt Shift+End": (b"\x1b[8$", "shift+end"),
    "rxvt Ctrl+PgUp": (b"\x1b[5^", "ctrl+pgup"), "rxvt Ctrl+PgDn": (b"\x1b[6^", "ctrl+pgdn"),
    "SS3 Ctrl+Left": (b"\x1bO5D", "ctrl+left"), "SS3 Shift+Right": (b"\x1bO2C", "shift+right"),
    "modifyOtherKeys Ctrl+S": (b"\x1b[27;5;115~", "ctrl+s"), "modifyOtherKeys Ctrl+Z": (b"\x1b[27;5;122~", "ctrl+z"),
    "modifyOtherKeys Ctrl+Shift+Z": (b"\x1b[27;6;90~", kitty(122, "6")),
}


@pytest.mark.parametrize("name", OTHER_TERMINALS)
def test_keys_of_other_terminals_do_what_the_key_does(ltt, ws, name):
    seq, same_as = OTHER_TERMINALS[name]
    assert effect(ltt, ws, seq) == effect(ltt, ws, same_as)


def test_a_key_right_after_an_rxvt_shift_home_counts(ltt, ws):  # dte's rxvt table
    app = open_file(ltt, ws)
    s = then_mark(app, b"\x1b[7$")
    assert s.editor_lines[0].startswith("|")


@pytest.mark.parametrize("seq", [kitty(104, "17"), kitty(107, "33"), kitty(104, "17", "104")],
                         ids=["hyper+h", "meta+k", "hyper+h with text"])
def test_kitty_hyper_and_meta_with_a_letter_type_nothing(ltt, ws, seq):  # Neovim's tui_spec, dte's sweep
    app = open_file(ltt, ws)
    s = then_mark(app, seq)
    assert s.editor_lines[0] == "|START"


def test_kitty_keypad_digit_without_text_types_the_digit(ltt, ws):  # Neovim's tui_spec
    app = open_file(ltt, ws)
    s = then_mark(app, kitty(57399))  # KP_0, as a terminal that leaves the text out sends it
    assert s.editor_lines[0] == "0|START"


@pytest.mark.parametrize("seq, typed", [(b"\x1bOl", ","), (b"\x1bO ", " ")], ids=["comma", "space"])
def test_application_keypad_comma_and_space_type_themselves(ltt, ws, seq, typed):  # dte's keypad table
    app = open_file(ltt, ws)
    s = then_mark(app, seq)
    assert s.editor_lines[0] == typed + "|START"


@pytest.mark.parametrize("after", ["wheel", "focus"])
def test_esc_followed_at_once_by_a_mouse_report_still_acts(ltt, ws, after):  # Neovim's #41763
    app = open_file(ltt, ws, text="".join(f"line {i}\n" for i in range(1, 200)))
    s0 = app.snap()
    tail = K.mouse(K.WHEEL_DOWN, s0.text_x + 5, s0.editor_rows[3].y, True) if after == "wheel" else b"\x1b[I"
    app.write(b"\x1b" + tail)  # Esc while a trackpad still scrolls
    s = app.wait(lambda s: s.mode == "TREE", what="the Esc", timeout=3)
    if after == "wheel":
        assert s.editor_rows[0].number > s0.editor_rows[0].number


def test_a_huge_terminal_reply_is_not_typed(ltt, ws):  # Neovim's 'does not crash on large inputs'
    app = open_file(ltt, ws)
    s = then_mark(app, b"\x1b]52;c;" + b"QUFB" * 20_000 + b"\x1b\\")
    assert s.editor_lines[0] == "|START"
