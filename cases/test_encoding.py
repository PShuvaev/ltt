"""Encodings: UTF-8 with and without BOM, UTF-16 LE/BE, Latin-1, broken text, binary files, files over 8 MB."""
import os
import shutil

import pytest

from harness import shared
from save_helpers import open_by_path, save

TEXT = "héllo wörld\nnaïve café\n"

# id: (encode text as the file stores it, what the open status says about the format)
FORMATS = {
    "utf8": (lambda t: t.encode(), ""),
    "utf8_bom": (lambda t: b"\xef\xbb\xbf" + t.encode(), ", UTF-8 with BOM"),
    "utf8_bom_cr": (lambda t: b"\xef\xbb\xbf" + t.replace("\n", "\r").encode(), ", UTF-8 with BOM, CR"),
    "utf16le": (lambda t: b"\xff\xfe" + t.encode("utf-16-le"), ", UTF-16 LE"),
    "utf16be": (lambda t: b"\xfe\xff" + t.encode("utf-16-be"), ", UTF-16 BE"),
    "utf16le_crlf": (lambda t: b"\xff\xfe" + t.replace("\n", "\r\n").encode("utf-16-le"), ", UTF-16 LE, CRLF"),
    "utf16be_crlf": (lambda t: b"\xfe\xff" + t.replace("\n", "\r\n").encode("utf-16-be"), ", UTF-16 BE, CRLF"),
    "latin1": (lambda t: t.encode("latin-1"), ", Latin-1 (not valid UTF-8)"),
    "latin1_crlf": (lambda t: t.replace("\n", "\r\n").encode("latin-1"), ", Latin-1 (not valid UTF-8), CRLF"),
}

BINARY = "Error: The file looks binary; editing is disabled."


@pytest.mark.parametrize("fmt", list(FORMATS))
def test_open_shows_the_text_and_names_the_format(ltt, ws, fmt):
    encode, described = FORMATS[fmt]
    p = ws.write("f.txt", encode(TEXT))
    app = ltt(ws.root, cols=140)
    s = open_by_path(app, p)
    assert f"Opened f.txt (3 lines{described}), tab 1/1." in s.status
    assert s.editor_lines[:3] == ["héllo wörld", "naïve café", ""]
    assert not s.tabs[0].dirty


# Not in FORMATS while they fail: id -> (BOM, codec, what the open status says)
UTF32 = {
    "utf32le": (b"\xff\xfe\x00\x00", "utf-32-le", ", UTF-32 LE"),
    "utf32be": (b"\x00\x00\xfe\xff", "utf-32-be", ", UTF-32 BE"),
}


@pytest.mark.xfail(strict=True, reason="BUG: UTF-32 LE opens as UTF-16 LE with a NUL between the letters "
                                       "(its BOM starts like UTF-16's), and UTF-32 BE is refused as binary")
@pytest.mark.parametrize("fmt", list(UTF32))
def test_utf32_opens_as_utf32_and_an_edit_keeps_it(ltt, ws, fmt):  # idea from dte's test/encoding.c
    bom, codec, described = UTF32[fmt]
    p = ws.write("f.txt", bom + TEXT.encode(codec))
    app = ltt(ws.root, cols=140)
    s = open_by_path(app, p)
    assert f"Opened f.txt (3 lines{described}), tab 1/1." in s.status
    assert s.editor_lines[:3] == ["héllo wörld", "naïve café", ""]
    app.type("Ünï ")
    app.wait_editor(["Ünï héllo wörld"])
    save(app, "f.txt")
    assert ws.read_bytes("f.txt") == bom + ("Ünï " + TEXT).encode(codec)


def _east_asian_text():
    chars = ([chr(c) for c in range(0x4E00, 0x4E00 + 1200)]   # CJK ideographs
             + [chr(c) for c in range(0x3041, 0x3097)]        # hiragana
             + [chr(c) for c in range(0xAC00, 0xAC00 + 600)])  # hangul
    return "".join("".join(chars[i:i + 40]) + "\n" for i in range(0, len(chars), 40))


@pytest.mark.parametrize("codec", ["big5", "gb18030", "shift_jis", "euc_kr"])
def test_an_east_asian_legacy_encoding_survives_an_edit(ltt, ws, codec):
    # Not UTF-8, so it opens as Latin-1 (a character per byte) and must come back byte for byte
    # (idea from Helix's WHATWG encoding fixtures; the text here is made from the codecs).
    data = _east_asian_text().encode(codec, errors="ignore")
    with pytest.raises(UnicodeDecodeError):
        data.decode("utf-8")
    p = ws.write("cjk.txt", data)
    app = ltt(p)
    app.press("ctrl+end")
    app.type("end")
    save(app, "cjk.txt")
    assert ws.read_bytes("cjk.txt") == data + b"end"


