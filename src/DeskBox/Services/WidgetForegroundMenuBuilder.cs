using DeskBox.Models;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Services;

internal static class WidgetForegroundMenuBuilder
{
    public static MenuFlyoutSubItem Create(
        WidgetConfig config,
        LocalizationService localizationService,
        Action<string?> applyModeOverride,
        Action chooseCustomColor)
    {
        string? modeOverride = WidgetForegroundSettings.GetModeOverride(config);
        var menu = new MenuFlyoutSubItem
        {
            Text = localizationService.T("Widget.Foreground.Menu"),
            Icon = new FontIcon { Glyph = "\uE790" }
        };

        menu.Items.Add(CreateChoice(
            localizationService.T("Widget.Foreground.UseGlobal"),
            modeOverride is null,
            () => applyModeOverride(null)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(CreateChoice(
            localizationService.T("Settings.WidgetForeground.FollowTheme"),
            modeOverride == WidgetForegroundSettings.ModeFollowTheme,
            () => applyModeOverride(WidgetForegroundSettings.ModeFollowTheme)));
        menu.Items.Add(CreateChoice(
            localizationService.T("Settings.WidgetForeground.Light"),
            modeOverride == WidgetForegroundSettings.ModeLight,
            () => applyModeOverride(WidgetForegroundSettings.ModeLight)));
        menu.Items.Add(CreateChoice(
            localizationService.T("Settings.WidgetForeground.Dark"),
            modeOverride == WidgetForegroundSettings.ModeDark,
            () => applyModeOverride(WidgetForegroundSettings.ModeDark)));
        menu.Items.Add(CreateChoice(
            localizationService.T("Settings.WidgetForeground.AutoBackground"),
            modeOverride == WidgetForegroundSettings.ModeAutoBackground,
            () => applyModeOverride(WidgetForegroundSettings.ModeAutoBackground)));
        menu.Items.Add(CreateChoice(
            localizationService.T("Widget.Foreground.CustomColor"),
            modeOverride == WidgetForegroundSettings.ModeCustom,
            chooseCustomColor));

        return menu;
    }

    private static ToggleMenuFlyoutItem CreateChoice(
        string text,
        bool isChecked,
        Action apply)
    {
        var item = new ToggleMenuFlyoutItem
        {
            Text = text,
            IsChecked = isChecked
        };
        item.Click += (_, _) => apply();
        return item;
    }
}
