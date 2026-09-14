def wcwidth(c):
    return 1
def wcswidth(s):
    return len(s)
def iter_graphemes_reverse(s):
    for ch in reversed(s):
        yield ch
