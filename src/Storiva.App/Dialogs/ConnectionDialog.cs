using Storiva.App.Icons;
using Storiva.App.Theme;
using Storiva.Core.Connections;

namespace Storiva.App.Dialogs;

/// <summary>
/// Accounts › Add New Account.. / Edit: one connection to AWS S3 or to an S3-compatible endpoint (MinIO…).
/// Reading order follows the reference dialog [F S2/E3]: name, account type, credentials, secure transfer,
/// "advanced settings..". Secrets typed here are handed to the main form once (to be protected with DPAPI) and the
/// password boxes are cleared; nothing is written by this dialog itself. "Test connection" sends read-only requests.
/// </summary>
internal sealed class ConnectionDialog : StorivaDialog
{
    private static readonly (string Text, StorageProvider Provider)[] Providers =
    [
        ("Amazon S3 (AWS)", StorageProvider.AwsS3),
        ("S3-compatible server (MinIO, Ceph, Garage, …) — custom endpoint", StorageProvider.S3Compatible),
    ];

    private static readonly (string Text, AuthenticationMethod Method)[] Methods =
    [
        ("Access key ID and secret access key", AuthenticationMethod.AccessKey),
        ("Named AWS profile (~/.aws config/credentials, SSO, assume role)", AuthenticationMethod.AwsProfile),
        ("AWS default credential chain (environment, default profile, instance role)", AuthenticationMethod.DefaultChain),
    ];

    private readonly ConnectionProfile? _original;
    private readonly IReadOnlyList<string> _otherNames;
    private readonly bool _hasStoredKey;
    private readonly Func<ConnectionProfile, CredentialsDraft?, CancellationToken, Task<ConnectionTestResult>> _test;
    private readonly Func<IReadOnlyList<string>>? _listProfiles;

    private readonly TextBox _name = new() { Text = "New connection" };
    private readonly ComboBox _provider = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _endpoint = new() { PlaceholderText = "https://minio.example.internal:9000" };
    private readonly ComboBox _region = new() { DropDownStyle = ComboBoxStyle.DropDown };
    private readonly ComboBox _method = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _accessKey = new();
    private readonly TextBox _secretKey = new() { UseSystemPasswordChar = true };
    private readonly TextBox _sessionToken = new() { UseSystemPasswordChar = true, PlaceholderText = "Only for temporary credentials" };
    private readonly ComboBox _profileName = new() { DropDownStyle = ComboBoxStyle.DropDown };
    private readonly CheckBox _pathStyle;
    private readonly CheckBox _allowHttp;
    private readonly CheckBox _checksums;
    private readonly CheckBox _secure;
    private readonly Button _testButton = new() { Text = "Test connection", AutoSize = true, MinimumSize = new Size(130, 30), FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false };
    private readonly Label _testResult = new() { AutoSize = true, MaximumSize = new Size(FieldWidth, 0), Margin = new Padding(Metrics.SpaceL, Metrics.SpaceXs, 0, Metrics.SpaceS) };
    private readonly TableLayoutPanel _endpointSection;
    private readonly TableLayoutPanel _keySection;
    private readonly TableLayoutPanel _profileSection;
    private readonly TableLayoutPanel _advanced;
    private readonly LinkLabel _advancedLink;
    private readonly Label _nameError;
    private readonly Label _endpointError;
    private readonly Label _regionError;
    private readonly Label _methodError;
    private readonly Label _accessKeyError;
    private readonly Label _secretKeyError;
    private readonly Label _profileError;
    private readonly HashSet<string> _touched = [];
    private CancellationTokenSource? _testCancellation;
    private bool _advancedShown;
    private bool _loading = true;

