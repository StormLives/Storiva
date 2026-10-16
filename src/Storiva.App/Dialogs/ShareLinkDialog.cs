using Storiva.App.Icons;
using Storiva.App.Theme;
using Storiva.Core.Remote;

namespace Storiva.App.Dialogs;

/// <summary>
/// "Generate Web URL.." / "Generate Web URLs.." [F menu items E5/E6; layout H]: pre-signed GET links for one or more
/// files, valid for 15 minutes to 7 days. The links are computed locally by the SDK when the dialog opens and when
/// the validity changes; they are shown read-only, copied only on request and never kept after the dialog closes.
/// </summary>
internal sealed class ShareLinkDialog : StorivaDialog
{
    private readonly Func<TimeSpan, CancellationToken, Task<IReadOnlyList<PresignedUrl>>> _generate;
    private readonly Action<string> _copy;
    private readonly ComboBox _validity = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _urls = new() { ReadOnly = true, Multiline = true };
    private readonly Label _status;
    private readonly Label _error;
    private readonly Label _notice;
    private CancellationTokenSource? _cancellation;
    private int _generation;

    /// <param name="subject">What the links are for, e.g. "\"report.pdf\" in bucket \"team\"" or "12 files in bucket \"team\"".</param>
    /// <param name="generate">Computes the links for a validity (no request is sent to the server).</param>
    /// <param name="copy">Puts text on the clipboard (kept out of the Windows clipboard history).</param>
    public ShareLinkDialog(string subject, int count, Func<TimeSpan, CancellationToken, Task<IReadOnlyList<PresignedUrl>>> generate, Action<string> copy)
        : base(count == 1 ? "Generate Web URL" : "Generate Web URLs",
            $"Pre-signed {(count == 1 ? "link" : "links")} to download {subject} with a browser, without an account or credentials.",
            Lucide.ExternalLink, "Copy")
    {
        _generate = generate;
        _copy = copy;
        CancelButtonControl.Text = "Close";

        foreach (var validity in ShareLinks.ValidityChoices)
        {
            _validity.Items.Add(new ValidityChoice(validity));
        }

        _validity.SelectedIndex = ShareLinks.ValidityChoices.ToList().IndexOf(ShareLinks.DefaultValidity);
        AddField("Valid for:", _validity,
            "At most 7 days (S3 Signature Version 4). The link is signed on this computer with the connection's key; nothing is sent to the server.");

        // One long URL wraps; a list keeps one URL per line and scrolls.
        _urls.WordWrap = count == 1;
        _urls.ScrollBars = count == 1 ? ScrollBars.Vertical : ScrollBars.Both;
        _urls.Height = count == 1 ? 84 : 150; // logical pixels: the dialog scales with the DPI
        _error = AddField(count == 1 ? "Web URL:" : $"Web URLs ({count}, one per line):", _urls, "Read-only. Copy puts the whole text on the clipboard.");
        _status = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(FieldWidth, 0),
            ForeColor = Palette.TextSecondary,
            Margin = new Padding(Metrics.SpaceL, Metrics.SpaceXs, 0, Metrics.SpaceM),
        };
        Body.Controls.Add(_status);
        _notice = AddNotice(count == 1
            ? "Anyone who has this link can download the file until it expires. Share it only with people who may read the file. " +
              "Storiva does not keep the link; it stops working early only if the key that signed it is deactivated or loses access to the object."
            : "Anyone who has these links can download the files until they expire. Share them only with people who may read the files. " +
              "Storiva does not keep the links; a link stops working early only if the key that signed it is deactivated or loses access to the object.");

        _validity.SelectedIndexChanged += (_, _) => _ = RegenerateAsync();
        PrimaryButton.Enabled = false;
    }

    /// <summary>Links currently shown (empty while generating or after a failure).</summary>
    internal IReadOnlyList<PresignedUrl> Links { get; private set; } = [];

    internal TimeSpan Validity => _validity.SelectedItem is ValidityChoice choice ? choice.Value : ShareLinks.DefaultValidity;

    // Smoke-test hooks.
    internal bool IsReady => Links.Count > 0 && PrimaryButton.Enabled;

    internal string StatusText => _status.Text;

    internal string NoticeText => _notice.Text;

    internal string VisibleError => _error.Visible ? _error.Text : string.Empty;

    internal void ChooseValidity(TimeSpan validity) => _validity.SelectedIndex = ShareLinks.ValidityChoices.ToList().IndexOf(validity);

    internal void ClickCopy() => OnPrimaryClick();

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _ = RegenerateAsync();
        _validity.Focus();
    }

    /// <summary>Copies every link; the dialog stays open (another validity can be chosen and copied).</summary>
    protected override void OnPrimaryClick()
    {
        if (Links.Count == 0)
        {
            return;
        }

        _copy(string.Join("\r\n", Links.Select(l => l.Url)));
        _status.Text = ExpiryText(Links) + $" Copied {(Links.Count == 1 ? "the link" : $"{Links.Count} links")} to the clipboard.";
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;

        // Do not leave the links in memory longer than needed.
        Links = [];
        _urls.Clear();
        base.OnFormClosed(e);
    }

    private async Task RegenerateAsync()
    {
        var generation = ++_generation;
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();
        var token = _cancellation.Token;
        Links = [];
        _urls.Clear();
        PrimaryButton.Enabled = false;
        ShowError(_error, _urls, null);
        _status.Text = "Generating…";
        try
        {
            var links = await _generate(Validity, token);
            if (generation != _generation || IsDisposed)
            {
                return;
            }

            Links = links;
            _urls.Text = string.Join("\r\n", links.Select(l => l.Url));
            _status.Text = ExpiryText(links);
            PrimaryButton.Enabled = links.Count > 0;
        }
        catch (StorageOperationException ex) when (generation == _generation && !IsDisposed)
        {
            _status.Text = string.Empty;
            ShowError(_error, _urls, ex.Message);
        }
        catch (OperationCanceledException)
        {
            // Another validity was chosen, or the dialog closed.
        }
    }

    private static string ExpiryText(IReadOnlyList<PresignedUrl> links)
    {
        if (links.Count == 0)
        {
            return string.Empty;
        }

        var expires = links.Min(l => l.ExpiresAt).ToLocalTime();
        var text = $"Expires {expires:yyyy-MM-dd HH:mm} (this computer's time).";
        if (links.Any(l => l.UsesTemporaryCredentials))
        {
            text += " This connection uses temporary credentials: the link stops working when they expire, which can be sooner.";
        }

        if (links.Any(l => l.Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
        {
            text += " The endpoint uses unencrypted HTTP: the link and the download can be read on the network.";
        }

        return text;
    }

    private sealed record ValidityChoice(TimeSpan Value)
    {
        public override string ToString() => Value == ShareLinks.MaxValidity
            ? "7 days (maximum)"
            : Value.TotalHours >= 1 ? $"{Value.TotalHours:0} {(Value.TotalHours == 1 ? "hour" : "hours")}" : $"{Value.TotalMinutes:0} minutes";
    }
}
