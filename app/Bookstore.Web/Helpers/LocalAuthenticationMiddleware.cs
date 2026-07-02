using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Bookstore.Domain.Customers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;

namespace Bookstore.Web.Helpers
{
    public class LocalAuthenticationMiddleware
    {
        private const string UserId = "FB6135C7-1464-4A72-B74E-4B63D343DD09";
        private const string CookieName = "LocalAuthentication";

        private readonly RequestDelegate _next;

        public LocalAuthenticationMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task InvokeAsync(HttpContext context, ICustomerService customerService)
        {
            if (context.Request.Path.StartsWithSegments("/Authentication/Login"))
            {
                var principal = CreateClaimsPrincipal();

                await context.SignInAsync(
                    CookieAuthenticationDefaults.AuthenticationScheme,
                    principal,
                    new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddDays(1) });

                await SaveCustomerDetailsAsync(customerService, (ClaimsIdentity)principal.Identity!);

                context.Response.Redirect("/");
                return;
            }

            if (context.Request.Cookies[CookieName] != null)
            {
                var principal = CreateClaimsPrincipal();
                context.User = principal;

                await SaveCustomerDetailsAsync(customerService, (ClaimsIdentity)principal.Identity!);
            }

            await _next(context);
        }

        private static ClaimsPrincipal CreateClaimsPrincipal()
        {
            var identity = new ClaimsIdentity("Application");

            identity.AddClaim(new Claim(ClaimTypes.Name, "bookstoreuser"));
            identity.AddClaim(new Claim("nameidentifier", UserId));
            identity.AddClaim(new Claim("given_name", "Bookstore"));
            identity.AddClaim(new Claim("family_name", "User"));
            identity.AddClaim(new Claim(ClaimTypes.Role, "Administrators"));

            return new ClaimsPrincipal(identity);
        }

        private static async Task SaveCustomerDetailsAsync(ICustomerService customerService, ClaimsIdentity identity)
        {
            var dto = new CreateOrUpdateCustomerDto(
                identity.FindFirst("nameidentifier")?.Value ?? string.Empty,
                identity.Name ?? string.Empty,
                identity.FindFirst("given_name")?.Value ?? string.Empty,
                identity.FindFirst("family_name")?.Value ?? string.Empty);

            await customerService.CreateOrUpdateCustomerAsync(dto);
        }
    }
}
