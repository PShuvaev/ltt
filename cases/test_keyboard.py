"""The keyboard: the kitty protocol as ltt asks for it (flags 1|4|8|16 — every key as CSI u, alternate
keys, the text a key types), legacy sequences, keyboard layouts, Cmd, Alt/Option combinations."""
import pytest

from harness import keys as K
from keys_helpers import edit, pos, select

ENCODINGS = ["legacy", "kitty"]


def ktext(text):
    """Text typed key by key in the kitty protocol: each character as CSI u with its text."""
    return b"".join(K.kitty_key(c) for c in text)


def type_in(app, keyboard, text):
    if keyboard == "kitty":
        app.press(ktext(text))
    else:
        app.type(text)


def save(app, name="a.txt"):
    app.press("ctrl+s")
    return app.wait_status(f"Saved: {name}")


def line(app, n=1):
    return app.snap().editor_lines[n - 1]


def release(code, mods=0):
    """A kitty key-release event (ltt does not ask for them; they must do nothing)."""
    return K.kitty(code, mods, event=3)


def repeat(code, mods=0, text=None):
    return K.kitty(code, mods, event=2, text=text)


def test_kitty_flags_are_pushed_at_start_and_popped_at_quit(ltt, ws):
    ws.write("proj/a.txt", "a\n")
    app = ltt(ws.path("proj"))
    assert b"\x1b[>29u" in app.output  # 1|4|8|16: disambiguate, alternate keys, all keys as CSI u, text
    assert app.quit() == 0
    out = app.output
    assert out.rfind(b"\x1b[<u") > out.rfind(b"\x1b[?2026l")  # popped after the last frame


# ---------------------------------------------------------------- text, in both encodings

TEXTS = {"cyrillic": "привет мир", "accents": "naïve café", "wide": "日本語 テキスト", "emoji": "👀 🚀",
         "shifted": "Hello World! #42"}


@pytest.mark.parametrize("keyboard", ENCODINGS)
@pytest.mark.parametrize("text", TEXTS.values(), ids=TEXTS.keys())
def test_text_is_typed_and_saved_as_it_is(ltt, ws, keyboard, text):
    app = edit(ltt, ws, "\n", keyboard=keyboard)
    type_in(app, keyboard, text)
    assert line(app) == text
    save(app)
    assert ws.read("proj/a.txt") == text + "\n"


def test_kitty_letter_with_shift_but_no_text_types_the_shifted_key(ltt, ws):
    app = edit(ltt, ws, "x\n", keyboard="kitty")
    app.press(K.kitty(97, K.SHIFT, shifted=65) + K.kitty(49, K.SHIFT, shifted=33))
    assert line(app) == "A!x"


def test_kitty_non_latin_key_without_text_types_its_letter(ltt, ws):
    app = edit(ltt, ws, "x\n", keyboard="kitty")
    app.press(K.kitty(1092, base=97) + K.kitty(1092, K.SHIFT, shifted=1060, base=97))  # ф, Ф on the A key
    assert line(app) == "фФx"


def test_kitty_text_with_an_empty_modifier_field_is_typed(ltt, ws):
    app = edit(ltt, ws, "x\n", keyboard="kitty")
    app.press(b"\x1b[1092::97;;1092u" + b"\x1b[98;;98u")
    assert line(app) == "фbx"


def test_kitty_caps_lock_letter_types_its_text(ltt, ws):
    app = edit(ltt, ws, "x\n", keyboard="kitty")
    app.press(K.kitty(97, 64, text="A"))  # Caps Lock on: the A key types A
    assert line(app) == "Ax"


@pytest.mark.parametrize("lock", [64, 128, 192], ids=["caps-lock", "num-lock", "both"])
def test_kitty_lock_keys_do_not_break_shortcuts(ltt, ws, lock):
    app = edit(ltt, ws, "abc\n", keyboard="kitty")
    app.press(ktext("X"))
    app.press(K.kitty(115, K.CTRL | lock))  # Ctrl+S
    app.wait_status("Saved: a.txt")
    app.press(K.kitty(122, K.CTRL | lock))  # Ctrl+Z
    s = app.wait_status("Undone.")
    assert s.editor_lines[0] == "abc"


def test_kitty_long_text_event_is_typed_whole(ltt, ws):  # T8: a CSI over 32 characters was cut and its tail typed
    app = edit(ltt, ws, "\n", keyboard="kitty")
    family = "\U0001F468\u200d\U0001F469\u200d\U0001F467"  # man, ZWJ, woman, ZWJ, girl
    app.press(b"\x1b[0;;128104:8205:128105:8205:128103u")
    save(app)
    assert ws.read("proj/a.txt") == family + "\n"


def test_kitty_space_types_a_space(ltt, ws):
    app = edit(ltt, ws, "ab\n", keyboard="kitty")
    app.press("right", K.kitty(32, text=" "))
    assert line(app) == "a b"