@pytest.mark.parametrize("fmt", list(FORMATS))
def test_open_and_save_keeps_the_encoding_byte_for_byte(ltt, ws, fmt):  # D3
    data = FORMATS[fmt][0](TEXT)
    p = ws.write("f.txt", data)
    app = ltt(p)
    save(app, "f.txt")
    assert ws.read_bytes("f.txt") == data


@pytest.mark.parametrize("fmt", list(FORMATS))
def test_an_edit_is_saved_in_the_files_encoding(ltt, ws, fmt):  # D3: UTF-16 used to become UTF-8
    encode = FORMATS[fmt][0]
    p = ws.write("f.txt", encode(TEXT))
    app = ltt(p)
    app.type("Ünï ")
    app.wait_editor(["Ünï héllo wörld"])
    save(app, "f.txt")
    assert ws.read_bytes("f.txt") == encode("Ünï " + TEXT)


def test_latin1_e_acute_stays_one_byte(ltt, ws):  # D3: it used to turn into U+FFFD
    p = ws.write("f.txt", b"caf\xe9\n")
    app = ltt(p)
    app.wait_editor(["café"])
    app.press("end")
    app.type("!")
    save(app, "f.txt")
    assert ws.read_bytes("f.txt") == b"caf\xe9!\n"


def test_latin1_refuses_to_save_a_character_it_cannot_hold(ltt, ws):
    p = ws.write("f.txt", b"caf\xe9\n")
    app = ltt(p)
    app.press("end")
    app.type("€")
    app.press("ctrl+s")
    s = app.wait_status("Save error: '€' cannot be saved in this file's Latin-1 encoding.")
    assert s.tabs[0].dirty and app.alive
    assert ws.read_bytes("f.txt") == b"caf\xe9\n"
    app.press("backspace")
    save(app, "f.txt")
    assert ws.read_bytes("f.txt") == b"caf\xe9\n"


@pytest.mark.xfail(strict=True, reason="BUG: the Latin-1 save error names half of an emoji ('\ufffd') "
                                       "instead of the character")
def test_the_latin1_save_error_names_a_character_outside_the_bmp(ltt, ws):
    p = ws.write("f.txt", b"caf\xe9\n")
    app = ltt(p, cols=140)
    app.press("end")
    app.type("👀")
    app.press("ctrl+s")
    s = app.wait_status("Save error:")
    assert "'👀' cannot be saved in this file's Latin-1 encoding." in s.status


@pytest.mark.parametrize("fmt", ["utf16le", "utf16be", "utf8_bom"])
def test_a_typed_emoji_is_saved_whole(ltt, ws, fmt):
    encode = FORMATS[fmt][0]
    p = ws.write("f.txt", encode("text\n"))
    app = ltt(p)
    app.type("👀 ")
    save(app, "f.txt")
    assert ws.read_bytes("f.txt") == encode("👀 text\n")


@pytest.mark.parametrize("data", [
    b"\xff\xfe" + "h".encode("utf-16-le") + b"\x00\xd8" + "i\n".encode("utf-16-le"),  # a lone high surrogate
    b"\xfe\xff" + "h".encode("utf-16-be") + b"\xdc\x00" + "i\n".encode("utf-16-be"),  # a lone low surrogate
    b"\xff\xfe" + "hi\n".encode("utf-16-le") + b"x",                                   # an odd byte at the end
], ids=["lone_high_surrogate", "lone_low_surrogate", "odd_byte"])
def test_broken_utf16_is_refused_and_left_alone(ltt, ws, data):  # V6
    p = ws.write("proj/f.txt", data)
    mtime = os.stat(p).st_mtime_ns
    app = ltt(ws.path("proj"))
    s = open_by_path(app, p)
    assert s.status == BINARY and s.tabs == [] and s.mode == "TREE"
    assert ws.read_bytes("proj/f.txt") == data and os.stat(p).st_mtime_ns == mtime


def test_utf16_without_a_bom_is_refused_as_binary(ltt, ws):
    p = ws.write("f.txt", "hello\n".encode("utf-16-le"))
    app = ltt(p)
    s = app.wait_status(BINARY)
    assert s.tabs == [] and s.mode == "TREE"


