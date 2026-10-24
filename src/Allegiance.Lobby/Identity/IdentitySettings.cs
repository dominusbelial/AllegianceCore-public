namespace Allegiance.Lobby.Identity;

public sealed class IdentitySettings
{
    public string PublicUrl { get; set; } = "http://localhost:5000";
    public string Store { get; set; } = "Sqlite";
    public string ConnectionString { get; set; } = "Data Source=.identity/accounts.db";
    public string SigningKeyFile { get; set; } = ".identity/signing-key.pem";
    public string PublicKeyFile { get; set; } = ".identity/public-key.pem";
    public string[] OperatorAccountIds { get; set; } = [];
    public ProviderSettings GitHub { get; set; } = new();
    public ProviderSettings Google { get; set; } = new();
    public ProviderSettings Steam { get; set; } = new();

    public void Validate()
    {
        var url = new Uri(PublicUrl, UriKind.Absolute);
        if ((url.Scheme != "https" && !(url.Scheme == "http" && url.IsLoopback))
            || url.AbsolutePath != "/" || url.Query.Length != 0 || url.Fragment.Length != 0 || url.UserInfo.Length != 0)
            throw new InvalidOperationException("Identity PublicUrl must be an HTTPS origin (HTTP localhost for development)");
        PublicUrl = url.GetLeftPart(UriPartial.Authority);
        if (Store is not "Sqlite" and not "Postgres") throw new InvalidOperationException("Identity Store must be Sqlite or Postgres");
    }
}

public sealed class ProviderSettings
{
    public bool Enabled { get; set; }
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
}