@pytest.mark.parametrize("keyboard", ENCODINGS)
def test_alt_letter_types_nothing(ltt, ws, keyboard):
    app = edit(ltt, ws, "x\n", keyboard=keyboard)
    app.press("alt+q")
    s = app.snap()
    assert s.editor_lines[0] == "x" and not s.title_dirty


def test_legacy_alt_non_latin_letter_types_nothing(ltt, ws):  # V11: Alt+ö typed "�ö"
    app = edit(ltt, ws, "x\n")
    app.press("\x1bö".encode(), "\x1bи".encode())
    s = app.snap()
    assert s.editor_lines[0] == "x" and not s.title_dirty


# ---------------------------------------------------------------- kitty event types: repeats act, releases do not

def test_kitty_key_repeat_types_again(ltt, ws):
    app = edit(ltt, ws, "x\n", keyboard="kitty")
    app.press(K.kitty_key("a") + repeat(97, text="a") + repeat(97, text="a"))
    assert line(app) == "aaax"


def test_kitty_key_release_types_nothing(ltt, ws):
    app = edit(ltt, ws, "x\n", keyboard="kitty")
    app.press(K.kitty_key("a") + release(97) + K.kitty_key("b") + release(98))
    assert line(app) == "abx"


RELEASES = {"enter": release(13), "backspace": release(127), "tab": release(9), "esc": release(27),
            "delete": b"\x1b[3;1:3~", "ctrl+z": release(122, K.CTRL), "ctrl+w": release(119, K.CTRL),
            "left": b"\x1b[1;1:3D"}
@pytest.mark.parametrize("data", [pytest.param(v, id=k) for k, v in RELEASES.items()])
def test_kitty_key_release_does_nothing(ltt, ws, data):
    app = edit(ltt, ws, "abc\n", keyboard="kitty")
    app.press("end", ktext("d"))
    app.press(data + K.kitty_key("e"))
    s = app.snap()
    assert (s.mode, s.editor_lines[:2], s.tab_names, s.pos[:3]) == ("EDITOR", ["abcde", ""], ["a.txt"], (1, 2, 6))


def test_kitty_repeat_of_enter_and_backspace_acts(ltt, ws):
    app = edit(ltt, ws, "abc\n", keyboard="kitty")
    app.press("end", repeat(127), repeat(127), repeat(13))
    assert app.snap().editor_lines[:3] == ["a", "", ""]


# ---------------------------------------------------------------- named keys as CSI u (kitty)

def test_kitty_esc_goes_back_to_the_tree(ltt, ws):
    app = edit(ltt, ws, keyboard="kitty")
    app.press("esc")  # CSI 27 u
    assert app.snap().mode == "TREE"


def test_kitty_esc_cancels_a_prompt(ltt, ws):
    app = edit(ltt, ws, keyboard="kitty")
    app.press("ctrl+l")
    app.wait_mode("PROMPT")
    app.press("esc")
    s = app.wait_status("Cancelled.")
    assert s.mode == "EDITOR"


def test_kitty_enter_breaks_the_line_and_keeps_the_indent(ltt, ws):
    app = edit(ltt, ws, "    indented\n", keyboard="kitty")
    app.press("end", "enter")  # CSI 13 u
    app.press(ktext("x"))
    assert app.snap().editor_lines[:2] == ["    indented", "    x"]


def test_kitty_tab_goes_from_the_tree_to_the_editor(ltt, ws):
    app = edit(ltt, ws, keyboard="kitty")
    app.press("esc")
    app.press("tab")  # CSI 9 u
    s = app.wait_status("Editing. Esc returns to the tree.")
    assert s.mode == "EDITOR"


def test_kitty_tab_indents_in_the_editor(ltt, ws):
    app = edit(ltt, ws, "x\n", keyboard="kitty")
    app.press("tab")
    assert line(app) == "    x"


def test_kitty_backspace_deletes_a_character(ltt, ws):
    app = edit(ltt, ws, "abc\n", keyboard="kitty")
    app.press("end", "backspace")  # CSI 127 u
    assert line(app) == "ab"


def test_kitty_keypad_enter_breaks_the_line(ltt, ws):
    app = edit(ltt, ws, "ab\n", keyboard="kitty")
    app.press("right", K.kitty(57414))
    assert app.snap().editor_lines[:2] == ["a", "b"]


def test_kitty_keypad_digits_and_navigation(ltt, ws):  # T10: the keypad did nothing
    app = edit(ltt, ws, "abc\n", keyboard="kitty")
    app.press(K.kitty(57424))  # keypad End (Num Lock off)
    app.press(K.kitty(57400, 128, text="1") + K.kitty(57401, 128, text="2"))  # keypad 1 2 (Num Lock on)
    app.press(K.kitty(57417), K.kitty(57417))  # keypad ← ←
    app.press(K.kitty(57399, 128, text="0"))
    assert line(app) == "abc012"


