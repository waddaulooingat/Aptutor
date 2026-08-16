using System.Security.Claims;
using ApTutor.ContentAdmin.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ApTutor.ContentAdmin.Pages.Account;

[AllowAnonymous]
public sealed class LoginModel : PageModel
{
    private readonly IConfiguration _config;

    public LoginModel(IConfiguration config) => _config = config;

    [BindProperty]
    public string Username { get; set; } = "";

    [BindProperty]
    public string Password { get; set; } = "";

    public string? Error { get; set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl)
    {
        var expectedUsername = _config["ContentAdmin:AdminUsername"];
        var passwordHash = Environment.GetEnvironmentVariable("CONTENT_ADMIN_PASSWORD_HASH");

        if (string.IsNullOrEmpty(expectedUsername) || string.IsNullOrEmpty(passwordHash))
        {
            Error = "Sign-in is not configured yet. Contact the site administrator.";
            return Page();
        }

        var usernameMatches = string.Equals(Username, expectedUsername, StringComparison.Ordinal);
        var passwordMatches = !string.IsNullOrEmpty(Password) && PasswordHasher.Verify(Password, passwordHash);

        if (!usernameMatches || !passwordMatches)
        {
            Error = "Incorrect username or password.";
            return Page();
        }

        var claims = new List<Claim> { new(ClaimTypes.Name, expectedUsername) };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, new AuthenticationProperties
        {
            IsPersistent = true,
            ExpiresUtc = DateTimeOffset.UtcNow.AddDays(14),
        });

        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            return LocalRedirect(returnUrl);
        return RedirectToPage("/Index");
    }
}