def test_a_stray_byte_after_a_utf8_bom_opens_as_latin1(ltt, ws):  # V7: it used to be unopenable
    p = ws.write("f.txt", b"\xef\xbb\xbfabc\xff\n")
    app = ltt(ws.root, cols=140)
    s = open_by_path(app, p)
    assert "(2 lines, Latin-1 (not valid UTF-8))" in s.status
    assert s.editor_lines[0] == "ï»¿abcÿ"
    save(app, "f.txt")
    assert ws.read_bytes("f.txt") == b"\xef\xbb\xbfabc\xff\n"
    app.press("end")
    app.type("X")
    save(app, "f.txt")
    assert ws.read_bytes("f.txt") == b"\xef\xbb\xbfabc\xffX\n"


@pytest.mark.parametrize("data", [
    b"a\x80b\n",                 # a lone continuation byte
    b"\xc3(\n",                  # a lead byte without its continuation
    b"\xed\xa0\x80 surrogate\n",  # an encoded surrogate
    b"\xc0\xaf overlong\n",      # an overlong slash
    b"\xf5\x80\x80\x80 big\n",   # past U+10FFFF
    b"cut at the end \xe2\x82",  # a truncated last character
    b"x" * 100_000 + "\u00e9\n".encode() + b"\xff\n",  # valid for the first read chunks, broken after them
], ids=["continuation", "missing_continuation", "surrogate", "overlong", "too_big", "truncated", "late"])
def test_invalid_utf8_opens_as_latin1_and_saves_byte_for_byte(ltt, ws, data):
    p = ws.write("f.txt", data)
    app = ltt(p)
    assert app.snap().tabs[0].name == "f.txt"
    save(app, "f.txt")
    assert ws.read_bytes("f.txt") == data


def test_a_binary_file_is_refused_from_the_tree(ltt, ws):
    ws.write("proj/bin.dat", b"\x00\x01\x02binary\n")
    app = ltt(ws.path("proj"))
    app.press("down")
    assert app.snap().tree_selected.name == "bin.dat"
    s = app.press("enter")
    s = app.wait_status(BINARY)
    assert s.tabs == [] and s.mode == "TREE"


def test_a_binary_file_on_the_command_line_opens_no_tab(ltt, ws):
    p = ws.write("bin.dat", b"\x7fELF\x02\x01\x01\x00\x00\x00")
    app = ltt(p)
    s = app.wait_status(BINARY)
    assert s.tabs == [] and s.mode == "TREE" and s.editor_hint.startswith("Pick a file")
    assert ws.read_bytes("bin.dat") == b"\x7fELF\x02\x01\x01\x00\x00\x00"


def test_a_nul_byte_deep_in_the_file_makes_it_binary(ltt, ws):
    p = ws.write("f.txt", b"text line\n" * 20_000 + b"\x00tail\n")  # the NUL is 200 KB in
    app = ltt(ws.root)
    s = open_by_path(app, p)
    assert s.status == BINARY and s.tabs == []


@pytest.mark.parametrize("fmt", ["utf8_bom", "utf16le", "utf16be"])
def test_a_bom_alone_is_an_empty_file_that_keeps_its_bom(ltt, ws, fmt):
    encode = FORMATS[fmt][0]
    p = ws.write("f.txt", encode(""))
    app = ltt(p)
    assert app.snap().editor_lines == [""]
    save(app, "f.txt")
    assert ws.read_bytes("f.txt") == encode("")
    app.type("a")
    save(app, "f.txt")
    assert ws.read_bytes("f.txt") == encode("a")


def _nine_megabytes():
    with shared("save-9mb") as (folder, ready):
        path = os.path.join(folder, "big.txt")
        if not ready:
            with open(path, "wb") as f:
                f.write(b"".join(b"%07d the quick brown fox jumps over the lazy dog\n" % i for i in range(180_000)))
    return path


def test_a_file_over_8_mb_opens_and_saves_byte_for_byte(ltt, ws):
    src = _nine_megabytes()
    p = ws.path("big.txt")
    shutil.copyfile(src, p)
    assert os.path.getsize(p) > 9_000_000
    app = ltt(p)
    s = app.wait_editor(lambda lines: lines[:1] == ["0000000 the quick brown fox jumps over the lazy dog"])
    assert s.pos[:2] == (1, 180_001)
    app.press("ctrl+end")
    app.type("!")
    save(app, "big.txt")
    with open(src, "rb") as f:
        assert ws.read_bytes("big.txt") == f.read() + b"!"