def test_kitty_keypad_key_without_text_never_types_a_private_use_character(ltt, ws):
    app = edit(ltt, ws, "x\n", keyboard="kitty")
    app.press(K.kitty(57400) + K.kitty(57413) + K.kitty(57376) + K.kitty_key("y"))  # keypad 1, keypad +, F13
    s = app.snap()
    assert s.editor_lines[0] == "1+yx"  # the keypad's own characters, never its private-use key codes


def test_application_keypad_types_digits_and_operators(ltt, ws):  # T10
    app = edit(ltt, ws, "\n")
    app.press(b"\x1bOq\x1bOk\x1bOr\x1bOX\x1bOs" + b"\x1bOm\x1bOj\x1bOo\x1bOn\x1bOp")  # keypad 1+2=3 -*/.0
    assert line(app) == "1+2=3-*/.0"


def test_application_keypad_enter_breaks_the_line(ltt, ws):  # T10
    app = edit(ltt, ws, "ab\n")
    app.press("right", b"\x1bOM")
    assert app.snap().editor_lines[:2] == ["a", "b"]


@pytest.mark.parametrize("data", [b"\x1b[57441;2u", b"\x1b[57442;5u", b"\x1b[57443;3u", b"\x1b[57444;9u",
                                  b"\x1b[57358u"], ids=["shift", "ctrl", "alt", "super", "caps-lock"])
def test_kitty_modifier_key_alone_types_nothing(ltt, ws, data):
    app = edit(ltt, ws, "x\n", keyboard="kitty")
    app.press(data + K.kitty_key("A"))
    s = app.snap()
    assert s.editor_lines[0] == "Ax" and s.mode == "EDITOR"


# ---------------------------------------------------------------- the same action in legacy and kitty encodings

@pytest.mark.parametrize("keyboard", ENCODINGS)
def test_enter_tab_backspace(ltt, ws, keyboard):
    app = edit(ltt, ws, "abc\n", keyboard=keyboard)
    app.press("right", "enter", "tab", "end", "backspace")
    assert app.snap().editor_lines[:2] == ["a", "    b"]


@pytest.mark.parametrize("keyboard", ENCODINGS)
def test_esc_goes_to_the_tree_and_tab_comes_back(ltt, ws, keyboard):
    app = edit(ltt, ws, keyboard=keyboard)
    app.press("esc")
    assert app.snap().mode == "TREE"
    app.press("tab")
    assert app.snap().mode == "EDITOR"


@pytest.mark.parametrize("keyboard", ENCODINGS)
def test_ctrl_s_saves(ltt, ws, keyboard):
    app = edit(ltt, ws, "abc\n", keyboard=keyboard)
    type_in(app, keyboard, "X")
    s = save(app)
    assert not s.title_dirty and ws.read("proj/a.txt") == "Xabc\n"


@pytest.mark.parametrize("keyboard", ENCODINGS)
def test_ctrl_z_undoes_and_ctrl_y_redoes(ltt, ws, keyboard):
    app = edit(ltt, ws, "abc\n", keyboard=keyboard)
    type_in(app, keyboard, "X")
    app.press("ctrl+z")
    s = app.wait_status("Undone.")
    assert s.editor_lines[0] == "abc"
    app.press("ctrl+y")
    s = app.wait_status("Redone.")
    assert s.editor_lines[0] == "Xabc"


@pytest.mark.parametrize("keyboard", ENCODINGS)
def test_ctrl_shift_z_redoes(ltt, ws, keyboard):  # legacy terminals cannot send it; the harness sends CSI u there too
    app = edit(ltt, ws, "abc\n", keyboard=keyboard)
    type_in(app, keyboard, "X")
    app.press("ctrl+z", "ctrl+shift+z")
    s = app.wait_status("Redone.")
    assert s.editor_lines[0] == "Xabc"


@pytest.mark.parametrize("keyboard", ENCODINGS)
def test_ctrl_a_selects_all(ltt, ws, keyboard):
    app = edit(ltt, ws, "one\ntwo\n", keyboard=keyboard)
    app.press("ctrl+a")
    s = app.wait_status("Selected all: 8 chars.")
    assert s.pos[3] == 8


@pytest.mark.parametrize("keyboard", ENCODINGS)
def test_ctrl_c_copies_and_ctrl_x_cuts(ltt, ws, keyboard):
    app = edit(ltt, ws, "one two\n", keyboard=keyboard)
    app.press("shift+right", "shift+right", "shift+right", "ctrl+c")
    app.wait_status("Copied to the clipboard: 3 chars")
    app.wait(lambda s: ws.clipboard == "one", timeout=5, what="the clipboard holds the selection")
    app.press("ctrl+x")
    s = app.wait_status("Cut to the clipboard.")
    assert s.editor_lines[0] == " two"


@pytest.mark.parametrize("keyboard", ENCODINGS)
def test_ctrl_v_pastes_the_clipboard(ltt, ws, keyboard):
    ws.set_clipboard("pasted ")
    app = edit(ltt, ws, "abc\n", keyboard=keyboard)
    app.press("ctrl+v")
    s = app.wait_status("Pasted: 7 chars")
    assert s.editor_lines[0] == "pasted abc"


