using Newtonsoft.Json;
using TexasPoker.Engine;

namespace TexasPoker.Tests;

public sealed class SecretStoreTests
{
    private const string Key = "q8Zr2Vb6YtP0nLw4XcJ1uFh7KdS9aMe3GiOy5TxBvN0";

    [Fact]
    public void A_key_is_saved_encrypted_and_reads_back()
    {
        var stored = SecretStore.Protect(Key);

        if (OperatingSystem.IsWindows())
        {
            Assert.StartsWith("dpapi:", stored);
            Assert.DoesNotContain(Key, stored);
        }

        Assert.Equal(Key, SecretStore.Unprotect(stored));
    }

    [Fact]
    public void No_key_saves_as_nothing()
    {
        Assert.Equal(string.Empty, SecretStore.Protect(string.Empty));
        Assert.Equal(string.Empty, SecretStore.Unprotect(string.Empty));
    }

    [Fact]
    public void A_key_that_cant_be_opened_here_reads_as_signed_out()
    {
        // As from another PC or Windows account: the blob doesn't open, so the dealer signs in again.
        Assert.Equal(string.Empty, SecretStore.Unprotect("dpapi:" + Convert.ToBase64String(new byte[64])));
        Assert.Equal(string.Empty, SecretStore.Unprotect("dpapi:not base64!"));
        Assert.Equal(string.Empty, SecretStore.Unprotect("something else"));
    }

    [Fact]
    public void A_key_kept_as_is_reads_back()
    {
        Assert.Equal(Key, SecretStore.Unprotect("plain:" + Key));
    }

    [Fact]
    public void An_old_plain_text_key_is_read_once_and_never_written_again()
    {
        // The same pattern as the plugin's Configuration, saved by Dalamud with Newtonsoft.Json.
        var config = JsonConvert.DeserializeObject<ConfigLike>($$"""{ "AuthSessionKey": "{{Key}}" }""")!;

        Assert.True(config.KeyNeedsSave);
        Assert.Equal(Key, config.AuthSessionKey);

        var saved = JsonConvert.SerializeObject(config);
        if (OperatingSystem.IsWindows())
            Assert.DoesNotContain(Key, saved);
        Assert.DoesNotContain("\"AuthSessionKey\"", saved);

        var reloaded = JsonConvert.DeserializeObject<ConfigLike>(saved)!;
        Assert.False(reloaded.KeyNeedsSave);
        Assert.Equal(Key, reloaded.AuthSessionKey);
    }

    private sealed class ConfigLike
    {
        [JsonIgnore]
        public string AuthSessionKey
        {
            get => authSessionKey ??= SecretStore.Unprotect(authKeyStored);
            set
            {
                authSessionKey = value;
                authKeyStored = SecretStore.Protect(value);
            }
        }

        public string AuthKeyStored
        {
            get => authKeyStored;
            set
            {
                authKeyStored = value;
                authSessionKey = null;
            }
        }

        private string authKeyStored = string.Empty;
        private string? authSessionKey;

        [JsonProperty("AuthSessionKey")]
        private string LegacyAuthSessionKey
        {
            set
            {
                if (string.IsNullOrEmpty(value) || authKeyStored.Length > 0)
                    return;
                AuthSessionKey = value;
                KeyNeedsSave = true;
            }
        }

        [JsonIgnore]
        public bool KeyNeedsSave { get; private set; }
    }
}
