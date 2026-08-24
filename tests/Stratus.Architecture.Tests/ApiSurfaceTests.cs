using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Stratus.Architecture.Tests;

/// <summary>
/// Every service publishes an OpenAPI document and a Scalar reference, and both
/// are wired ONCE in ServiceDefaults rather than per host.
///
/// These drive the real pipeline through <see cref="WebApplicationFactory{T}"/>
/// rather than reading Program.cs, because the interesting failures are all
/// invisible to a source scan: a document served at a path the reference does
/// not ask for, a reference whose relative URL resolves somewhere else once it
/// is behind a gateway, a route that exists but returns the WRONG service's
/// document. Tenancy stands in for the fleet — the wiring under test lives in
/// ServiceDefaults, so a second host would re-verify the same code.
/// </summary>
public class ApiSurfaceTests
{
    private const string Service = "tenancy";

    private static WebApplicationFactory<Program> Host(bool? exposed = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Database=t;Username=t;Password=t");
            if (exposed is not null)
            {
                b.UseSetting("OpenApi:Exposed", exposed.Value ? "true" : "false");
            }
        });

    [Fact]
    public async Task The_openapi_document_is_named_after_the_service()
    {
        using var factory = Host();
        using var client = factory.CreateClient();

        // Not `/openapi/v1.json`: six services behind one gateway all publishing
        // "v1" cannot be told apart, and the gateway is the only public surface.
        var response = await client.GetAsync($"/openapi/{Service}.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var title = doc.RootElement.GetProperty("info").GetProperty("title").GetString();
        Assert.Contains(Service, title, StringComparison.Ordinal);

        // A document with no paths would satisfy every assertion above while
        // describing nothing, which is the shape this whole file exists to catch.
        var paths = doc.RootElement.GetProperty("paths");
        Assert.NotEmpty(paths.EnumerateObject());
    }

    [Fact]
    public async Task The_reference_ui_is_served_at_a_service_scoped_path()
    {
        using var factory = Host();
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/docs/{Service}/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("scalar", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_reference_points_at_a_document_that_actually_resolves()
    {
        using var factory = Host();
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync($"/docs/{Service}/");

        // Scalar embeds the document URL RELATIVE, and its own script resolves
        // it against origin + basePath — not against the page's directory. So
        // the URL the browser really fetches is the relative value hung off the
        // root. Assert on that, because asserting on the page alone passes for
        // a reference whose document 404s and renders empty.
        var marker = $"\"url\":\"{Service}.json\"";
        var relative = html.Contains(marker, StringComparison.Ordinal)
            ? $"{Service}.json"
            : $"openapi/{Service}.json";
        Assert.Contains(relative, html, StringComparison.Ordinal);

        var resolved = await client.GetAsync($"/{relative}");
        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
    }

    [Fact]
    public async Task Health_is_never_gated_on_the_api_surface()
    {
        using var factory = Host(exposed: false);
        using var client = factory.CreateClient();

        // Withholding the docs must not take liveness with it — a probe that
        // fails because documentation is off is a restart loop.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
    }

    [Fact]
    public async Task Setting_OpenApi_Exposed_false_withholds_both()
    {
        using var factory = Host(exposed: false);
        using var client = factory.CreateClient();

        // The NEGATIVE half. Without it, the three tests above would pass just
        // as happily if the config switch did nothing at all.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/openapi/{Service}.json")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/docs/{Service}/")).StatusCode);
    }

    [Fact]
    public void Service_defaults_owns_the_api_surface_so_no_host_restates_it()
    {
        // The duplication this replaced: three hosts called AddOpenApi/MapOpenApi
        // and three did not, so half the fleet published nothing. Keeping the
        // wiring in one place is the fix; this keeps it there.
        var hosts = Directory.GetFiles(RepoRoot(), "Program.cs", SearchOption.AllDirectories)
            .Where(p => p.Contains($"{Path.DirectorySeparatorChar}src{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(hosts);

        var offenders = hosts
            .Where(p => File.ReadAllText(p) is var t
                        && (t.Contains("AddOpenApi(", StringComparison.Ordinal)
                            || t.Contains("MapOpenApi(", StringComparison.Ordinal)
                            || t.Contains("MapScalarApiReference(", StringComparison.Ordinal)))
            .Select(p => Path.GetRelativePath(RepoRoot(), p))
            .ToList();

        Assert.True(offenders.Count == 0,
            "these hosts wire the API surface themselves instead of via AddServiceDefaults/MapDefaultEndpoints: "
            + string.Join(", ", offenders));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("could not locate the repo root from the test output directory");
    }
}