@pytest.mark.parametrize("keyboard", ENCODINGS)
def test_ctrl_c_without_a_selection_never_quits(ltt, ws, keyboard):
    app = edit(ltt, ws, keyboard=keyboard)
    app.press("ctrl+c")
    app.wait_status("Ctrl+C copies a selection. Ctrl+Q quits.")
    assert app.alive


@pytest.mark.parametrize("keyboard", ENCODINGS)
@pytest.mark.parametrize("key, prompt", [("ctrl+f", "Search file:"), ("ctrl+l", "Go to:"), ("ctrl+n", "New file:"),
                                         ("ctrl+shift+n", "New folder:"), ("ctrl+r", "Rename: a.txt"),
                                         ("ctrl+o", "Add folder:")])
def test_ctrl_key_opens_its_prompt(ltt, ws, keyboard, key, prompt):
    app = edit(ltt, ws, keyboard=keyboard)
    app.press(key)
    s = app.wait(lambda s: s.prompt is not None and s.prompt.startswith(prompt), what=f"the prompt {prompt!r}")
    assert s.mode == "PROMPT"


@pytest.mark.parametrize("keyboard", ENCODINGS)
def test_prompt_takes_typed_text_and_enter(ltt, ws, keyboard):
    app = edit(ltt, ws, "1\n2\n3\n4\n", keyboard=keyboard)
    app.press("ctrl+l")
    type_in(app, keyboard, "3")
    app.press("enter")
    s = app.wait(lambda s: s.mode == "EDITOR" and s.pos[0] == 3, what="line 3")
    assert s.status == "Line 3 of 5."


@pytest.mark.parametrize("keyboard", ENCODINGS)
def test_search_input_takes_typed_text(ltt, ws, keyboard):
    app = edit(ltt, ws, "alpha\nbeta\ngamma\n", keyboard=keyboard)
    app.press("ctrl+f")
    type_in(app, keyboard, "gam")
    s = app.wait(lambda s: s.results and s.results[0].snippet == "gamma", what="the hit")
    assert s.prompt.startswith("Search file: gam")


@pytest.mark.parametrize("keyboard", ENCODINGS)
def test_ctrl_g_and_ctrl_shift_g_step_through_hits(ltt, ws, keyboard):
    app = edit(ltt, ws, "alpha\nbeta\nalpine\ngamma\nalps\n", keyboard=keyboard)
    app.press("ctrl+f")
    type_in(app, keyboard, "alp")
    app.wait(lambda s: len(s.results) == 3, what="three hits")
    app.press("enter")
    assert app.wait_mode("EDITOR").pos[0] == 1
    app.press("ctrl+g")
    assert pos(app) == (3, 1)
    app.press("ctrl+g")
    assert pos(app) == (5, 1)
    app.press("ctrl+shift+g")
    assert pos(app) == (3, 1)


@pytest.mark.parametrize("keyboard", ENCODINGS)
def test_ctrl_w_closes_the_tab(ltt, ws, keyboard):
    app = edit(ltt, ws, keyboard=keyboard)
    app.press("ctrl+w")
    s = app.wait_status('Closed tab "a.txt".')
    assert s.tabs == [] and s.mode == "TREE"


@pytest.mark.parametrize("keyboard", ENCODINGS)
def test_answer_letter_at_a_question(ltt, ws, keyboard):
    app = edit(ltt, ws, "abc\n", keyboard=keyboard)
    type_in(app, keyboard, "X")
    app.press("ctrl+w")
    app.wait_mode("CONFIRM")
    app.press("d")
    s = app.wait_status('Closed tab "a.txt".')
    assert s.tabs == [] and ws.read("proj/a.txt") == "abc\n"


@pytest.mark.parametrize("keyboard", ENCODINGS)
def test_ctrl_q_quits(ltt, ws, keyboard):
    app = edit(ltt, ws, keyboard=keyboard)
    assert app.quit() == 0


@pytest.mark.parametrize("keyboard", ENCODINGS)
def test_ctrl_pgdn_and_pgup_cycle_tabs(ltt, ws, keyboard):
    app = two_tabs(ltt, ws, keyboard)
    app.press("ctrl+pgdn")
    assert app.snap().active_tab.name == "b.txt"
    app.press("ctrl+pgup")
    assert app.snap().active_tab.name == "a.txt"


@pytest.mark.parametrize("keyboard", ENCODINGS)
def test_shift_arrows_select(ltt, ws, keyboard):
    app = edit(ltt, ws, "one\ntwo\n", keyboard=keyboard)
    app.press("shift+right", "shift+down")
    assert app.snap().pos == (2, 3, 2, 5)


@pytest.mark.parametrize("keyboard", ENCODINGS)
def test_shift_home_and_shift_end_select(ltt, ws, keyboard):
    app = edit(ltt, ws, "one two\n", keyboard=keyboard)
    app.press("right", "right", "shift+end")
    assert app.snap().pos == (1, 2, 8, 5)
    app.press("shift+home")
    assert app.snap().pos == (1, 2, 1, 2)


