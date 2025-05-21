using System;
using System.Configuration;
using System.Data;
using System.Windows;

namespace RepoToTxtGui;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            // Apply the stored or default theme on startup
            ThemeManager.ApplyTheme(ThemeManager.LoadCurrentThemePreference());

            base.OnStartup(e);
            MainWindow mainWindow = new MainWindow();
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error during application startup: {ex.Message}\n\nStack trace: {ex.StackTrace}",
                "Application Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}

