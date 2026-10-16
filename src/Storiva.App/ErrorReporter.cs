namespace Storiva.App;

/// <summary>Last-resort error handling: tell the user what happened instead of crashing silently.</summary>
internal static class ErrorReporter
{
    private static int _showing;

    public static void Show(Exception? exception, bool isTerminating = false)
    {
        // Avoid stacking dialogs if an error repeats while one is already open.
        if (Interlocked.Exchange(ref _showing, 1) == 1)
        {
            return;
        }

        try
        {
            var message = exception is null
                ? "An unknown error occurred."
                : $"{exception.GetType().Name}: {exception.Message}";
            var suffix = isTerminating
                ? "\r\n\r\nStoriva has to close."
                : "\r\n\r\nYou can keep working; if the problem repeats, restart Storiva.";
            MessageBox.Show(
                "Something went wrong in Storiva.\r\n\r\n" + message + suffix,
                "Storiva — unexpected error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            Interlocked.Exchange(ref _showing, 0);
        }
    }
}
