using Amazon.Extensions.Configuration.SystemsManager;
using Amazon.Rekognition;
using Amazon.S3;
using Amazon.SimpleSystemsManagement;
using Autofac;
using Autofac.Extensions.DependencyInjection;
using Bookstore.Data;
using Bookstore.Data.FileServices;
using Bookstore.Data.ImageResizeService;
using Bookstore.Data.ImageValidationServices;
using Bookstore.Data.Repositories;
using Bookstore.Domain;
using Bookstore.Domain.Addresses;
using Bookstore.Domain.Books;
using Bookstore.Domain.Carts;
using Bookstore.Domain.Customers;
using Bookstore.Domain.Offers;
using Bookstore.Domain.Orders;
using Bookstore.Domain.ReferenceData;
using Bookstore.Web.Helpers;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using NLog;
using NLog.AWS.Logger;
using NLog.Config;
using NLog.Extensions.Logging;
using NLog.Targets;
using System.IO;
using System.Security.Claims;
using System.Threading.Tasks;

// -------------------------------------------------------------------------
// Program.cs — ASP.NET Core 8.0 entry point for Bookstore.Web
// Migrated from:
//   • Global.asax / Global.asax.cs  (HttpApplication lifecycle)
//   • OWIN Startup.cs               (OWIN pipeline)
//   • App_Start/AuthenticationSetup.cs
//   • App_Start/BundleConfig.cs     (NOTE: see bundle migration comment below)
//   • App_Start/ConfigurationSetup.cs
//   • App_Start/DependencyInjectionSetup.cs
//   • App_Start/FilterConfig.cs
//   • App_Start/LoggingSetup.cs
//   • App_Start/RouteConfig.cs
// -------------------------------------------------------------------------

var builder = WebApplication.CreateBuilder(args);

// =========================================================================
// 1. CONFIGURATION
// Replaces ConfigurationSetup.ConfigureConfiguration() / Web.config <appSettings>.
// appsettings.json holds local defaults; AWS SSM is layered on top when the
// corresponding service flag is set to "aws".
// =========================================================================
builder.Configuration
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables();

// Conditionally pull secrets from AWS Systems Manager Parameter Store.
// The original ConfigurationSetup used AmazonSimpleSystemsManagementClient
// directly; here we use the AWS .NET SDK configuration provider pattern
// so that all SSM values are available through IConfiguration.
var appName = builder.Configuration["AppName"] ?? "Bookstore";

if (builder.Configuration["Services:Database"] == "aws")
{
    // SSM path: /<AppName>/Database/ConnectionStrings/BookstoreDatabaseConnection
    builder.Configuration.AddSystemsManager($"/{appName}/Database");
}

if (builder.Configuration["Services:Authentication"] == "aws")
{
    // SSM path: /<AppName>/Authentication/**
    builder.Configuration.AddSystemsManager($"/{appName}/Authentication");
}

if (builder.Configuration["Services:FileService"] == "aws")
{
    // SSM path: /<AppName>/Files/**
    builder.Configuration.AddSystemsManager($"/{appName}/Files");
}

// =========================================================================
// 2. LOGGING
// Replaces LoggingSetup.ConfigureLogging() which configured NLog programmatically.
// NLog.Extensions.Logging bridges NLog into ASP.NET Core's ILogger pipeline.
// =========================================================================
builder.Logging.ClearProviders();

var nlogConfig = new LoggingConfiguration();
Target loggingTarget;

if (builder.Configuration["Services:LoggingService"] == "aws")
{
    // Migrated from: loggingTarget = new AWSTarget { LogGroup = Constants.AppName };
    loggingTarget = new AWSTarget { LogGroup = appName };
}
else
{
    // Migrated from: loggingTarget = new DebuggerTarget();
    loggingTarget = new DebuggerTarget("debugger");
}

nlogConfig.AddTarget("default", loggingTarget);
nlogConfig.LoggingRules.Add(new LoggingRule("*", NLog.LogLevel.Info, loggingTarget));
LogManager.Configuration = nlogConfig;

builder.Logging.AddNLog();

// =========================================================================
// 3. AUTOFAC — Dependency Injection
// Replaces DependencyInjectionSetup.ConfigureDependencyInjection() which used
// Autofac.Integration.Mvc + Autofac.Integration.Owin.
// Now uses Autofac.Extensions.DependencyInjection with the generic host.
// =========================================================================
builder.Host.UseServiceProviderFactory(new AutofacServiceProviderFactory());