@pytest.mark.parametrize("keyboard", ENCODINGS)
def test_ctrl_home_and_ctrl_end(ltt, ws, keyboard):
    app = edit(ltt, ws, "one\ntwo\nthree\n", keyboard=keyboard)
    app.press("ctrl+end")
    assert pos(app) == (4, 1)
    app.press("ctrl+home")
    assert pos(app) == (1, 1)


# ---------------------------------------------------------------- keyboard layouts (T3): the letter, not the US position

def test_azerty_ctrl_a_selects_all_instead_of_quitting(ltt, ws):  # T3: the A key sits on the US Q
    app = edit(ltt, ws, "some text\n", keyboard="kitty")
    app.press(K.kitty_key("ctrl+a", base="q"))
    s = app.wait_status("Selected all")
    assert app.alive and s.pos[3] == len("some text\n")


def test_azerty_ctrl_z_undoes_instead_of_closing_the_tab(ltt, ws):  # T3: the Z key sits on the US W
    app = edit(ltt, ws, "abc\n", keyboard="kitty")
    app.press(ktext("X"))
    app.press(K.kitty_key("ctrl+z", base="w"))
    s = app.wait_status("Undone.")
    assert s.tab_names == ["a.txt"] and s.editor_lines[0] == "abc"


def test_azerty_ctrl_w_closes_the_tab(ltt, ws):
    app = edit(ltt, ws, "abc\n", keyboard="kitty")
    app.press(K.kitty_key("ctrl+w", base="z"))
    s = app.wait_status('Closed tab "a.txt".')
    assert s.tabs == []


def test_azerty_ctrl_q_quits(ltt, ws):
    app = edit(ltt, ws, "abc\n", keyboard="kitty")
    app.press(K.kitty_key("ctrl+q", base="a"), settle=False)
    assert app.wait_exit() == 0


def test_qwertz_ctrl_z_undoes_instead_of_redoing(ltt, ws):  # T3: the Z key sits on the US Y
    app = edit(ltt, ws, "abc\n", keyboard="kitty")
    app.press(ktext("X"))
    app.press(K.kitty_key("ctrl+z", base="y"))
    s = app.wait_status("Undone.")
    assert s.editor_lines[0] == "abc"


def test_qwertz_ctrl_y_redoes(ltt, ws):
    app = edit(ltt, ws, "abc\n", keyboard="kitty")
    app.press(ktext("X"), "ctrl+z")
    app.press(K.kitty_key("ctrl+y", base="z"))
    s = app.wait_status("Redone.")
    assert s.editor_lines[0] == "Xabc"


def ask_to_delete(ltt, ws):
    ws.write("proj/a.txt", "a\n")
    ws.write("proj/b.txt", "bb\n")
    app = ltt(ws.path("proj"), keyboard="kitty")
    select(app, "a.txt")
    app.press("ctrl+delete")
    app.wait(lambda s: s.question is not None and "Delete file" in s.question, what="the delete question")
    return app


def test_qwertz_z_does_not_answer_the_delete_question(ltt, ws):  # T3: z on the US Y position deleted the file
    app = ask_to_delete(ltt, ws)
    app.press(K.kitty_key("z", base="y"))
    s = app.snap()
    assert s.mode == "CONFIRM" and ws.exists("proj/a.txt")


def test_qwertz_y_answers_the_delete_question(ltt, ws):
    app = ask_to_delete(ltt, ws)
    app.press(K.kitty_key("y", base="z"))
    app.wait_tree("a.txt", present=False)
    assert not ws.exists("proj/a.txt")


def test_dvorak_ctrl_s_saves(ltt, ws):  # Dvorak S sits on the US ;
    app = edit(ltt, ws, "abc\n", keyboard="kitty")
    app.press(ktext("X"))
    app.press(K.kitty(ord("s"), K.CTRL, base=ord(";")))
    save_s = app.wait_status("Saved: a.txt")
    assert not save_s.title_dirty


def test_cyrillic_ctrl_f_searches_by_the_physical_key(ltt, ws):
    app = edit(ltt, ws, keyboard="kitty")
    app.press(K.kitty_key("ctrl+а", base="f"))
    app.wait(lambda s: s.prompt is not None and s.prompt.startswith("Search file:"), what="the search prompt")


def test_cyrillic_ctrl_s_saves_by_the_physical_key(ltt, ws):
    app = edit(ltt, ws, "abc\n", keyboard="kitty")
    app.press(ktext("X"))
    app.press(K.kitty_key("ctrl+ы", base="s"))
    app.wait_status("Saved: a.txt")
    assert ws.read("proj/a.txt") == "Xabc\n"


def test_cyrillic_ctrl_z_undoes_by_the_physical_key(ltt, ws):
    app = edit(ltt, ws, "abc\n", keyboard="kitty")
    app.press(ktext("X"))
    app.press(K.kitty_key("ctrl+я", base="z"))
    s = app.wait_status("Undone.")
    assert s.editor_lines[0] == "abc"


