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
        base.OnStartup(e);
        MainWindow mainWindow = new MainWindow();
        mainWindow.Show();
    }
}

