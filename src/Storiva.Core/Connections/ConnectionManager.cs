using System.Diagnostics;
using Storiva.Core.Remote;
using Storiva.Core.Settings;

namespace Storiva.Core.Connections;

/// <summary>Persistence of the non-secret connection list.</summary>
public interface IConnectionStore
{
    string Location { get; }

    LoadResult<IReadOnlyList<ConnectionProfile>> Load();

    void Save(IReadOnlyList<ConnectionProfile> connections);
}

public sealed class InMemoryConnectionStore : IConnectionStore
{
    private IReadOnlyList<ConnectionProfile> _connections;

    public InMemoryConnectionStore(IEnumerable<ConnectionProfile>? initial = null) => _connections = initial?.ToList() ?? [];

    public string Location => "(memory)";

    public LoadResult<IReadOnlyList<ConnectionProfile>> Load() => LoadResult<IReadOnlyList<ConnectionProfile>>.Ok(_connections);

    public void Save(IReadOnlyList<ConnectionProfile> connections) => _connections = connections.ToList();
}

/// <summary>
/// Saved connections, their protected secrets and their run-time state. Never connects on its own: every
/// network request starts from an explicit user action (Test, Connect).
/// </summary>
public sealed class ConnectionManager
{
    private readonly IConnectionStore _store;
    private readonly ISecretStore _secrets;
    private readonly IObjectStorageClientFactory _factory;
    private readonly Func<DateTimeOffset> _clock;
    private readonly List<ConnectionProfile> _connections;
    private readonly Dictionary<Guid, ConnectionStatus> _runtime = new();

    public ConnectionManager(IConnectionStore store, ISecretStore secrets, IObjectStorageClientFactory factory, Func<DateTimeOffset>? clock = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _clock = clock ?? (() => DateTimeOffset.Now);

        var loaded = store.Load();
        _connections = loaded.Value.ToList();
        LoadWarnings = loaded.Warnings;
        IsReadOnly = loaded.ReadOnly;
    }

    public event EventHandler? Changed;

    public IReadOnlyList<ConnectionProfile> Connections => _connections;

    /// <summary>Problems found while loading (corrupt file moved aside, unknown entries skipped…).</summary>
    public IReadOnlyList<string> LoadWarnings { get; }

    /// <summary>True when the file was written by a newer Storiva: it is not overwritten.</summary>
    public bool IsReadOnly { get; }

    public string StoreLocation => _store.Location;

    public string SecretStoreDescription => _secrets.Description;

    public ConnectionProfile? Find(Guid id) => _connections.FirstOrDefault(c => c.Id == id);

    /// <summary>Run-time state when known in this session, otherwise the last persisted test, otherwise NotTested.</summary>
    public ConnectionStatus StatusOf(Guid id) =>
        _runtime.TryGetValue(id, out var status) ? status : Find(id)?.LastTest?.Status ?? ConnectionStatus.NotTested;

