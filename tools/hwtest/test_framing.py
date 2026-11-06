"""Framing (PROTOCOL.md §2): noise, ':' restart, line length, unknown numbers."""
import pytest


def test_ping(board):
    assert board.cmd(":25#") == "OK#"


def test_noise_is_ignored(board):
    assert board.raw("hello\r\n#xyz", timeout=0.3) == ""     # no reply to bytes outside ':...#'
    board.send("garbage##\r\n")
    assert board.cmd(":25#") == "OK#"


def test_colon_restarts_command(board):
    assert board.cmd(":3:25#") == "OK#"
    assert board.cmd(":99junk:25#") == "OK#"


def test_line_limit(board):
    line46 = "25" + "0" * 44                                  # exactly 46 characters
    assert board.cmd(f":{line46}#") == "OK#"                  # :25 ignores its parameters
    assert board.cmd(f":{line46}0#") == "ERR,11#"             # 47 characters
    # PROTOCOL.md §2: hosts never burst more than ~64 bytes. On the rev 1 board a ~200-byte burst
    # sent right after a reply loses its '#' (no reply), as the spec allows.
    assert board.cmd(":25" + "x" * 60 + "#") == "ERR,11#"   # 64 bytes
    assert board.cmd(":25#") == "OK#"                         # and the parser recovered


@pytest.mark.parametrize("text", [":#", ":2#", ":A5#", ":-1#", ": 25#"])
def test_bad_format(board, text):
    assert board.cmd(text) == "ERR,11#"


@pytest.mark.parametrize("n", [0, 1, 5, 10, 19])
def test_00_19_unknown(board, n):
    assert board.cmd(f":{n:02d}#") == "ERR,10#"


@pytest.mark.parametrize("n", [20, 21, 26, 27, 28, 29, 35, 36, 37, 38, 39, 45, 49, 57, 68, 69, 97, 98, 99])
def test_unused_numbers(board, n):
    assert board.cmd(f":{n:02d}#") == "ERR,10#"


def test_parameter_separators(board):
    a = board.cmd(":47F#")
    assert board.cmd(":47 F#") == a
    assert board.cmd(":47,F#") == a
    assert board.cmd(":47,f#") == a


@pytest.mark.parametrize("text", [":41,,F#", ":42,F,,4#", ":51,,5#", ":72,1,,2#", ":93,,1#",
                                  ":48,F,1,2,3,4,5,6,7#"])
def test_malformed_arguments_are_bad_parameter(board, text):
    """An empty value or too many values is a bad parameter (ERR,7) in every block."""
    assert board.cmd(text) == "ERR,7#"