    /// <param name="original">Connection being edited, or null to add one.</param>
    /// <param name="credentialHint">Edit mode: masked stored key ("Stored key: AKIA…WXYZ").</param>
    public ConnectionDialog(
        ConnectionProfile? original,
        IEnumerable<string> otherNames,
        bool hasStoredKey,
        string? credentialHint,
        Func<ConnectionProfile, CredentialsDraft?, CancellationToken, Task<ConnectionTestResult>> test,
        Func<IReadOnlyList<string>>? listProfiles,
        Action<string> showHelp)
        : base(
            original is null ? "Add New Account" : "Edit Connection",
            original is null ? "Connect Storiva to Amazon S3 or to an S3-compatible server." : $"Change the settings of \"{original.Name}\".",
            original is null ? Lucide.UserPlus : Lucide.Server,
            original is null ? "Add new account" : "Save changes")
    {
        _original = original;
        _otherNames = otherNames.ToList();
        _hasStoredKey = hasStoredKey;
        _test = test;
        _listProfiles = listProfiles;
        AddHeaderLink("online help", () => showHelp(HelpText));

        _nameError = AddField("Display name:", _name, "Any name for this connection.");
        foreach (var p in Providers)
        {
            _provider.Items.Add(p.Text);
        }

        AddField("Account type:", _provider, "Amazon S3, or a server that implements the S3 API at its own address.");

        _endpointSection = AddSection(visible: false);
        _endpointError = AddField("Endpoint URL:", _endpoint,
            "Scheme, host and port of the server, e.g. https://minio.example.internal:9000. HTTPS is strongly recommended.", _endpointSection);

        _region.Items.AddRange(ConnectionRules.KnownAwsRegions);
        _regionError = AddField("Region:", _region, "AWS: the region requests are signed for (buckets in other regions are found automatically). MinIO: usually us-east-1.");

        foreach (var m in Methods)
        {
            _method.Items.Add(m.Text);
        }

        _methodError = AddField("Credentials:", _method, "Prefer temporary credentials (SSO or assume-role profiles) over long-lived keys.");

        _keySection = AddSection(visible: true);
        _accessKeyError = AddField("Access Key ID:", _accessKey, hasStoredKey && credentialHint is not null
            ? credentialHint + ". Leave both key fields empty to keep it."
            : "The public part of the key pair.", _keySection);
        _secretKeyError = AddField("Secret Access Key:", _secretKey,
            "Encrypted with Windows DPAPI for your Windows user before it is saved. Never shown again, never logged.", _keySection);
        AddField("Session token (optional):", _sessionToken, "For temporary (STS) credentials; they expire.", _keySection);

        _profileSection = AddSection(visible: false);
        _profileError = AddField("AWS profile name:", _profileName,
            "A profile from the AWS CLI config/credentials files of this Windows user. Storiva saves only the name.", _profileSection);
        if (_listProfiles is not null)
        {
            var link = new LinkLabel
            {
                Text = "list profiles found on this computer",
                AutoSize = true,
                LinkColor = Palette.Link,
                ActiveLinkColor = Palette.AccentStrong,
                Margin = new Padding(Metrics.SpaceL, 0, 0, Metrics.SpaceXs),
            };
            link.LinkClicked += (_, _) => FillProfiles();
            _profileSection.Controls.Add(link);
        }

        _secure = AddCheckBox("Use secure transfer (SSL/TLS)",
            "AWS always uses HTTPS. For a custom endpoint the URL scheme decides; certificates are always validated.", initial: true);
        _secure.Enabled = false;

        _advanced = AddSection(visible: false);
        _pathStyle = AddCheckBox("Use path-style addressing (https://host/bucket/key)",
            "Needed by most MinIO servers and by bucket names with dots. Off: virtual-hosted style (https://bucket.host/key).", initial: false, _advanced);
        _allowHttp = AddCheckBox("Allow unencrypted HTTP (isolated test servers only)",
            "Lets the endpoint use http://. Data and request signatures then travel unencrypted.", initial: false, _advanced);
        _checksums = AddCheckBox("Send flexible checksums (CRC) when supported",
            "On for AWS. Some S3-compatible servers reject them; turn off if uploads fail with checksum or signature errors.", initial: true, _advanced);

        // Test connection: read-only requests only (list buckets).
        ButtonStyling.Apply(_testButton, primary: false);
        _testButton.Margin = new Padding(Metrics.SpaceL, Metrics.SpaceM, 0, 0);
        _testButton.Click += (_, _) => OnTestClick();
        Body.Controls.Add(_testButton);
        Body.Controls.Add(_testResult);

        _advancedLink = AddFooterLink("advanced settings..", ToggleAdvanced);

        LoadFrom(original);
        _provider.SelectedIndexChanged += (_, _) => OnProviderChanged();
        _method.SelectedIndexChanged += (_, _) => Touch(nameof(ConnectionProfile.Authentication));
        _name.TextChanged += (_, _) => Touch(nameof(ConnectionProfile.Name));
        _endpoint.TextChanged += (_, _) => Touch(nameof(ConnectionProfile.Endpoint));
        _region.TextChanged += (_, _) => Touch(nameof(ConnectionProfile.Region));
        _accessKey.TextChanged += (_, _) => Touch(nameof(CredentialsDraft.AccessKeyId));
        _secretKey.TextChanged += (_, _) => Touch(nameof(CredentialsDraft.SecretAccessKey));
        _profileName.TextChanged += (_, _) => Touch(nameof(ConnectionProfile.ProfileName));
        _allowHttp.CheckedChanged += (_, _) => Touch(nameof(ConnectionProfile.Endpoint));
        _loading = false;
        UpdateSections();
        Revalidate();
    }