builder.Host.ConfigureContainer<ContainerBuilder>(autofacBuilder =>
{
    // ---- Domain Services ----
    // Migrated from: builder.RegisterType<BookService>().As<IBookService>(); etc.
    autofacBuilder.RegisterType<BookService>().As<IBookService>();
    autofacBuilder.RegisterType<OrderService>().As<IOrderService>();
    autofacBuilder.RegisterType<ReferenceDataService>().As<IReferenceDataService>();
    autofacBuilder.RegisterType<OfferService>().As<IOfferService>();
    autofacBuilder.RegisterType<CustomerService>().As<ICustomerService>();
    autofacBuilder.RegisterType<AddressService>().As<IAddressService>();
    autofacBuilder.RegisterType<ShoppingCartService>().As<IShoppingCartService>();
    autofacBuilder.RegisterType<ImageResizeService>().As<IImageResizeService>();

    // ---- Repositories ----
    autofacBuilder.RegisterType<CustomerRepository>().As<ICustomerRepository>();
    autofacBuilder.RegisterType<AddressRepository>().As<IAddressRepository>();
    autofacBuilder.RegisterType<BookRepository>().As<IBookRepository>();
    autofacBuilder.RegisterType<OfferRepository>().As<IOfferRepository>();
    autofacBuilder.RegisterType<ShoppingCartRepository>().As<IShoppingCartRepository>();
    autofacBuilder.RegisterType<OrderRepository>().As<IOrderRepository>();
    autofacBuilder.RegisterType<ReferenceDataRepository>().As<IReferenceDataRepository>();

    // ---- Generic pagination ----
    // Migrated from: builder.RegisterGeneric(typeof(PaginatedList<>)).As(typeof(IPaginatedList<>)).InstancePerLifetimeScope();
    autofacBuilder.RegisterGeneric(typeof(PaginatedList<>))
                  .As(typeof(IPaginatedList<>))
                  .InstancePerLifetimeScope();

    // ---- DbContext ----
    // Migrated from: builder.RegisterType<ApplicationDbContext>().WithParameter(...).InstancePerRequest();
    // InstancePerRequest() maps to InstancePerLifetimeScope() in ASP.NET Core (one scope per HTTP request).
    var connectionString = builder.Configuration.GetConnectionString("BookstoreDatabaseConnection");
    autofacBuilder.RegisterType<ApplicationDbContext>()
                  .WithParameter("connectionString", connectionString)
                  .InstancePerLifetimeScope();

    // ---- File Service ----
    // Migrated from the conditional registration in DependencyInjectionSetup.
    if (builder.Configuration["Services:FileService"] == "aws")
    {
        autofacBuilder.RegisterType<AmazonS3Client>().As<IAmazonS3>();
        autofacBuilder.RegisterType<S3FileService>().As<IFileService>();
    }
    else
    {
        // Replaces HttpRuntime.AppDomainAppPath with IWebHostEnvironment.WebRootPath
        // (resolved at registration time via the builder's configuration).
        var webRootPath = Path.Combine(
            builder.Environment.ContentRootPath, "wwwroot", "Content");
        autofacBuilder.RegisterInstance(new LocalFileService(webRootPath)).As<IFileService>();
    }

    // ---- Image Validation Service ----
    if (builder.Configuration["Services:ImageValidationService"] == "aws")
    {
        autofacBuilder.RegisterType<AmazonRekognitionClient>().As<IAmazonRekognition>();
        autofacBuilder.RegisterType<RekognitionImageValidationService>().As<IImageValidationService>();
    }
    else
    {
        autofacBuilder.RegisterType<LocalImageValidationService>().As<IImageValidationService>();
    }

    // ---- Local authentication middleware (non-AWS only) ----
    if (builder.Configuration["Services:Authentication"] != "aws")
    {
        autofacBuilder.RegisterType<LocalAuthenticationMiddleware>();
    }
});

// =========================================================================
// 4. MVC + GLOBAL FILTERS
// Migrated from:
//   • AreaRegistration.RegisterAllAreas()   → AddControllersWithViews() auto-discovers areas
//   • FilterConfig.RegisterGlobalFilters()  → options.Filters.Add(...)
//     - HandleErrorAttribute  → app.UseExceptionHandler (see middleware section)
//     - AuthorizeAttribute    → RequireAuthorization policy applied globally
// =========================================================================
builder.Services.AddControllersWithViews(options =>
{
    // Migrated from: filters.Add(new AuthorizeAttribute());
    // All controllers require an authenticated user by default.
    options.Filters.Add(new Microsoft.AspNetCore.Mvc.Authorization.AuthorizeFilter());

    // NOTE: HandleErrorAttribute is replaced by app.UseExceptionHandler() below.
});

// Required to enable [Area] routing for discovered area controllers.
builder.Services.AddRazorPages();