def test_cyrillic_ctrl_shift_n_opens_the_new_folder_prompt(ltt, ws):
    app = edit(ltt, ws, keyboard="kitty")
    app.press(K.kitty(ord("т"), K.CTRL | K.SHIFT, shifted=ord("Т"), base=ord("n")))
    app.wait(lambda s: s.prompt is not None and s.prompt.startswith("New folder:"), what="the new-folder prompt")


@pytest.mark.parametrize("keyboard", ENCODINGS)
def test_cyrillic_letter_answers_a_question_by_the_physical_key(ltt, ws, keyboard):
    app = edit(ltt, ws, "abc\n", keyboard=keyboard)
    type_in(app, keyboard, "X")
    app.press("ctrl+w")
    app.wait_mode("CONFIRM")
    app.press(K.kitty_key("ы", base="s") if keyboard == "kitty" else "ы".encode())  # the S key: save
    s = app.wait_status('Closed tab "a.txt".')
    assert s.tabs == [] and ws.read("proj/a.txt") == "Xabc\n"


# ---------------------------------------------------------------- Cmd works as Ctrl and never types (T5)

def test_cmd_s_saves_without_typing(ltt, ws):  # T5: Cmd+S typed "s"
    app = edit(ltt, ws, "abc\n", keyboard="kitty")
    app.press(ktext("X"))
    app.press("cmd+s")  # CSI 115;9 u
    s = app.wait_status("Saved: a.txt")
    assert s.editor_lines[0] == "Xabc" and ws.read("proj/a.txt") == "Xabc\n"


def test_cmd_with_a_text_field_still_types_nothing(ltt, ws):  # T5
    app = edit(ltt, ws, "abc\n", keyboard="kitty")
    app.press(K.kitty(115, K.SUPER, text="s"))
    s = app.wait_status("Saved: a.txt")
    assert s.editor_lines[0] == "abc"


@pytest.mark.parametrize("key, status", [("cmd+z", "Nothing to undo."), ("cmd+a", "Selected all: 4 chars."),
                                         ("cmd+w", 'Closed tab "a.txt".')])
def test_cmd_letter_works_as_ctrl(ltt, ws, key, status):
    app = edit(ltt, ws, "abc\n", keyboard="kitty")
    app.press(key)
    app.wait_status(status)


def test_cmd_unbound_letter_types_nothing(ltt, ws):
    app = edit(ltt, ws, "abc\n", keyboard="kitty")
    app.press("cmd+k", "cmd+shift+k", K.kitty(107, K.SUPER, text="k"))
    s = app.snap()
    assert s.editor_lines[0] == "abc" and not s.title_dirty


def test_cmd_left_and_right_go_to_the_line_start_and_end(ltt, ws):
    app = edit(ltt, ws, "one two\n", keyboard="kitty")
    app.press("cmd+right")
    assert pos(app) == (1, 8)
    app.press("cmd+left")
    assert pos(app) == (1, 1)


def test_cmd_up_and_down_go_to_the_document_start_and_end(ltt, ws):
    app = edit(ltt, ws, "one\ntwo\nthree\n", keyboard="kitty")
    app.press("down", "cmd+down")
    assert pos(app) == (4, 1)
    app.press("cmd+up")
    assert pos(app) == (1, 1)


def test_cmd_shift_right_selects_to_the_line_end(ltt, ws):
    app = edit(ltt, ws, "one two\n", keyboard="kitty")
    app.press("right", "cmd+shift+right")
    assert app.snap().pos == (1, 2, 8, 6)


# ---------------------------------------------------------------- Alt+1..9 and Alt+B/F by the key, not the character (V16)

def two_tabs(ltt, ws, keyboard):
    ws.write("proj/a.txt", "a\n")
    ws.write("proj/b.txt", "b\n")
    return ltt(ws.path("proj/a.txt"), ws.path("proj/b.txt"), keyboard=keyboard)


@pytest.mark.parametrize("keyboard", ENCODINGS)
def test_alt_digit_jumps_to_that_tab(ltt, ws, keyboard):
    app = two_tabs(ltt, ws, keyboard)
    app.press("alt+2")
    s = app.wait_status("Tab 2/2: b.txt")
    assert s.active_tab.name == "b.txt" and s.editor_lines[0] == "b"
    app.press("alt+1")
    assert app.wait_status("Tab 1/2: a.txt").active_tab.name == "a.txt"


@pytest.mark.parametrize("data", [K.kitty(233, K.ALT, base=50), K.kitty(233, K.ALT | K.SHIFT, shifted=50, base=50)],
                         ids=["alt", "alt-shift"])
def test_alt_digit_key_on_azerty_jumps_to_the_tab(ltt, ws, data):  # V16: AZERTY's 2 key types é
    app = two_tabs(ltt, ws, "kitty")
    app.press(data)
    assert app.wait_status("Tab 2/2: b.txt").active_tab.name == "b.txt"


