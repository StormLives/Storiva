using Storiva.Core.Connections;
using Storiva.Core.Remote;
using Storiva.Tests.Fakes;

namespace Storiva.Tests;

public class ConnectionRulesTests
{
    private static ConnectionProfile Compatible(string endpoint, bool allowHttp = false) => new()
    {
        Name = "MinIO",
        Provider = StorageProvider.S3Compatible,
        Endpoint = endpoint,
        Region = "us-east-1",
        Authentication = AuthenticationMethod.AccessKey,
        AllowInsecureHttp = allowHttp,
    };

    private static readonly CredentialsDraft Keys = new("EXAMPLEKEYID", "example-secret-value", string.Empty);

    [Theory]
    [InlineData("https://minio.example.internal:9000", false, true)]
    [InlineData("https://minio.example.internal:9000/", false, true)]
    [InlineData("minio.example.internal:9000", false, false)]          // no scheme
    [InlineData("ftp://minio.example.internal", false, false)]
    [InlineData("http://minio.example.internal:9000", false, false)]   // http needs explicit consent
    [InlineData("http://minio.example.internal:9000", true, true)]
    [InlineData("https://user:pass@minio.example.internal", false, false)]
    [InlineData("https://minio.example.internal/bucket", false, false)]
    [InlineData("https://minio.example.internal/?x=1", false, false)]
    [InlineData("", false, false)]
    public void Endpoint_requires_an_explicit_scheme_and_prefers_https(string endpoint, bool allowHttp, bool valid) =>
        Assert.Equal(valid, ConnectionRules.Validate(ConnectionRules.Normalize(Compatible(endpoint, allowHttp)), Keys, []).All(e => e.Field != nameof(ConnectionProfile.Endpoint)));

    [Theory]
    [InlineData("us-east-1", true)]
    [InlineData("eu-west-3", true)]
    [InlineData("my-minio-region", true)]
    [InlineData("", false)]
    [InlineData("US-EAST-1", false)]
    [InlineData("-bad", false)]
    [InlineData("us east 1", false)]
    public void Region_format(string region, bool valid) => Assert.Equal(valid, ConnectionRules.ValidateRegion(region) is null);

    [Fact]
    public void Default_chain_is_refused_for_third_party_endpoints()
    {
        var profile = Compatible("https://minio.example.internal") with { Authentication = AuthenticationMethod.DefaultChain };
        Assert.Contains(ConnectionRules.Validate(profile, null, []), e => e.Field == nameof(ConnectionProfile.Authentication));
        Assert.DoesNotContain(ConnectionRules.Validate(new ConnectionProfile { Name = "AWS", Authentication = AuthenticationMethod.DefaultChain }, null, []),
            e => e.Field == nameof(ConnectionProfile.Authentication));
    }

    [Fact]
    public void Access_keys_are_required_unless_stored_ones_are_kept()
    {
        var profile = new ConnectionProfile { Name = "AWS" };
        Assert.Contains(ConnectionRules.Validate(profile, new CredentialsDraft("", "", ""), []), e => e.Field == nameof(CredentialsDraft.AccessKeyId));
        Assert.Contains(ConnectionRules.Validate(profile, new CredentialsDraft("KEY", "", ""), []), e => e.Field == nameof(CredentialsDraft.SecretAccessKey));
        Assert.Empty(ConnectionRules.Validate(profile, new CredentialsDraft("", "", "", KeepStored: true), []));
        Assert.Contains(ConnectionRules.Validate(profile, new CredentialsDraft("KEY ID", "s", ""), []), e => e.Field == nameof(CredentialsDraft.AccessKeyId));
    }

    [Fact]
    public void Names_must_be_unique_and_profile_names_valid()
    {
        Assert.Contains(ConnectionRules.Validate(new ConnectionProfile { Name = " aws " }, Keys, ["AWS"]), e => e.Field == nameof(ConnectionProfile.Name));
        var profile = new ConnectionProfile { Name = "p", Authentication = AuthenticationMethod.AwsProfile, ProfileName = "with space" };
        Assert.Contains(ConnectionRules.Validate(profile, null, []), e => e.Field == nameof(ConnectionProfile.ProfileName));
        Assert.Empty(ConnectionRules.Validate(profile with { ProfileName = "dev-sso" }, null, []));
    }

    [Fact]
    public void Normalize_trims_and_drops_fields_that_do_not_apply()
    {
        var aws = ConnectionRules.Normalize(new ConnectionProfile { Name = " a ", Endpoint = "https://x", AllowInsecureHttp = true, ProfileName = "p" });
        Assert.Equal(("a", (string?)null, false, (string?)null), (aws.Name, aws.Endpoint, aws.AllowInsecureHttp, aws.ProfileName));
        var minio = ConnectionRules.Normalize(Compatible(" https://m.example.internal:9000/ "));
        Assert.Equal("https://m.example.internal:9000", minio.Endpoint);
    }

