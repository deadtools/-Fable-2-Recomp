using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Fable2Launcher;

public static class LauncherLanguageTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("Language regression: " + message);
    }

    private static bool ContainsCyrillic(string text) =>
        text.Any(c => c is >= '\u0400' and <= '\u04FF');

    public static void Run(string testDirectory)
    {
        Check(LauncherLanguage.Normalize(null) == "en", "initial language is not English");
        Check(LauncherLanguage.Normalize("RU") == "ru", "Russian code not recognized");
        Check(LauncherLanguage.Normalize("unexpected") == "en", "unsupported code accepted");

        // Release 1.6.1 and its EN/RU change log must be localized in both directions.
        Check(LauncherLanguage.Translate("Версия 1.6.1", "en") == "Version 1.6.1",
            "release 1.6.1 label was not translated");
        Check(LauncherLanguage.Translate("Что нового в лаунчере 1.6.1", "en") ==
              "What’s new in launcher 1.6.1", "release notes title was not translated");
        const string languageChange = "Добавлено переключение языка интерфейса между английским и русским (EN / RU).";
        Check(LauncherLanguage.Translate(languageChange, "en") ==
              "Added launcher interface language switching between English and Russian (EN / RU).",
              "language feature is not in English release notes");
        Check(LauncherLanguage.Translate(languageChange, "ru") == languageChange,
              "language feature disappeared from Russian release notes");

        string localeFile = Path.Combine(testDirectory, "language.txt");
        Check(LauncherLanguage.Load(localeFile) == "en", "missing setting not English");
        LauncherLanguage.Save(localeFile, "ru");
        Check(LauncherLanguage.Load(localeFile) == "ru", "Russian selection not persisted");
        LauncherLanguage.Save(localeFile, "en");
        Check(LauncherLanguage.Load(localeFile) == "en", "English selection not persisted");
        File.WriteAllText(localeFile, "unexpected");
        Check(LauncherLanguage.Load(localeFile) == "en", "invalid setting did not default to English");

        const string russian = "Текстуры героя и собаки";
        Check(LauncherLanguage.Translate(russian, "en") == "Hero and dog textures",
            "core translation missing");
        Check(LauncherLanguage.Translate(russian, "ru") == russian,
            "Russian UI was modified");
        Check(LauncherLanguage.Translate("D:\\Games\\Fable2", "en") == "D:\\Games\\Fable2",
            "an external file path was translated");
        Check(LauncherLanguage.Translate("Внутреннее: 2560 × 1440", "en") == "Internal: 2560 × 1440",
            "calculated resolution not translated");
        Check(LauncherLanguage.Translate("Сохранено: fable_2.toml.", "en") == "Saved: fable_2.toml.",
            "dynamic save message not translated");
        Check(LauncherLanguage.Translate("Запрошенный режим: Морфинг на GPU в реальном времени", "en") ==
              "Requested mode: Realtime GPU morphing", "texture detail not translated");
        Check(LauncherLanguage.Translate("DualShock включён (v17): заменено 16 GUI-текстур", "en") ==
              "DualShock enabled (v17): replaced 16 GUI textures", "DualShock status not translated");

        // The WPF window cannot run in the console test runner, so check every
        // Russian declarative label against the exact same localization catalogue.
        string xamlPath = Path.Combine(AppContext.BaseDirectory, "TestData", "MainWindow.xaml");
        XDocument xaml = XDocument.Load(xamlPath);
        int examined = 0;
        foreach (XElement element in xaml.Root!.DescendantsAndSelf())
        {
            foreach (XAttribute attribute in element.Attributes())
            {
                string property = attribute.Name.LocalName;
                if (property is not ("Text" or "Content" or "Title" or "ToolTip" or "Name")) continue;
                if (!ContainsCyrillic(attribute.Value)) continue;
                examined++;
                Check(!ContainsCyrillic(LauncherLanguage.Translate(attribute.Value, "en")),
                    "untranslated XAML attribute: " + attribute.Value);
            }
            if (element.Name.LocalName == "ComboBoxItem" && ContainsCyrillic(element.Value))
            {
                examined++;
                Check(!ContainsCyrillic(LauncherLanguage.Translate(element.Value.Trim(), "en")),
                    "untranslated dropdown item: " + element.Value.Trim());
            }
        }
        Check(examined > 75, "too few XAML labels checked");
        Check(xaml.Descendants().Any(node => node.Name.LocalName == "ComboBox"
            && node.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == "LanguageCombo")),
            "EN/RU selector not present");
        Console.WriteLine($"EN/RU localization: persistence, switching, game path safety and {examined} UI strings passed.");
    }
}
