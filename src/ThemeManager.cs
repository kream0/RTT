using System;
using System.Windows;

namespace RepoToTxtGui
{
    public static class ThemeManager
    {
        public enum AppTheme { Light, Dark }

        private const string ThemeSettingKey = "AppThemePreference";

        public static void ApplyTheme(AppTheme theme)
        {
            var mergedDictionaries = Application.Current.Resources.MergedDictionaries;

            // Remove existing theme dictionaries to prevent conflicts or duplicates
            for (int i = mergedDictionaries.Count - 1; i >= 0; i--)
            {
                var dict = mergedDictionaries[i];
                if (dict.Source != null && (dict.Source.OriginalString.Contains("LightTheme.xaml") || dict.Source.OriginalString.Contains("DarkTheme.xaml")))
                {
                    mergedDictionaries.RemoveAt(i);
                }
            }

            string themeUriPath = theme == AppTheme.Dark ? "Themes/DarkTheme.xaml" : "Themes/LightTheme.xaml";
            Uri themeUri = new Uri(themeUriPath, UriKind.Relative);

            ResourceDictionary themeDictionary = new ResourceDictionary { Source = themeUri };
            mergedDictionaries.Add(themeDictionary);

            SaveCurrentThemePreference(theme);
        }

        public static AppTheme LoadCurrentThemePreference()
        {
            // This is a placeholder for loading from a persistent store like settings or registry.
            // For simplicity, we'll use a temporary approach.
            // Replace this with actual settings loading if persistence is required.
            try
            {
                string? themeName = Application.Current.Properties[ThemeSettingKey] as string;
                if (Enum.TryParse<AppTheme>(themeName, out AppTheme theme))
                {
                    return theme;
                }
            }
            catch
            {
                // Ignore errors loading preference, default to Light.
            }
            return AppTheme.Light; // Default theme
        }

        private static void SaveCurrentThemePreference(AppTheme theme)
        {
            // This is a placeholder for saving to a persistent store.
            // For simplicity, using Application.Current.Properties (session-only).
            // Replace with actual settings saving for persistence across sessions.
            try
            {
                Application.Current.Properties[ThemeSettingKey] = theme.ToString();
            }
            catch
            {
                // Ignore errors saving preference.
            }
        }
    }
}
