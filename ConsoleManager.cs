using System.Runtime.InteropServices;

public static class ConsoleManager
{
    [DllImport("kernel32.dll")]
    public static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    public static extern bool FreeConsole();
}