    /// <summary>Saved settings (non-secret part).</summary>
    public ConnectionProfile? Result { get; private set; }

    /// <summary>Typed credentials, to be protected by the caller and then dropped. Null when unchanged.</summary>
    public CredentialsDraft? ResultCredentials { get; private set; }

    // ---------- smoke-test hooks (drive the same controls a user would) ----------

    internal bool CanSubmit => PrimaryButton.Enabled;

    internal bool EndpointVisible => _endpointSection.Visible || _endpointShown;

    internal bool KeyFieldsVisible => _keyShown;

    internal bool SecretIsMasked => _secretKey.UseSystemPasswordChar && _sessionToken.UseSystemPasswordChar;

    internal string VisibleErrors => string.Join(" | ",
        new[] { _nameError, _endpointError, _regionError, _methodError, _accessKeyError, _secretKeyError, _profileError }.Where(l => l.Text.Length > 0 && _errorShown.Contains(l)).Select(l => l.Text));

    internal string TestResultText => _testResult.Text;

    internal void Fill(string name, StorageProvider provider, string? endpoint, string region, AuthenticationMethod method, string accessKey, string secret, bool pathStyle = false, bool allowHttp = false)
    {
        _name.Text = name;
        _provider.SelectedIndex = Array.FindIndex(Providers, p => p.Provider == provider);
        _endpoint.Text = endpoint ?? string.Empty;
        _region.Text = region;
        _method.SelectedIndex = Array.FindIndex(Methods, m => m.Method == method);
        _accessKey.Text = accessKey;
        _secretKey.Text = secret;
        _pathStyle.Checked = pathStyle;
        _allowHttp.Checked = allowHttp;
    }

    internal void ClickSave() => OnPrimaryClick();

    internal Task RunTestAsync() => TestAsync();

    // ---------- behaviour ----------

    private bool _endpointShown;
    private bool _keyShown = true;
    private readonly HashSet<Label> _errorShown = [];

    private StorageProvider SelectedProvider => Providers[Math.Max(0, _provider.SelectedIndex)].Provider;

    private AuthenticationMethod SelectedMethod => Methods[Math.Max(0, _method.SelectedIndex)].Method;

    private void LoadFrom(ConnectionProfile? p)
    {
        p ??= new ConnectionProfile { Name = "New connection" };
        _name.Text = p.Name;
        _provider.SelectedIndex = Array.FindIndex(Providers, x => x.Provider == p.Provider);
        _endpoint.Text = p.Endpoint ?? string.Empty;
        _region.Text = p.Region;
        _method.SelectedIndex = Array.FindIndex(Methods, x => x.Method == p.Authentication);
        _profileName.Text = p.ProfileName ?? string.Empty;
        _pathStyle.Checked = p.ForcePathStyle;
        _allowHttp.Checked = p.AllowInsecureHttp;
        _checksums.Checked = p.UseFlexibleChecksums;
        if (_original is not null && _hasStoredKey)
        {
            _accessKey.PlaceholderText = "(unchanged)";
            _secretKey.PlaceholderText = "(unchanged)";
        }
    }

