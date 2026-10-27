using System.Net.Http.Headers;
using System.Text;

using PowerOps.Core.Deploy;
using PowerOps.Core.Drift;

namespace PowerOps.Cli;

/// <summary>Reads one solution in one live environment: <c>pac org who</c> for the name and URL, <c>az</c> for a token, then the Web API for reads 2 to 7. Nothing here formats a <see cref="PacResult"/> or puts the token, the URL or the <c>--env</c> value in a message.</summary>
internal sealed class DriftLiveReader(
    Func<IReadOnlyList<string>, TimeSpan, PacResult> pac,
    Func<IReadOnlyList<string>, TimeSpan, PacResult> az,
    Func<Uri, string, TimeSpan, WebResult> web)
{
    private const int MaxLayerReads = 50;
    private const long MaxBody = 16 * 1024 * 1024;
    private const string SignInHint = "Sign in with az login, and check that the account can read that environment.";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    internal static string AzExecutable(bool windows) => windows ? "az.cmd" : "az";

    public Snapshot Read(string solution, string env)
    {
        var (name, url, _) = Who(env);
        return ReadSolution(name, url, Token(url), solution);
    }

    /// <summary>Reads a deploy target: one <c>pac org who</c> and one token, then every solution in full and each dependency by its <c>solution</c> row only (version, or null when not installed).</summary>
    public DeployTarget ReadTarget(string target, IReadOnlyList<string> solutions, IReadOnlyList<string> dependencies)
    {
        var (name, url, idText) = Who(target);
        if (!Guid.TryParse(idText, out var id))
        {
            throw new DriftReadException("`pac org who` printed no environment id powerops can read.");
        }

        var token = Token(url);
        var snapshots = solutions.Select(s => ReadSolution(name, url, token, s)).ToList();
        var versions = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in dependencies)
        {
            versions[d] = Read(url, token, "solution", "solutions", DriftQueries.Solution(d), DriftParsers.Solution)?.Version;
        }

        return new DeployTarget(name, id, snapshots, versions);
    }

    private Snapshot ReadSolution(string name, string url, string token, string solution)
    {
        var found = Read(url, token, "solution", "solutions", DriftQueries.Solution(solution), DriftParsers.Solution);
        if (found is not { } sol)
        {
            return new Snapshot(name, solution, false, null, null, true, [], [], [], []);
        }

        var components = Read(url, token, "solutioncomponent", "solutioncomponents", DriftQueries.Components(sol.Id), DriftParsers.Components);
        var flows = Read(url, token, "workflow", "workflows", DriftQueries.Flows(sol.Id), DriftParsers.Flows);
        var variables = Read(url, token, "environmentvariabledefinition", "environmentvariabledefinitions", DriftQueries.Variables(sol.Id), DriftParsers.Variables);
        var references = Read(url, token, "connectionreference", "connectionreferences", DriftQueries.References(sol.Id), DriftParsers.References);

        // The layer table takes one component per query, so a managed solution costs one read per mapped component.
        var referenceIds = references.Select(r => r.Id).ToHashSet();
        var named = components.Select(c => (c.Type, c.Id, Name: referenceIds.Contains(c.Id) ? DriftQueries.ReferenceLayerName : DriftQueries.LayerName(c.Type))).ToList();
        var layersRead = !sol.Managed || named.Count(c => c.Name is not null) <= MaxLayerReads;
        return SnapshotFile.Sort(new Snapshot(name, solution, true, sol.Version, sol.Managed, layersRead,
            [.. named.Select(c => new SnapshotComponent(c.Type, c.Id,
                sol.Managed && layersRead && c.Name is not null
                    ? Read(url, token, "msdyn_componentlayer", "msdyn_componentlayers", DriftQueries.Layers(c.Id, c.Name), DriftParsers.Layers)
                    : null))],
            variables, references, flows));
    }

    private (string Name, string Url, string? EnvironmentId) Who(string env)
    {
        var who = pac(["org", "who", "--environment", env], Timeout);
        if (!who.Started)
        {
            throw new DriftReadException("pac was not found on PATH. Install pac (dotnet tool install --global Microsoft.PowerApps.CLI.Tool) and add the dotnet tools folder to PATH.");
        }

        if (who.TimedOut)
        {
            throw new DriftReadException("`pac org who` timed out after 30 s.");
        }

        if (who.ExitCode != 0)
        {
            throw new DriftReadException(
                $"`pac org who` failed (exit code {who.ExitCode}). Sign in with pac auth create, and check that the account can read that environment.", who.Stdout + who.Stderr);
        }

        const string Unreadable = "`pac org who` printed no environment name, or an environment URL powerops does not accept.";
        var name = SettingsCommand.Field(who.Stdout, "Friendly Name:") ?? throw new DriftReadException(Unreadable);
        try
        {
            var uri = DriftParsers.EnvironmentUrl(SettingsCommand.Field(who.Stdout, "Org URL:"));
            return DriftParsers.IsDataverseHost(uri)
                ? (name, uri.GetLeftPart(UriPartial.Authority), SettingsCommand.Field(who.Stdout, "Environment ID:"))
                : throw new DriftReadException(
                    "`pac org who` named an environment URL that is not on a documented Dataverse host (*.dynamics.com, *.crm.microsoftdynamics.us, *.crm.appsplatform.us, *.crm.dynamics.cn); no token was requested.", exitCode: 2);
        }
        catch (InvalidDataException)
        {
            throw new DriftReadException(Unreadable);
        }
    }

    // The token is taken out of az's stdout at once; on failure only stderr is forwarded, because stdout is the token on success.
    private string Token(string url)
    {
        var result = az(["account", "get-access-token", "--resource", url, "--query", "accessToken", "-o", "tsv"], Timeout);
        if (!result.Started)
        {
            throw new DriftReadException("az was not found on PATH. Install the Azure CLI and sign in with az login.");
        }

        if (result.TimedOut)
        {
            throw new DriftReadException("`az account get-access-token` timed out after 30 s.");
        }

        if (result.ExitCode != 0)
        {
            throw new DriftReadException($"`az account get-access-token` failed (exit code {result.ExitCode}). {SignInHint}", result.Stderr);
        }

        var token = result.Stdout.Trim();
        return token.Length > 0 ? token : throw new DriftReadException("`az account get-access-token` printed no token.");
    }

    // One Web API read, parsed; every other outcome than a readable 200 is a failed read.
    private T Read<T>(string url, string token, string table, string entitySet, string xml, Func<string, T> parser)
    {
        var r = web(new Uri($"{url}/api/data/v9.2/{entitySet}?fetchXml={Uri.EscapeDataString(xml)}"), token, Timeout);
        var read = $"the Web API read of `{table}`";
        if (r.TimedOut)
        {
            throw new DriftReadException($"{read} timed out after 30 s.");
        }

        if (r.Failure is not null)
        {
            throw new DriftReadException($"{read} failed ({r.Failure}).");
        }

        if (r.Status != 200)
        {
            throw new DriftReadException($"{read} failed (HTTP {r.Status}).{(r.Status is 401 or 403 ? " " + SignInHint : "")}", DriftParsers.ErrorMessage(r.Body));
        }

        try
        {
            return parser(r.Body);
        }
        catch (InvalidDataException ex)
        {
            throw new DriftReadException($"{read} returned output powerops cannot read: {ex.Message}");
        }
    }

    /// <summary>The production handler: it never follows a redirect, so the token cannot travel to another host.</summary>
    internal static SocketsHttpHandler Handler() => new() { AllowAutoRedirect = false };

    /// <summary>The web function over one <see cref="HttpClient"/> that uses <paramref name="handler"/>. The token travels only in each request's Authorization header.</summary>
    internal static Func<Uri, string, TimeSpan, WebResult> Web(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        return (url, token, timeout) =>
        {
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                return Fetch(client, url, token, cts.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                return new WebResult(0, "", true);
            }
            catch (Exception)
            {
                // Whatever went wrong, the text stays fixed: an exception's own message can name the host.
                return new WebResult(0, "", false, "transport error");
            }
        };
    }

    private static async Task<WebResult> Fetch(HttpClient client, Uri url, string token, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Add("OData-MaxVersion", "4.0");
        request.Headers.Add("OData-Version", "4.0");
        request.Headers.Add("Prefer", "odata.include-annotations=\"Microsoft.Dynamics.CRM.morerecords\"");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
        await using var stream = await response.Content.ReadAsStreamAsync(cancel);
        using var body = new MemoryStream();
        var buffer = new byte[81920];
        int n;
        while ((n = await stream.ReadAsync(buffer, cancel)) > 0)
        {
            if (body.Length + n > MaxBody)
            {
                return new WebResult((int)response.StatusCode, "", false, "unreadable response");
            }

            body.Write(buffer, 0, n);
        }

        return new WebResult((int)response.StatusCode, Encoding.UTF8.GetString(body.GetBuffer(), 0, (int)body.Length), false);
    }
}