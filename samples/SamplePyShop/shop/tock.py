from shop import tick


def hit(n: int) -> int:
    return 0 if n <= 0 else tick.hit(n - 1)
