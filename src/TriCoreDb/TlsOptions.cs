using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace TriCoreDb;

/// <summary>
/// TLS settings for <see cref="TriCoreClient"/>. Mirrors the Rust client's <c>TlsOptions</c>
/// (crates/tricore_client/src/tls.rs).
///
/// <para>Passing a <see cref="TlsOptions"/> at all is what turns TLS on. Once on, the server
/// certificate is verified <b>and</b> its name is checked, unless
/// <see cref="DangerAcceptInvalidCerts"/> is set.</para>
///
/// <code>
/// var tls = new TlsOptions { CaFile = "/etc/tricore/ca.pem", ServerName = "db.internal" };
/// await using var db = await TriCoreClient.ConnectAsync("db.internal", 8427, "admin", "pw", tls: tls);
/// </code>
///
/// <para><b>On the validation callback.</b> A custom
/// <see cref="RemoteCertificateValidationCallback"/> is the usual way a .NET app accidentally
/// disables TLS: <c>(_, _, _, _) =&gt; true</c> is one line, compiles, and silently accepts
/// every certificate on earth. The callback here exists for the opposite reason — .NET
/// validates against the OS trust store by default, and this driver's contract is a caller-supplied
/// CA (<see cref="CaFile"/>) with an <i>empty</i> store when none is given, matching Rust's empty
/// <c>RootCertStore</c>. The callback therefore validates <i>more</i> narrowly than the default,
/// never less, and it re-checks the name mismatch flag itself rather than assuming.</para>
///
/// <para><b>Secret hygiene.</b> Private key contents are never logged or placed in an exception
/// message. On failure the <i>path</i> is reported so an operator can find the file, never the
/// bytes in it.</para>
/// </summary>
public sealed class TlsOptions
{
    /// <summary>
    /// PEM CA bundle used to verify the server. When null the trust store is left <b>empty</b>
    /// rather than falling back to the OS roots, so a typo'd path fails closed instead of
    /// quietly succeeding against some unrelated public CA.
    /// </summary>
    public string? CaFile { get; init; }

    /// <summary>Expected server name: used for SNI and for the certificate name check.</summary>
    public string ServerName { get; init; } = "localhost";

    /// <summary>
    /// <b>Development only.</b> Accept any server certificate without verifying it, and skip the
    /// name check. A connection with this set looks encrypted but authenticates nothing, so an
    /// active attacker can sit in the middle undetected — strictly worse than visibly using plain
    /// TCP. Never enable it against a real server.
    /// </summary>
    public bool DangerAcceptInvalidCerts { get; init; }

    /// <summary>PEM client certificate chain to present (mTLS). Requires <see cref="ClientKeyFile"/>.</summary>
    public string? ClientCertFile { get; init; }

    /// <summary>PEM private key for <see cref="ClientCertFile"/> (mTLS).</summary>
    public string? ClientKeyFile { get; init; }

    /// <summary>
    /// Layer TLS over a connected socket and complete the handshake before any frame is
    /// written, so HELLO and the AUTH secret travel inside the session rather than ahead of it.
    /// </summary>
    internal async Task<SslStream> WrapAsync(NetworkStream inner, CancellationToken cancellationToken)
    {
        if ((ClientCertFile is null) != (ClientKeyFile is null))
        {
            var missing = ClientKeyFile is null ? nameof(ClientKeyFile) : nameof(ClientCertFile);
            throw new TriCoreException(
                $"tls {missing} is required alongside the other (both are needed for mTLS)");
        }

        var roots = LoadRoots();
        var clientCerts = LoadClientIdentity();

        // leaveInnerStreamOpen: false — disposing the SslStream must take the socket
        // with it, or a failed handshake leaks the connection.
        var ssl = new SslStream(inner, leaveInnerStreamOpen: false, ValidateCertificate(roots));
        try
        {
            await ssl.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = ServerName, // drives SNI and the name-mismatch flag
                    ClientCertificates = clientCerts,
                    EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12
                                          | System.Security.Authentication.SslProtocols.Tls13,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not TriCoreException)
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw new TriCoreException($"tls handshake with `{ServerName}` failed: {e.Message}", e);
        }
        return ssl;
    }

    /// <summary>
    /// Build the certificate validation callback. Returns a callback that accepts everything
    /// only when <see cref="DangerAcceptInvalidCerts"/> is set.
    /// </summary>
    private RemoteCertificateValidationCallback ValidateCertificate(X509Certificate2Collection roots)
    {
        if (DangerAcceptInvalidCerts)
        {
            // Development-only: deliberately verifies nothing. Reachable only through the
            // loudly-named property above.
            return static (_, _, _, _) => true;
        }

        return (_, certificate, chain, sslPolicyErrors) =>
        {
            if (certificate is null || chain is null)
                return false;

            // The name check is .NET's, but acting on it is ours: this flag is set
            // independently of chain building, so ignoring it would accept a valid
            // certificate issued for an entirely different host.
            if (sslPolicyErrors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch)
                || sslPolicyErrors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
            {
                return false;
            }

            // Re-build the chain against CaFile alone. CustomRootTrust replaces the machine
            // trust store outright, which is what makes an absent CaFile mean "trust nothing"
            // rather than "trust every public CA".
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Clear();
            chain.ChainPolicy.CustomTrustStore.AddRange(roots);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

            return chain.Build(new X509Certificate2(certificate));
        };
    }

    private X509Certificate2Collection LoadRoots()
    {
        var roots = new X509Certificate2Collection();
        if (CaFile is null)
            return roots; // empty == trust nothing, by design

        try
        {
            roots.ImportFromPemFile(CaFile);
        }
        catch (Exception e)
        {
            throw new TriCoreException($"tls CaFile `{CaFile}`: {e.Message}", e);
        }
        if (roots.Count == 0)
            throw new TriCoreException($"tls CaFile `{CaFile}` has no PEM certificates");
        return roots;
    }

    private X509Certificate2Collection? LoadClientIdentity()
    {
        if (ClientCertFile is null)
            return null;

        X509Certificate2 cert;
        try
        {
            cert = X509Certificate2.CreateFromPemFile(ClientCertFile, ClientKeyFile);
        }
        catch (Exception e)
        {
            // e names the paths (CreateFromPemFile takes filenames), never the key bytes.
            throw new TriCoreException(
                $"tls client identity (cert `{ClientCertFile}`, key `{ClientKeyFile}`): {e.Message}", e);
        }

        // On Windows, SChannel cannot use the ephemeral private key that CreateFromPemFile
        // produces — client auth silently presents no certificate, which surfaces much later
        // as an opaque server-side rejection. Round-tripping through a PKCS#12 export attaches
        // the key in a form SChannel accepts. No-op cost elsewhere.
        if (OperatingSystem.IsWindows())
        {
            var ephemeral = cert;
            cert = new X509Certificate2(ephemeral.Export(X509ContentType.Pfx));
            ephemeral.Dispose();
        }

        return new X509Certificate2Collection(cert);
    }
}
