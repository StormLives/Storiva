using Storiva.App.Dialogs;
using Storiva.App.Theme;
using Storiva.Core.Settings;

namespace Storiva.App.Main;

/// <summary>
/// Light / Dark / System. <see cref="IThemeService"/> decides the palette (and follows Windows in System mode);
/// this part re-colours the open windows without a restart: assigned colours are mapped by role
/// (<see cref="ThemeApplier"/>), icons are re-rendered through the same path as a DPI change, and owner-drawn parts
/// repaint with the new palette.
/// </summary>
internal sealed partial class MainForm
{
    /// <summary>Applies and saves the preference chosen in Tools › Options › Interface.</summary>
    internal void ApplyThemePreference(ThemePreference preference)
    {
        if (preference == _theme.Preference)
        {
            return;
        }

        _theme.SetPreference(preference); // raises Changed when the effective palette changes
        _activity.Write($"Theme set to {ThemeResolver.Label(preference)} (effective: {_theme.Effective})." +
            (_theme.PersistenceWarning is { } warning ? " " + warning : " Saved for next time."));
        SetStatus($"Theme: {ThemeResolver.Label(preference)}.");
    }

    private void OnThemeChanged(object? sender, ThemeChangedEventArgs e)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            // Defence in depth: controls are only touched on the UI thread.
            BeginInvoke(() => OnThemeChanged(sender, e));
            return;
        }

        SuspendLayout();
        try
        {
            _bottomTabs.UseCustomPainting(e.Current.IsDark);
            ThemeApplier.Apply(this, e.Previous, e.Current);
            foreach (var form in Application.OpenForms.Cast<Form>().Where(f => f != this && !f.IsDisposed).ToList())
            {
                ThemeApplier.Apply(form, e.Previous, e.Current);
            }

            ButtonStyling.RefreshAll();
            if (_layoutInitialized)
            {
                ApplyDpiResources();
                RebuildTree();
                RefreshList(keepSelection: true);
                RebuildLocationBar();
                RefreshTaskView();
                BindDetails(force: true);
            }
        }
        finally
        {
            ResumeLayout(performLayout: true);
        }

        Invalidate(invalidateChildren: true);
        if (sender is not null && _theme.Preference == ThemePreference.System)
        {
            _activity.Write($"Windows switched to {(e.Current.IsDark ? "dark" : "light")} mode; Storiva follows it (theme: System).");
        }
    }

    /// <summary>Theme in use (smoke test).</summary>
    internal EffectiveTheme EffectiveTheme => _theme.Effective;
}