    [Fact]
    public void Profiles_have_no_secret_fields()
    {
        var names = typeof(ConnectionProfile).GetProperties().Select(p => p.Name.ToLowerInvariant()).ToList();
        Assert.DoesNotContain(names, n => n.Contains("secret") || n.Contains("password") || n.Contains("token") || n.Contains("accesskey"));
    }

    [Theory]
    [InlineData("https://minio.example.internal:9000", "https://minio.example.internal:9000")]
    [InlineData("https://AKIAEXAMPLE:s3cr3t@minio.example.internal:9000/bucket?X-Amz-Signature=abc#f", "https://minio.example.internal:9000")]
    [InlineData("http://user@10.0.0.5/", "http://10.0.0.5")]
    [InlineData("https://[::1]:9443", "https://[::1]:9443")]
    [InlineData("not a url", "(no endpoint)")]
    [InlineData(null, "(no endpoint)")]
    public void Endpoint_label_shows_only_scheme_host_and_port(string? endpoint, string expected) =>
        Assert.Equal(expected, ConnectionProfile.SafeEndpointLabel(endpoint));
}

public class SecretHandlingTests
{
    [Fact]
    public void Credentials_and_drafts_never_print_their_values()
    {
        var credentials = new StorageCredentials("AKIAEXAMPLEKEY123456", "s3cr3t/value+with/slashes0000000000000000", "token-value");
        Assert.DoesNotContain("s3cr3t", credentials.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("token-value", credentials.ToString(), StringComparison.Ordinal);
        Assert.Equal("AKIA…3456", credentials.MaskedAccessKeyId);
        Assert.DoesNotContain("secret-x", new CredentialsDraft("k", "secret-x", "").ToString(), StringComparison.Ordinal);
        Assert.Equal("••••", StorageCredentials.Mask("abcd"));
    }

    [Fact]
    public void Redactor_removes_known_values_and_key_shapes()
    {
        var credentials = new StorageCredentials("MYKEYID1", "my-very-secret-value", "session-token-value");
        var text = "failed for MYKEYID1 with my-very-secret-value and session-token-value; also AKIAABCDEFGHIJKLMNOP and " +
                   "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY and X-Amz-Credential=abc/def Signature=0123abcd";
        var redacted = SecretRedactor.Redact(text, credentials);
        foreach (var secret in new[] { "MYKEYID1", "my-very-secret-value", "session-token-value", "AKIAABCDEFGHIJKLMNOP", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY", "abc/def", "0123abcd" })
        {
            Assert.DoesNotContain(secret, redacted, StringComparison.Ordinal);
        }

        Assert.Contains(SecretRedactor.Placeholder, redacted, StringComparison.Ordinal);
        Assert.Equal(string.Empty, SecretRedactor.Redact(null));
    }
}

public class ConnectionManagerTests
{
    private static readonly CredentialsDraft Keys = new("EXAMPLEKEYID", "example-secret-value", string.Empty);

    private static (ConnectionManager Manager, InMemoryConnectionStore Store, InMemorySecretStore Secrets, FakeClientFactory Factory) Create(IObjectStorageClient? client = null)
    {
        var store = new InMemoryConnectionStore();
        var secrets = new InMemorySecretStore();
        var factory = new FakeClientFactory(client is null ? null : (_, _) => client);
        return (new ConnectionManager(store, secrets, factory, () => new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero)), store, secrets, factory);
    }

    [Fact]
    public void Save_keeps_secrets_out_of_the_profile_store()
    {
        var (manager, store, secrets, _) = Create();
        var saved = manager.Save(new ConnectionProfile { Name = "AWS prod" }, Keys);
        Assert.True(secrets.Exists(saved.Id));
        var persisted = Assert.Single(store.Load().Value);
        Assert.Equal("AWS prod", persisted.Name);
        Assert.Equal("Stored key: EXAM…EYID", manager.CredentialHint(saved.Id));
    }

    [Fact]
    public void Save_rejects_invalid_drafts_and_missing_keys()
    {
        var (manager, _, _, _) = Create();
        Assert.Throws<ArgumentException>(() => manager.Save(new ConnectionProfile { Name = "" }, Keys));
        Assert.Throws<ArgumentException>(() => manager.Save(new ConnectionProfile { Name = "no keys" }, null));
    }

    [Fact]
    public void Editing_with_empty_key_fields_keeps_the_stored_secret()
    {
        var (manager, _, secrets, _) = Create();
        var saved = manager.Save(new ConnectionProfile { Name = "AWS" }, Keys);
        manager.Save(saved with { Region = "eu-west-1" }, new CredentialsDraft("", "", "", KeepStored: true));
        Assert.Equal("EXAMPLEKEYID", secrets.Load(saved.Id).Credentials!.AccessKeyId);
        Assert.Equal("eu-west-1", manager.Find(saved.Id)!.Region);
    }

    [Fact]
    public void Switching_to_a_profile_deletes_the_stored_key_and_remove_deletes_everything()
    {
        var (manager, _, secrets, _) = Create();
        var saved = manager.Save(new ConnectionProfile { Name = "AWS" }, Keys);
        manager.Save(saved with { Authentication = AuthenticationMethod.AwsProfile, ProfileName = "dev" }, null);
        Assert.False(secrets.Exists(saved.Id));

        var other = manager.Save(new ConnectionProfile { Name = "Other" }, Keys);
        Assert.True(manager.Remove(other.Id));
        Assert.False(secrets.Exists(other.Id));
        Assert.Null(manager.Find(other.Id));
    }

    [Fact]
    public async Task Test_results_are_recorded_and_a_changed_configuration_clears_them()
    {
        var (manager, store, _, _) = Create();
        var saved = manager.Save(new ConnectionProfile { Name = "AWS" }, Keys);
        var result = await manager.TestSavedAsync(saved.Id, CancellationToken.None);
        Assert.Equal(ConnectionStatus.Connected, result.Status);
        Assert.Equal(ConnectionStatus.Connected, manager.StatusOf(saved.Id));
        Assert.NotNull(Assert.Single(store.Load().Value).LastTest);

        manager.Save(manager.Find(saved.Id)! with { Name = "AWS renamed" }, null);
        Assert.NotNull(manager.Find(saved.Id)!.LastTest); // a rename keeps the result
        manager.Save(manager.Find(saved.Id)! with { Region = "eu-west-1" }, null);
        Assert.Null(manager.Find(saved.Id)!.LastTest);    // a different region does not
        Assert.Equal(ConnectionStatus.NotTested, manager.StatusOf(saved.Id));
    }

    [Fact]
    public async Task Test_failures_map_to_connection_states_and_never_throw()
    {
        var client = new InMemoryObjectStorageClient { FailEverything = StorageErrorKind.Tls };
        var (manager, _, _, _) = Create(client);
        var result = await manager.TestAsync(new ConnectionProfile { Name = "x" }, Keys, CancellationToken.None);
        Assert.Equal(ConnectionStatus.TlsError, result.Status);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Testing_an_invalid_draft_sends_nothing()
    {
        var (manager, _, _, factory) = Create();
        var result = await manager.TestAsync(new ConnectionProfile { Name = "x", Provider = StorageProvider.S3Compatible, Endpoint = "no-scheme" }, Keys, CancellationToken.None);
        Assert.Equal(ConnectionStatus.ConfigurationError, result.Status);
        Assert.Empty(factory.Created);
    }

    [Fact]
    public void Unreadable_secrets_give_a_safe_credentials_error()
    {
        var store = new InMemoryConnectionStore();
        var manager = new ConnectionManager(store, new UndecryptableSecrets(), new FakeClientFactory());
        var saved = manager.Save(new ConnectionProfile { Name = "AWS" }, Keys);
        var ex = Assert.Throws<StorageOperationException>(() => manager.OpenClient(saved.Id));
        Assert.Equal(StorageErrorKind.CredentialsUnavailable, ex.Kind);
        Assert.Contains("DPAPI", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_read_only_store_is_never_overwritten()
    {
        var manager = new ConnectionManager(new ReadOnlyStore(), new InMemorySecretStore(), new FakeClientFactory());
        Assert.True(manager.IsReadOnly);
        Assert.Throws<InvalidOperationException>(() => manager.Save(new ConnectionProfile { Name = "x" }, Keys));
    }

    private sealed class UndecryptableSecrets : ISecretStore
    {
        public string Description => "test";

        public void Save(Guid connectionId, StorageCredentials credentials)
        {
        }

        public SecretLoadResult Load(Guid connectionId) => new(SecretLoadStatus.Undecryptable, null, "Cannot be decrypted (DPAPI: other user or computer).");

        public bool Exists(Guid connectionId) => true;

        public bool Delete(Guid connectionId) => true;
    }

    private sealed class ReadOnlyStore : IConnectionStore
    {
        public string Location => "test";

        public Core.Settings.LoadResult<IReadOnlyList<ConnectionProfile>> Load() => new([], ["newer"], ReadOnly: true);

        public void Save(IReadOnlyList<ConnectionProfile> connections) => throw new InvalidOperationException("read-only");
    }
}
