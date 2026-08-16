// Tutor AI Content Admin — hosted review app so a non-technical subject-matter expert can
// generate and approve curriculum content without touching git, the CLI, or any dev tooling. See
// the handoff/plan for the full design; this file is just DI + pipeline wiring.

using ApTutor.ContentAdmin.Services;
using ApTutor.ContentFactory;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;

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

builder.Services.AddSingleton<GitRepoService>();
builder.Services.AddSingleton<CourseCatalog>();
builder.Services.AddSingleton<ContentGenerationService>();
builder.Services.AddSingleton<RejectionLog>();

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

// Clone/refresh the working copy and fail fast on a bad DAG/config at startup, rather than on
// whatever request happens to be first through the door.
using (var scope = app.Services.CreateScope())
{
    var git = scope.ServiceProvider.GetRequiredService<GitRepoService>();
    await git.EnsureUpToDateAsync();
    scope.ServiceProvider.GetRequiredService<CourseCatalog>();
}

app.Run();
