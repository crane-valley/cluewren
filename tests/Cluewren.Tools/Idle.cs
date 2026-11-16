namespace Cluewren.Tools;

// A process that only lives, until its stdin closes: another brain or editor, for the tests that need a second live pid.
static class Idle
{
    public static int Run()
    {
        Console.In.ReadToEnd();
        return 0;
    }
}
