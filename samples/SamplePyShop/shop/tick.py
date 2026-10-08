from shop import tock


def hit(n: int) -> int:
    return 0 if n <= 0 else tock.hit(n - 1)
