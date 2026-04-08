using Velopack;

namespace Azure.PIM.Tray;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Velopack must run before anything else — it handles install/update
        // hooks and will exit the process immediately when invoked by the updater.
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
