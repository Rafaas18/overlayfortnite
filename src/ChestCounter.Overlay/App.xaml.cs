using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace ChestCounter.Overlay;

public partial class App : Application
{
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(initiallyOwned: true, "OverlayCofres.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("El overlay de cofres ya está abierto (mira el icono junto al reloj).", "Overlay Cofres");
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;

        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var logPath = Path.Combine(Settings.DataFolder, "error.log");
        try
        {
            Directory.CreateDirectory(Settings.DataFolder);
            File.AppendAllText(logPath, $"{DateTime.Now:u} {e.Exception}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Nothing else we can do if the log cannot be written.
        }

        MessageBox.Show($"Error inesperado: {e.Exception.Message}\n\nDetalle guardado en {logPath}", "Overlay Cofres");
        e.Handled = true;
    }
}
