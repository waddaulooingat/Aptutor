// Tutor AI Content Admin — hosted review app so a non-technical subject-matter expert can
// generate and approve curriculum content without touching git, S3, or any dev tooling. S3 is the
// source of truth for approved content; git remains for application source code only. See the
// plan for the full design; this file is just DI + pipeline wiring.

using Amazon;
using Amazon.S3;
using ApTutor.ContentAdmin.Services;
using ApTutor.ContentFactory;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // No proxy IPs/networks are trusted by default. Whoever picks the hosting target (out of
    // scope here — see the plan) must add that host's reverse-proxy address via KnownProxies /
    // KnownNetworks before X-Forwarded-* headers are actually honored.
});

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Account/Login";
        options.Cookie.Name = "ContentAdmin.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromDays(14);
        options.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/");
    options.Conventions.AllowAnonymousToPage("/Account/Login");
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // Scoped to the whole Review page (Generate/Approve/Reject) rather than just Generate: Razor
    // Pages' [EnableRateLimiting] attaches at the page level, not per named handler, since all of
    // a page's handlers share one routed endpoint. Folding Approve/Reject in too is harmless (a
    // human clicking a button, not billed API spend) — the real target, Generate, is covered.
    options.AddFixedWindowLimiter("content-mutations", limiterOptions =>
    {
        limiterOptions.Window = TimeSpan.FromMinutes(1);
        limiterOptions.PermitLimit = 10;
        limiterOptions.QueueLimit = 0;
    });
});

// Reused as-is from ApTutor.ContentFactory: ClaudeClient owns one long-lived HttpClient, so it
// (and Generator, which just wraps it) are registered as singletons — constructing a fresh
// ClaudeClient/HttpClient per request would be the classic socket-exhaustion footgun.
builder.Services.AddSingleton(_ =>
{
    var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
        ?? throw new InvalidOperationException("ANTHROPIC_API_KEY is not set.");
    var model = Environment.GetEnvironmentVariable("ANTHROPIC_MODEL")
        ?? throw new InvalidOperationException("ANTHROPIC_MODEL is not set.");
    return new ClaudeClient(apiKey, model);
});
builder.Services.AddSingleton<Generator>();

builder.Services.Configure<S3ContentStoreOptions>(builder.Configuration.GetSection("ContentStore"));
builder.Services.AddSingleton<IAmazonS3>(sp =>
{
    var options = sp.GetRequiredService<IOptions<S3ContentStoreOptions>>().Value;
    if (string.IsNullOrWhiteSpace(options.Region))
        throw new InvalidOperationException("ContentStore:Region is not configured.");

    // Explicit region rather than a zero-arg AmazonS3Client() — avoids an implicit
    // GetBucketLocation call (a permission this app's IAM policy doesn't grant) if the SDK can't
    // otherwise resolve a region in whatever environment this ends up running in.
    return new AmazonS3Client(new AmazonS3Config { RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region) });
});
// IAmazonS3/S3ContentStore explicitly singleton — same connection-pooling reasoning as ClaudeClient above.
builder.Services.AddSingleton<S3ContentStore>();
builder.Services.AddSingleton<RejectionLog>();
builder.Services.AddSingleton<ContentGenerationService>();
builder.Services.AddSingleton<UnitStructureGenerationService>();
builder.Services.AddSingleton<CourseCreationService>();
builder.Services.AddSingleton<CourseCatalog>();

var app = builder.Build();

app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
    app.UseHsts();
app.UseHttpsRedirection();

app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();

// Fail fast at startup on a real S3 misconfiguration (wrong bucket, revoked credentials) rather
// than surfacing as a confusing 403/404 on the first SME's first click — see
// S3ContentStore.ValidateConnectivityAsync for why this matters specifically for S3's 403-vs-404
// behavior on a missing key. CourseCatalog's first RefreshAsync also happens here, eagerly, so a
// broken course discovery fails loud at boot rather than surfacing as an empty course list.
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<S3ContentStore>().ValidateConnectivityAsync();
    await scope.ServiceProvider.GetRequiredService<CourseCatalog>().RefreshAsync();
}

app.Run();
