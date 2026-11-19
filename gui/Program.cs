namespace XCTrackMapSync;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var singleInstance = new Mutex(
            initiallyOwned: true,
            name: "Local\\XCTrackMapSync.SingleInstance",
            createdNew: out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show(
                "XCTrack Map Sync jau paleista. Uždarykite esamą langą prieš paleisdami kitą kopiją.\n\n" +
                "XCTrack Map Sync is already running. Close the existing window before starting another copy.",
                "XCTrack Map Sync",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        var demoMode = args
            .FirstOrDefault(argument => argument.StartsWith("--demo=", StringComparison.OrdinalIgnoreCase))?
            .Split('=', 2)[1];
        var language = args
            .FirstOrDefault(argument => argument.StartsWith("--lang=", StringComparison.OrdinalIgnoreCase))?
            .Split('=', 2)[1];
        Application.Run(new MainForm(demoMode, language));
        GC.KeepAlive(singleInstance);
    }
}
