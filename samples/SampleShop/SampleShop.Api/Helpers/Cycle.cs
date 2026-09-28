namespace SampleShop.Api.Helpers;

// Two classes that call each other: the score should find one cycle.
public class Tick
{
    public void Hit(Tock other, int n)
    {
        if (n > 0) other.Hit(this, n - 1);
    }
}

public class Tock
{
    public void Hit(Tick other, int n)
    {
        if (n > 0) other.Hit(this, n - 1);
    }
}