    public void SetRuntimeStatus(Guid id, ConnectionStatus status)
    {
        _runtime[id] = status;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool HasStoredCredentials(Guid id) => _secrets.Exists(id);

    /// <summary>Masked key ID for the edit dialog ("AKIA…WXYZ"), or a safe explanation when unavailable.</summary>
    public string CredentialHint(Guid id)
    {
        var result = _secrets.Load(id);
        return result.Status == SecretLoadStatus.Loaded ? "Stored key: " + result.Credentials!.MaskedAccessKeyId : result.Message ?? "No stored credentials.";
    }

    /// <summary>
    /// Adds or replaces a connection. Secrets are written first (to the secret store only); when the method no longer
    /// uses an access key, the stored secret is deleted.
    /// </summary>
    /// <exception cref="ArgumentException">Validation failed (message lists the problems).</exception>
    /// <exception cref="InvalidOperationException">The connections file is read-only (written by a newer version).</exception>
    public ConnectionProfile Save(ConnectionProfile profile, CredentialsDraft? credentials)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (IsReadOnly)
        {
            throw new InvalidOperationException("The connections file was written by a newer version of Storiva and is not modified by this version.");
        }

        profile = ConnectionRules.Normalize(profile);
        var others = _connections.Where(c => c.Id != profile.Id).Select(c => c.Name);
        var errors = ConnectionRules.Validate(profile, credentials, others).ToList();
        var isNewSecret = credentials is not null && !credentials.IsEmpty;
        if (profile.Authentication == AuthenticationMethod.AccessKey && !isNewSecret && !_secrets.Exists(profile.Id))
        {
            errors.Add(new(nameof(CredentialsDraft.AccessKeyId), "Enter the access key ID and secret access key."));
        }

        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(Environment.NewLine, errors.Select(e => e.Message)));
        }

        if (profile.Authentication == AuthenticationMethod.AccessKey && isNewSecret)
        {
            _secrets.Save(profile.Id, credentials!.ToCredentials());
        }
        else if (profile.Authentication != AuthenticationMethod.AccessKey)
        {
            _secrets.Delete(profile.Id);
        }

        var index = _connections.FindIndex(c => c.Id == profile.Id);
        if (index >= 0)
        {
            // A changed configuration invalidates the previous test result.
            var previous = _connections[index];
            var keepTest = previous with { LastTest = null, Name = profile.Name } == profile with { LastTest = null } && !isNewSecret;
            profile = profile with { LastTest = keepTest ? previous.LastTest : null };
            _connections[index] = profile;
            _runtime.Remove(profile.Id);
        }
        else
        {
            _connections.Add(profile with { LastTest = null });
        }

        Persist();
        return Find(profile.Id)!;
    }

    /// <summary>Removes the connection and its protected secret. Remote data is never touched.</summary>
    public bool Remove(Guid id)
    {
        if (IsReadOnly)
        {
            throw new InvalidOperationException("The connections file was written by a newer version of Storiva and is not modified by this version.");
        }

        var removed = _connections.RemoveAll(c => c.Id == id) > 0;
        _secrets.Delete(id);
        _runtime.Remove(id);
        if (removed)
        {
            Persist();
        }

        return removed;
    }

    /// <summary>Opens a client for a saved connection, reading its credentials from the secret store.</summary>
    /// <exception cref="StorageOperationException">Unknown connection or credentials not available.</exception>
    public IObjectStorageClient OpenClient(Guid id)
    {
        var profile = Find(id) ?? throw new StorageOperationException(new StorageError(StorageErrorKind.InvalidRequest, "The connection no longer exists."));
        return _factory.Create(profile, ResolveStoredCredentials(profile));
    }

    /// <summary>
    /// Tests a draft from the connection dialog (not necessarily saved). Typed credentials win; empty fields of an
    /// existing connection fall back to the stored ones. Only read requests are sent.
    /// </summary>
    public async Task<ConnectionTestResult> TestAsync(ConnectionProfile draft, CredentialsDraft? credentials, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        draft = ConnectionRules.Normalize(draft);
        var errors = ConnectionRules.Validate(draft, credentials, []).Where(e => e.Field != nameof(ConnectionProfile.Name)).ToList();
        if (errors.Count > 0)
        {
            return new ConnectionTestResult(ConnectionStatus.ConfigurationError, string.Join(" ", errors.Select(e => e.Message)));
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            StorageCredentials? creds = null;
            if (draft.Authentication == AuthenticationMethod.AccessKey)
            {
                creds = credentials is { IsEmpty: false } ? credentials.ToCredentials() : ResolveStoredCredentials(draft);
            }

            using var client = _factory.Create(draft, creds);
            var result = await client.TestConnectionAsync(cancellationToken).ConfigureAwait(false);
            return result with { Duration = stopwatch.Elapsed };
        }
        catch (StorageOperationException ex)
        {
            return new ConnectionTestResult(ConnectionStatusText.FromError(ex.Kind), ex.Error.Describe(), Error: ex.Error, Duration: stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            return new ConnectionTestResult(ConnectionStatus.Canceled, "The test was canceled.", Duration: stopwatch.Elapsed);
        }
    }

    /// <summary>Tests a saved connection and records the outcome (status, time, safe summary).</summary>
    public async Task<ConnectionTestResult> TestSavedAsync(Guid id, CancellationToken cancellationToken)
    {
        var profile = Find(id) ?? throw new ArgumentException("Unknown connection.", nameof(id));
        SetRuntimeStatus(id, ConnectionStatus.Connecting);
        var result = await TestAsync(profile, null, cancellationToken).ConfigureAwait(true);
        RecordTest(id, result);
        return result;
    }

    /// <summary>Stores the outcome of a test (or a connect attempt) for the connection.</summary>
    public void RecordTest(Guid id, ConnectionTestResult result)
    {
        var index = _connections.FindIndex(c => c.Id == id);
        if (index < 0)
        {
            return;
        }

        _runtime.Remove(id);
        if (result.Status != ConnectionStatus.Canceled)
        {
            _connections[index] = _connections[index] with { LastTest = new ConnectionTestRecord(result.Status, _clock(), Truncate(result.Summary, 300)) };
            if (!IsReadOnly)
            {
                Persist();
                return;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Null for methods that do not use stored keys (AWS profile, default chain).</summary>
    private StorageCredentials? ResolveStoredCredentials(ConnectionProfile profile)
    {
        if (profile.Authentication != AuthenticationMethod.AccessKey)
        {
            return null;
        }

        var result = _secrets.Load(profile.Id);
        return result.Status == SecretLoadStatus.Loaded
            ? result.Credentials!
            : throw new StorageOperationException(new StorageError(StorageErrorKind.CredentialsUnavailable,
                result.Message ?? "The stored credentials could not be read. Edit the connection and enter them again."));
    }

    private void Persist()
    {
        _store.Save(_connections);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