// =========================================================================
// 5. AUTHENTICATION
// Migrated from AuthenticationSetup.ConfigureAuthentication() which used
// OWIN middleware (Microsoft.Owin.Security.Cookies + Microsoft.Owin.Security.OpenIdConnect).
// Now uses Microsoft.AspNetCore.Authentication.Cookies and .OpenIdConnect.
// =========================================================================
if (builder.Configuration["Services:Authentication"] == "aws")
{
    // --- Cognito OpenID Connect (AWS) ---
    // Migrated from ConfigureCognitoAuthentication(app) in AuthenticationSetup.cs.
    builder.Services
        .AddAuthentication(options =>
        {
            options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
        })
        .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, options =>
        {
            // Migrated from OpenIdConnectAuthenticationOptions in AuthenticationSetup.cs.
            options.ClientId = builder.Configuration["Authentication:Cognito:LocalClientId"];
            options.MetadataAddress = builder.Configuration["Authentication:Cognito:MetadataAddress"];
            options.ResponseType = OpenIdConnectResponseType.Code;
            options.UsePkce = true;                 // replaces RedeemCode = true
            options.Scope.Clear();
            options.Scope.Add("openid");
            options.Scope.Add("profile");
            options.SaveTokens = true;
            options.UseTokenLifetime = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                NameClaimType = "cognito:username",
                RoleClaimType = "cognito:groups"
            };

            // Migrated from Notifications.RedirectToIdentityProvider:
            // Ensure the redirect_uri reflects the actual request URL.
            options.Events = new OpenIdConnectEvents
            {
                OnRedirectToIdentityProvider = ctx =>
                {
                    ctx.ProtocolMessage.RedirectUri = ctx.Request.GetReturnUrl();
                    return Task.CompletedTask;
                },
                OnAuthorizationCodeReceived = ctx =>
                {
                    ctx.TokenEndpointRequest!.RedirectUri = ctx.Request.GetReturnUrl();
                    return Task.CompletedTask;
                },
                // Migrated from SecurityTokenValidated — create/update the customer record
                // after a successful token validation.
                OnTokenValidated = async ctx =>
                {
                    var service = ctx.HttpContext.RequestServices.GetRequiredService<ICustomerService>();
                    var identity = (ClaimsIdentity)ctx.Principal!.Identity!;

                    var dto = new Bookstore.Domain.Customers.CreateOrUpdateCustomerDto(
                        identity.GetSub(),
                        identity.Name,
                        identity.FindFirst(c => c.Type.Contains("givenname"))!.Value,
                        identity.FindFirst(c => c.Type.Contains("surname"))!.Value);

                    await service.CreateOrUpdateCustomerAsync(dto);
                }
            };
        });
}
else
{
    // --- Local authentication (non-AWS) ---
    // Migrated from ConfigureLocalAuthentication(app) which used
    // app.UseMiddlewareFromContainer<LocalAuthenticationMiddleware>().
    // The middleware itself is registered in Autofac above and added to the
    // pipeline in the middleware section below.
    builder.Services
        .AddAuthentication(options =>
        {
            options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        })
        .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme);
}

builder.Services.AddAuthorization();

// =========================================================================
// 6. BUILD THE APPLICATION
// =========================================================================
var app = builder.Build();

// =========================================================================
// 7. EXCEPTION HANDLING MIDDLEWARE
// Migrated from:
//   • FilterConfig: filters.Add(new HandleErrorAttribute())  → UseExceptionHandler
//   • Application_Error NLog logging in Global.asax.cs       → captured by NLog
//     which is already wired to the ASP.NET Core logging pipeline (step 2).
// =========================================================================
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    // In production, route unhandled exceptions to /Home/Error.
    // NLog captures the exception via its ASP.NET Core logging integration.
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

// =========================================================================
// 8. STATIC FILES
// Replaces the implicit static-file handling provided by IIS in the classic app.
// BundleConfig.RegisterBundles() is NOT directly portable to ASP.NET Core.
// The bundles (jquery, jqueryval, modernizr, bootstrap, css) should be
// referenced as individual <script>/<link> tags in _Layout.cshtml, or a
// modern bundler (LibMan, WebOptimizer, webpack) should be adopted.
// TODO (manual follow-up): update Views/Shared/_Layout.cshtml to remove
//   @Styles.Render / @Scripts.Render calls and add direct <link>/<script> tags.
// =========================================================================
app.UseHttpsRedirection();
app.UseStaticFiles();

// =========================================================================
// 9. ROUTING
// =========================================================================
app.UseRouting();

// =========================================================================
// 10. AUTHENTICATION & AUTHORISATION MIDDLEWARE
// =========================================================================
app.UseAuthentication();
app.UseAuthorization();

// =========================================================================
// 11. LOCAL AUTHENTICATION MIDDLEWARE (non-AWS)
// Migrated from: app.UseMiddlewareFromContainer<LocalAuthenticationMiddleware>()
// in AuthenticationSetup.ConfigureLocalAuthentication().
// =========================================================================
if (app.Configuration["Services:Authentication"] != "aws")
{
    app.UseMiddleware<LocalAuthenticationMiddleware>();
}

// =========================================================================
// 12. ROUTING / ENDPOINT MAPPING
// Migrated from RouteConfig.RegisterRoutes() which mapped:
//   url: "{controller}/{action}/{id}"  defaults: Home/Index, id optional
// Areas are automatically discovered by AddControllersWithViews();
// no explicit AreaRegistration.RegisterAllAreas() call is required.
// =========================================================================
app.MapControllerRoute(
    name: "areas",
    pattern: "{area:exists}/{controller=Home}/{action=Index}/{id?}");

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}",
    defaults: new { controller = "Home", action = "Index" });

// =========================================================================
// 13. START THE APPLICATION
// =========================================================================
app.Run();