def test_alt_digit_past_the_last_tab_does_nothing(ltt, ws):
    app = two_tabs(ltt, ws, "legacy")
    before = app.snap()
    app.press("alt+9")
    s = app.snap()
    assert s.active_tab.name == "a.txt" and s.status == before.status and s.editor_lines[0] == "a"


def test_alt_digit_from_the_tree_focuses_the_editor(ltt, ws):
    app = two_tabs(ltt, ws, "legacy")
    app.press("esc")
    app.press("alt+2")
    s = app.wait_status("Tab 2/2: b.txt")
    assert s.mode == "EDITOR"


@pytest.mark.parametrize("keyboard", ENCODINGS)
def test_alt_b_and_alt_f_jump_words(ltt, ws, keyboard):
    app = edit(ltt, ws, "one two three\n", keyboard=keyboard)
    app.press("end", "alt+b")
    assert pos(app) == (1, 9)
    app.press("alt+b", "alt+f")
    assert pos(app) == (1, 8)


def test_alt_b_and_alt_f_on_cyrillic_jump_words_by_the_key(ltt, ws):  # V16: и and а sit on B and F
    app = edit(ltt, ws, "one two three\n", keyboard="kitty")
    app.press("end", K.kitty(ord("и"), K.ALT, base=ord("b")))
    assert pos(app) == (1, 9)
    app.press(K.kitty(ord("и"), K.ALT, base=ord("b")), K.kitty(ord("а"), K.ALT, base=ord("f")))
    assert pos(app) == (1, 8)
    assert app.snap().editor_lines[0] == "one two three"


# ---------------------------------------------------------------- Option/Ctrl+arrows in every encoding

WORD_LEFT = {"csi-alt": b"\x1b[1;3D", "esc-b": b"\x1bb", "esc-esc-csi": b"\x1b\x1b[D", "esc-esc-ss3": b"\x1b\x1bOD",
             "csi-ctrl": b"\x1b[1;5D", "kitty-alt-b": b"\x1b[98;3u"}
WORD_RIGHT = {"csi-alt": b"\x1b[1;3C", "esc-f": b"\x1bf", "esc-esc-csi": b"\x1b\x1b[C", "esc-esc-ss3": b"\x1b\x1bOC",
              "csi-ctrl": b"\x1b[1;5C", "kitty-alt-f": b"\x1b[102;3u"}
PARAGRAPH_DOWN = {"csi-alt": b"\x1b[1;3B", "esc-esc-csi": b"\x1b\x1b[B", "esc-esc-ss3": b"\x1b\x1bOB"}
PARAGRAPH_UP = {"csi-alt": b"\x1b[1;3A", "esc-esc-csi": b"\x1b\x1b[A", "esc-esc-ss3": b"\x1b\x1bOA"}


@pytest.mark.parametrize("data", WORD_LEFT.values(), ids=WORD_LEFT.keys())
def test_word_left(ltt, ws, data):
    app = edit(ltt, ws, "one two three\n")
    app.press("end", data)
    s = app.snap()
    assert s.pos[:3] == (1, 2, 9) and s.editor_lines[0] == "one two three"


@pytest.mark.parametrize("data", WORD_RIGHT.values(), ids=WORD_RIGHT.keys())
def test_word_right(ltt, ws, data):
    app = edit(ltt, ws, "one two three\n")
    app.press(data, data)
    s = app.snap()
    assert s.pos[:3] == (1, 2, 8) and s.editor_lines[0] == "one two three"


@pytest.mark.parametrize("data", PARAGRAPH_DOWN.values(), ids=PARAGRAPH_DOWN.keys())
def test_paragraph_down(ltt, ws, data):  # T8: ESC ESC [B typed "[B"
    app = edit(ltt, ws, "a\nb\n\nc\nd\n")
    app.press(data)
    s = app.snap()
    assert s.pos[:3] == (4, 6, 1) and s.editor_lines[:5] == ["a", "b", "", "c", "d"]


@pytest.mark.parametrize("data", PARAGRAPH_UP.values(), ids=PARAGRAPH_UP.keys())
def test_paragraph_up(ltt, ws, data):
    app = edit(ltt, ws, "a\nb\n\nc\nd\n")
    app.press("ctrl+end", data)
    assert pos(app) == (4, 1)
    app.press(data)
    s = app.snap()
    assert s.pos[:3] == (1, 6, 1) and s.editor_lines[:5] == ["a", "b", "", "c", "d"]


def test_shift_option_left_selects_a_word(ltt, ws):
    app = edit(ltt, ws, "one two three\n")
    app.press("end", b"\x1b[1;4D")
    assert app.snap().pos == (1, 2, 9, 5)


def test_ctrl_shift_arrows_do_nothing(ltt, ws):
    app = edit(ltt, ws, "one two three\n")
    app.press("right", b"\x1b[1;6C", b"\x1b[1;6D", b"\x1b[1;6B")
    assert app.snap().pos == (1, 2, 2, 0)