    private void OnProviderChanged()
    {
        if (_loading)
        {
            return;
        }

        // Sensible defaults when switching: MinIO needs path-style and usually rejects the new checksums.
        var compatible = SelectedProvider == StorageProvider.S3Compatible;
        _pathStyle.Checked = compatible;
        _checksums.Checked = !compatible;
        if (compatible && SelectedMethod == AuthenticationMethod.DefaultChain)
        {
            _method.SelectedIndex = 0;
        }

        Touch(nameof(ConnectionProfile.Provider));
    }

    private void UpdateSections()
    {
        var compatible = SelectedProvider == StorageProvider.S3Compatible;
        // Own flags: Control.Visible reads the effective visibility (false until the dialog is shown).
        _endpointShown = compatible;
        _endpointSection.Visible = compatible;
        _keyShown = SelectedMethod == AuthenticationMethod.AccessKey;
        _keySection.Visible = _keyShown;
        _profileSection.Visible = SelectedMethod == AuthenticationMethod.AwsProfile;
        _allowHttp.Enabled = compatible;
        _secure.Checked = !compatible || !_endpoint.Text.Trim().StartsWith("http://", StringComparison.OrdinalIgnoreCase);
    }

    private void FillProfiles()
    {
        try
        {
            var names = _listProfiles?.Invoke() ?? [];
            _profileName.Items.Clear();
            _profileName.Items.AddRange(names.Cast<object>().ToArray());
            ShowError(_profileError, _profileName, names.Count == 0 ? "No AWS profiles were found for this Windows user." : null);
            if (names.Count > 0)
            {
                _profileName.DroppedDown = true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowError(_profileError, _profileName, "The AWS configuration files could not be read.");
        }
    }

    private ConnectionProfile CurrentProfile() => new()
    {
        Id = _original?.Id ?? Guid.NewGuid(),
        Name = _name.Text,
        Provider = SelectedProvider,
        Endpoint = _endpoint.Text,
        Region = _region.Text,
        Authentication = SelectedMethod,
        ProfileName = _profileName.Text,
        ForcePathStyle = _pathStyle.Checked,
        AllowInsecureHttp = _allowHttp.Checked,
        UseFlexibleChecksums = _checksums.Checked,
        LastTest = _original?.LastTest,
    };

    private CredentialsDraft? CurrentCredentials() => SelectedMethod == AuthenticationMethod.AccessKey
        ? new CredentialsDraft(_accessKey.Text, _secretKey.Text, _sessionToken.Text, KeepStored: _original is not null && _hasStoredKey)
        : null;

    private void Touch(string field)
    {
        if (_loading)
        {
            return;
        }

        _touched.Add(field);
        UpdateSections();
        Revalidate();
    }

    private IReadOnlyList<ConnectionValidationError> Revalidate()
    {
        var errors = ConnectionRules.Validate(ConnectionRules.Normalize(CurrentProfile()), CurrentCredentials(), _otherNames);
        void Show(Label label, Control input, string field)
        {
            var message = _touched.Contains(field) ? errors.FirstOrDefault(e => e.Field == field)?.Message : null;
            ShowError(label, input, message);
            if (message is null)
            {
                _errorShown.Remove(label);
            }
            else
            {
                _errorShown.Add(label);
            }
        }

        Show(_nameError, _name, nameof(ConnectionProfile.Name));
        Show(_endpointError, _endpoint, nameof(ConnectionProfile.Endpoint));
        Show(_regionError, _region, nameof(ConnectionProfile.Region));
        Show(_methodError, _method, nameof(ConnectionProfile.Authentication));
        Show(_accessKeyError, _accessKey, nameof(CredentialsDraft.AccessKeyId));
        Show(_secretKeyError, _secretKey, nameof(CredentialsDraft.SecretAccessKey));
        Show(_profileError, _profileName, nameof(ConnectionProfile.ProfileName));
        if (_touched.Contains(nameof(ConnectionProfile.Endpoint)) && errors.Any(e => e.Field == nameof(ConnectionProfile.Endpoint)) && _allowHttp.Checked && !_advancedShown)
        {
            ToggleAdvanced(); // never hide the setting behind an error in a collapsed section
        }

        PrimaryButton.Enabled = errors.Count == 0;
        return errors;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _name.Focus();
        _name.SelectAll();
    }

    protected override void OnPrimaryClick()
    {
        _touched.UnionWith([nameof(ConnectionProfile.Name), nameof(ConnectionProfile.Endpoint), nameof(ConnectionProfile.Region),
            nameof(ConnectionProfile.Authentication), nameof(CredentialsDraft.AccessKeyId), nameof(CredentialsDraft.SecretAccessKey), nameof(ConnectionProfile.ProfileName)]);
        if (Revalidate().Count > 0)
        {
            return;
        }

        Result = ConnectionRules.Normalize(CurrentProfile());
        var credentials = CurrentCredentials();
        ResultCredentials = credentials is { IsEmpty: false } ? credentials : null;
        ClearSecrets();
        base.OnPrimaryClick();
    }

    private void ToggleAdvanced()
    {
        _advancedShown = !_advancedShown;
        _advanced.Visible = _advancedShown;
        _advancedLink.Text = _advancedShown ? "hide advanced settings" : "advanced settings..";
    }

    private async void OnTestClick()
    {
        if (_testCancellation is not null)
        {
            _testCancellation.Cancel();
            return;
        }

        await TestAsync();
    }

    private async Task TestAsync()
    {
        _touched.UnionWith([nameof(ConnectionProfile.Endpoint), nameof(ConnectionProfile.Region), nameof(CredentialsDraft.AccessKeyId), nameof(CredentialsDraft.SecretAccessKey), nameof(ConnectionProfile.ProfileName)]);
        var errors = Revalidate().Where(e => e.Field != nameof(ConnectionProfile.Name)).ToList();
        if (errors.Count > 0)
        {
            SetTestResult(errors[0].Message, Palette.Danger);
            return;
        }

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _testCancellation = cancellation;
        _testButton.Text = "Cancel test";
        SetTestResult("Testing… (read-only: lists buckets; nothing is created or changed)", Palette.TextSecondary);
        try
        {
            var result = await _test(ConnectionRules.Normalize(CurrentProfile()), CurrentCredentials(), cancellation.Token);
            var color = result.Status switch
            {
                ConnectionStatus.Connected => Palette.Success,
                ConnectionStatus.ConnectedLimited or ConnectionStatus.Canceled => Palette.Warning,
                _ => Palette.Danger,
            };
            var duration = result.Duration is { } d ? $" ({d.TotalSeconds:0.0} s)" : string.Empty;
            SetTestResult($"{ConnectionStatusText.Label(result.Status)}{duration}: {result.Summary}", color);
        }
        finally
        {
            _testCancellation = null;
            if (!IsDisposed)
            {
                _testButton.Text = "Test connection";
            }
        }
    }

    private void SetTestResult(string text, Color color)
    {
        if (IsDisposed)
        {
            return;
        }

        _testResult.Text = text;
        _testResult.ForeColor = color;
        _testResult.AccessibleName = "Test result: " + text;
    }

    private void ClearSecrets()
    {
        _secretKey.Text = string.Empty;
        _sessionToken.Text = string.Empty;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _testCancellation?.Cancel();
        ClearSecrets();
        base.OnFormClosed(e);
    }

    private const string HelpText =
        "Add New Account / Edit Connection\r\n\r\n" +
        "• Amazon S3: choose the region and how to authenticate. A named AWS profile (SSO, assume role) or the default " +
        "chain avoids storing long-lived keys in Storiva.\r\n" +
        "• S3-compatible (MinIO…): enter the endpoint URL with its scheme, e.g. https://minio.example.internal:9000, the " +
        "region the server expects (MinIO: us-east-1) and the access key. Most MinIO servers need path-style addressing " +
        "(advanced settings).\r\n" +
        "• Test connection only lists buckets: nothing is created, changed or deleted.\r\n" +
        "• Secrets are encrypted with Windows DPAPI for your Windows user in %LOCALAPPDATA%\\Storiva\\secrets; the other " +
        "settings are in %LOCALAPPDATA%\\Storiva\\connections.json.\r\n" +
        "• TLS certificates are always validated. For a private certificate authority, install its certificate in " +
        "Windows (see docs/CONNECTION_SETUP.md).";
}
