namespace XCTrackMapSync;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var demoMode = args
            .FirstOrDefault(argument => argument.StartsWith("--demo=", StringComparison.OrdinalIgnoreCase))?
            .Split('=', 2)[1];
        var language = args
            .FirstOrDefault(argument => argument.StartsWith("--lang=", StringComparison.OrdinalIgnoreCase))?
            .Split('=', 2)[1];
        Application.Run(new MainForm(demoMode, language));
    }
}