# ---------------------------------------------------------------- Backspace with Ctrl/Option

WORD_BACKSPACE = {"kitty-ctrl": b"\x1b[127;5u", "kitty-alt": b"\x1b[127;3u", "esc-del": b"\x1b\x7f",
                  "esc-ctrl-h": b"\x1b\x08"}


@pytest.mark.parametrize("data", WORD_BACKSPACE.values(), ids=WORD_BACKSPACE.keys())
def test_word_backspace_deletes_the_word_before_the_cursor(ltt, ws, data):
    app = edit(ltt, ws, "foo bar  baz\n")
    app.press("end", data)
    assert (line(app), pos(app)) == ("foo bar", (1, 10))  # "foo bar  ": the blanks stay
    app.press(data)
    assert (line(app), pos(app)) == ("foo", (1, 5))  # then the blanks go with the word before them


def test_word_backspace_at_a_line_start_joins_the_lines(ltt, ws):
    app = edit(ltt, ws, "one\ntwo\n")
    app.press("down", b"\x1b\x7f")
    assert app.snap().editor_lines[:2] == ["onetwo", ""]


def test_ctrl_h_is_a_plain_backspace(ltt, ws):  # legacy Ctrl+Backspace sends ^H, as some terminals send for Backspace
    app = edit(ltt, ws, "foo bar\n")
    app.press("end", b"\x08", b"\x7f")
    assert line(app) == "foo b"


def test_word_backspaces_undo_as_one_step(ltt, ws):
    app = edit(ltt, ws, "foo bar baz\n")
    app.press("end", b"\x1b\x7f", b"\x1b\x7f", "ctrl+z")
    assert line(app) == "foo bar baz"


# ---------------------------------------------------------------- legacy sequences

HOME_END = {"csi": (b"\x1b[H", b"\x1b[F"), "ss3": (b"\x1bOH", b"\x1bOF"), "tilde-1-4": (b"\x1b[1~", b"\x1b[4~"),
            "tilde-7-8": (b"\x1b[7~", b"\x1b[8~")}


@pytest.mark.parametrize("home, end", HOME_END.values(), ids=HOME_END.keys())
def test_home_and_end_in_every_encoding(ltt, ws, home, end):
    app = edit(ltt, ws, "one two\n")
    app.press(end)
    assert pos(app) == (1, 8)
    app.press(home)
    assert pos(app) == (1, 1)


def test_application_mode_arrows_move(ltt, ws):
    app = edit(ltt, ws, "abc\ndef\n")
    app.press(b"\x1bOC", b"\x1bOC", b"\x1bOB", b"\x1bOD")
    assert pos(app) == (2, 2)
    app.press(b"\x1bOA")
    assert pos(app) == (1, 2)


def test_unknown_sequence_is_ignored_and_the_next_key_typed(ltt, ws):
    app = edit(ltt, ws, "x\n")
    app.press(b"\x1b[99X" + b"\x1b[?25l" + b"\x1bO~" + b"a")
    assert line(app) == "ax"


def test_overlong_sequence_is_dropped_whole(ltt, ws):  # T8: the tail of a cut-off sequence was typed
    app = edit(ltt, ws, "x\n")
    app.press(b"\x1b[" + b"9" * 300 + b"A" + b"z")
    s = app.snap()
    assert s.editor_lines[0] == "zx" and s.pos[:3] == (1, 2, 2)


def test_broken_utf8_byte_does_not_swallow_the_next_key(ltt, ws):  # V11
    app = edit(ltt, ws, "x\n")
    app.press(b"\xc3" + b"a" + b"\xe2\x82" + b"b" + b"\xff" + b"c")
    assert line(app) == "abcx"


def test_c1_control_is_never_typed(ltt, ws):  # V11
    app = edit(ltt, ws, "x\n")
    app.press(b"\xc2\x9b" + b"\xc2\x85" + b"y")
    assert line(app) == "yx"


def test_stray_control_bytes_type_nothing(ltt, ws):
    app = edit(ltt, ws, "x\n")
    app.press(b"\x1c\x1d\x1e\x1f" + b"y")
    s = app.snap()
    assert s.editor_lines[0] == "yx" and s.mode == "EDITOR"


def test_two_escapes_are_two_presses_not_a_sequence(ltt, ws):
    app = edit(ltt, ws, "x\n")
    app.write(b"\x1b\x1b")
    s = app.wait_mode("TREE")
    assert s.editor_lines[0] == "x"


@pytest.mark.parametrize("data", [b"\x00", b"\x1b[57441;2u", b"\x1b[57358u"], ids=["ctrl-space", "kitty-shift", "kitty-caps"])
def test_a_key_that_means_nothing_does_not_stall_the_screen(ltt, ws, data):  # T4: the loop waited for the next key
    ws.write("proj/a.txt", "a\n")
    app = ltt(ws.path("proj"))
    app.write(data)
    ws.write("proj/new.txt", "n\n")
    app.wait_tree("new.txt")
