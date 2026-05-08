using System;
using System.Threading.Tasks;
using Amazon.Rekognition;
using Amazon.S3;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
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
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.IdentityModel.Tokens;
using NLog;

using NLog.Config;
using NLog.Extensions.Logging;
using Microsoft.Extensions.Logging;
using NLog.Targets;
using NLog.Web;
using System.Security.Claims;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Bookstore.Web
{
    public class Program
    {
        public static void Main(string[] args)
        {
            // -----------------------------------------------------------------------
            // 1. LOGGING SETUP (migrated from LoggingSetup.ConfigureLogging())
            //    Configure NLog before the host is built so early startup errors are
            //    captured.  NLog.Web.AspNetCore wires NLog into the .NET logging
            //    pipeline via builder.Logging below.
            // -----------------------------------------------------------------------
            const string AppName = "Bookstore";
            var nlogConfig = new LoggingConfiguration();

            NLog.Targets.Target loggingTarget;

            // Read the raw environment flag the same way ConfigurationSetup did;
            // the actual IConfiguration is not available yet, so we fall back to
            // the environment variable that mirrors the appsettings key.
            var loggingService = Environment.GetEnvironmentVariable("Services/LoggingService")
                                 ?? "local";

            if (loggingService == "aws")
            {
                loggingTarget = new FileTarget("aws-file") { FileName = $"{AppName}.log" };
            }
            else
            {
                loggingTarget = new DebuggerTarget("debugger");
            }

            nlogConfig.AddTarget(loggingTarget);
            nlogConfig.LoggingRules.Add(new LoggingRule("*", NLog.LogLevel.Info, loggingTarget));
            LogManager.Configuration = nlogConfig;

            // Use NLog.Web so that NLog becomes the backing ILogger implementation.
            var logger = LogManager.Setup()
                                   .LoadConfiguration(nlogConfig)
                                   .GetCurrentClassLogger();

            try
            {
                logger.Info("Starting Bookstore.Web application");

                // -------------------------------------------------------------------
                // 2. BUILD THE HOST  (replaces Program.CreateHostBuilder + Startup)
                // -------------------------------------------------------------------
                var builder = WebApplication.CreateBuilder(args);

                // Wire NLog into Microsoft.Extensions.Logging
                builder.Logging.ClearProviders();
                NLog.Web.AspNetExtensions.AddNLog(builder.Logging, nlogConfig);
                builder.Host.UseNLog();

                // -------------------------------------------------------------------
                // 3. AWS SYSTEMS MANAGER CONFIGURATION
                //    (migrated from ConfigurationSetup.ConfigureConfiguration())
                //    Augment the IConfiguration with values pulled from SSM so that
                //    all later service registrations can consume them via
                //    builder.Configuration.
                // -------------------------------------------------------------------
                var rootPath = "/" + AppName;

                if (builder.Configuration["Services/Database"] == "aws")
                {
                    using var ssmClient = new AmazonSimpleSystemsManagementClient();
                    var request = new GetParameterRequest
                    {
                        Name = $"{rootPath}/Database/ConnectionStrings/BookstoreDatabaseConnection"
                    };
                    var response = ssmClient.GetParameterAsync(request).Result;

                    // Inject the retrieved connection string into the in-memory
                    // configuration so builder.Configuration.GetConnectionString()
                    // returns the correct value for the rest of the startup.
                    builder.Configuration["ConnectionStrings:BookstoreDatabaseConnection"] =
                        response.Parameter.Value;
                }

                if (builder.Configuration["Services/Authentication"] == "aws")
                {
                    using var ssmClient = new AmazonSimpleSystemsManagementClient();
                    var request = new GetParametersByPathRequest
                    {
                        Path = $"{rootPath}/Authentication/",
                        Recursive = true
                    };
                    var response = ssmClient.GetParametersByPathAsync(request).Result;

                    foreach (var parameter in response.Parameters)
                    {
                        // Strip the SSM path prefix so the key matches the
                        // appsettings.json keys expected by the rest of the app
                        // (e.g. "Authentication/Cognito/MetadataAddress").
                        var key = parameter.Name
                            .Replace($"{rootPath}/", string.Empty)
                            .Replace('/', ':');
                        builder.Configuration[key] = parameter.Value;
                    }
                }

                if (builder.Configuration["Services/FileService"] == "aws")
                {
using var ssmClient = new AmazonSimpleSystemsManagementClient();
                    var request = new GetParametersByPathRequest
                    {
                        Path = $"{rootPath}/Files/",
                        Recursive = true
                    };
                    var response = ssmClient.GetParametersByPathAsync(request).Result;

                    foreach (var parameter in response.Parameters)
                    {
                        var key = parameter.Name
                            .Replace($"{rootPath}/", string.Empty)
                            .Replace('/', ':');
                        builder.Configuration[key] = parameter.Value;
                    }
                }

                // -------------------------------------------------------------------
                // 4. AUTOFAC SERVICE-PROVIDER FACTORY
                //    (migrated from DependencyInjectionSetup.ConfigureDependencyInjection())
                //    Keep Autofac as the DI container; individual registrations live
                //    in BookstoreAutofacModule.
                // -------------------------------------------------------------------
                builder.Host.UseServiceProviderFactory(new AutofacServiceProviderFactory());
                builder.Host.ConfigureContainer<ContainerBuilder>((context, containerBuilder) =>
                {
                    containerBuilder.RegisterModule(
                        new BookstoreAutofacModule(builder.Configuration, builder.Environment));
                });

                // -------------------------------------------------------------------
                // 5. MVC SERVICES  (migrated from FilterConfig.RegisterGlobalFilters())
                //    Global error handler  +  global Authorize filter are registered
                //    as MVC options – the .NET 8 equivalent of FilterConfig.
                // -------------------------------------------------------------------
                builder.Services.AddControllersWithViews(options =>
                {
                    // Equivalent of filters.Add(new HandleErrorAttribute())
                    options.Filters.Add<HandleErrorFilter>();

                    // Equivalent of filters.Add(new AuthorizeAttribute())
                    // Apply a global "must be authenticated" policy.
                    var policy = new AuthorizationPolicyBuilder()
                        .RequireAuthenticatedUser()
                        .Build();
                    options.Filters.Add(new AuthorizeFilter(policy));
                });

                // -------------------------------------------------------------------
                // 6. AUTHENTICATION SETUP
                //    (migrated from AuthenticationConfig.ConfigureAuthentication())
                //    Preserve the dual-mode local vs. Cognito logic.
                // -------------------------------------------------------------------
                var authMode = builder.Configuration["Services/Authentication"] ?? "local";

                if (authMode == "aws")
                {
                    // ---- Cognito / OpenID Connect ----
                    builder.Services
                        .AddAuthentication(options =>
                        {
                            options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                            options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
                        })
                        .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme)
                        .AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, options =>
                        {
                            options.ClientId = builder.Configuration["Authentication:Cognito:LocalClientId"];
                            options.MetadataAddress = builder.Configuration["Authentication:Cognito:MetadataAddress"];
                            options.ResponseType = "code";
                            options.UsePkce = true;
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
                            options.Events = new OpenIdConnectEvents
                            {
                                OnRedirectToIdentityProvider = context =>
                                {
                                    // Preserve the original redirect URI so the
                                    // callback returns to the right place.
                                    var returnUrl = context.Request.GetReturnUrl();
                                    if (!string.IsNullOrEmpty(returnUrl))
                                    {
                                        context.ProtocolMessage.RedirectUri = returnUrl;
                                    }
                                    return Task.CompletedTask;
                                },
                                OnAuthorizationCodeReceived = context =>
                                {
                                    var returnUrl = context.Request.GetReturnUrl();
                                    if (!string.IsNullOrEmpty(returnUrl))
                                    {
                                        context.TokenEndpointRequest!.RedirectUri = returnUrl;
                                    }
                                    return Task.CompletedTask;
                                },
                                OnTokenValidated = async context =>
                                {
                                    // Create-or-update the local customer record on
                                    // every successful Cognito sign-in.
                                    var service = context.HttpContext.RequestServices
                                        .GetRequiredService<ICustomerService>();

                                    var identity = (ClaimsIdentity)context.Principal!.Identity!;

                                    var dto = new CreateOrUpdateCustomerDto(
                                        identity.GetSub(),
                                        identity.Name ?? string.Empty,
                                        identity.FindFirst(c => c.Type.Contains("givenname"))?.Value ?? string.Empty,
                                        identity.FindFirst(c => c.Type.Contains("surname"))?.Value ?? string.Empty);

                                    await service.CreateOrUpdateCustomerAsync(dto);
                                }
                            };
                        });
                }
                else
                {
                    // ---- Local (development) authentication ----
                    // Cookie auth is still required so that LocalAuthenticationMiddleware
                    // can issue a sign-in ticket via HttpContext.SignInAsync.
                    builder.Services
                        .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                        .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
                        {
                            options.LoginPath = "/Authentication/Login";
                            options.ExpireTimeSpan = TimeSpan.FromDays(1);
                        });
                }

                builder.Services.AddAuthorization();

                // IHttpContextAccessor is needed by LocalAuthenticationMiddleware
                // (and other helpers that previously used HttpContext.Current).
                builder.Services.AddHttpContextAccessor();

                // -------------------------------------------------------------------
                // BUILD the WebApplication
                // -------------------------------------------------------------------
                var app = builder.Build();

                // -------------------------------------------------------------------
                // 7. MIDDLEWARE PIPELINE
                //    Order matters – match the original pipeline intent.
                // -------------------------------------------------------------------

                if (app.Environment.IsDevelopment())
                {
                    app.UseDeveloperExceptionPage();
                }
                else
                {
                    // Migrated from Global.asax Application_Error + HandleErrorAttribute
                    app.UseExceptionHandler("/Home/Error");
                    app.UseHsts();
                }

                app.UseHttpsRedirection();

                // Static files replace BundleConfig – scripts and styles are served
                // directly from wwwroot; Microsoft.AspNet.Web.Optimization is dropped.
                app.UseStaticFiles();

                app.UseRouting();

                // Authentication must come before Authorization
                app.UseAuthentication();

                // ---- Local authentication middleware ----
                // Replaces app.UseMiddlewareFromContainer<LocalAuthenticationMiddleware>()
                // from OWIN AuthenticationConfig.ConfigureLocalAuthentication().
                if (authMode != "aws")
                {
                    app.UseMiddleware<LocalAuthenticationMiddleware>();
                }

                app.UseAuthorization();

                // -------------------------------------------------------------------
                // 8. ROUTING  (migrated from RouteConfig + AdminAreaRegistration)
                // -------------------------------------------------------------------
                app.MapControllerRoute(
                    name: "Admin_default",
                    pattern: "Admin/{controller}/{action}/{id?}",
                    defaults: new { action = "Index" },
                    constraints: null,
                    dataTokens: new { area = "Admin", Namespaces = new[] { "Bookstore.Web.Areas.Admin.Controllers" } });

                app.MapControllerRoute(
                    name: "default",
                    pattern: "{controller=Home}/{action=Index}/{id?}",
                    defaults: null,
                    constraints: null,
                    dataTokens: new { Namespaces = new[] { "Bookstore.Web.Controllers" } });

                app.Run();
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Stopped program because of exception");
                throw;
            }
            finally
            {
                // Flush and stop internal NLog timers/threads before application-exit
                LogManager.Shutdown();
            }
        }
    }

    // ---------------------------------------------------------------------------
    // Autofac Module
    // Migrated from DependencyInjectionSetup.ConfigureDependencyInjection().
    // Keeps Autofac as the DI container via AutofacServiceProviderFactory.
    // ---------------------------------------------------------------------------
    public class BookstoreAutofacModule : Module
    {
        private readonly IConfiguration _configuration;
        private readonly IWebHostEnvironment _environment;

        public BookstoreAutofacModule(IConfiguration configuration, IWebHostEnvironment environment)
        {
            _configuration = configuration;
            _environment = environment;
        }

        protected override void Load(ContainerBuilder builder)
        {
            // ---- Domain / application services ----
            builder.RegisterType<BookService>().As<IBookService>();
            builder.RegisterType<OrderService>().As<IOrderService>();
            builder.RegisterType<ReferenceDataService>().As<IReferenceDataService>();
            builder.RegisterType<OfferService>().As<IOfferService>();
            builder.RegisterType<CustomerService>().As<ICustomerService>();
            builder.RegisterType<AddressService>().As<IAddressService>();
            builder.RegisterType<ShoppingCartService>().As<IShoppingCartService>();
            builder.RegisterType<ImageResizeService>().As<IImageResizeService>();

            // ---- Data / persistence ----
            var connectionString =
                _configuration.GetConnectionString("BookstoreDatabaseConnection")
                ?? string.Empty;

            // InstancePerLifetimeScope is the ASP.NET Core equivalent of
            // InstancePerRequest when Autofac.Extensions.DependencyInjection is used.
            builder.RegisterType<ApplicationDbContext>()
                   .WithParameter("connectionString", connectionString)
                   .InstancePerLifetimeScope();

            builder.RegisterType<CustomerRepository>().As<ICustomerRepository>();
            builder.RegisterType<AddressRepository>().As<IAddressRepository>();
            builder.RegisterType<BookRepository>().As<IBookRepository>();
            builder.RegisterType<OfferRepository>().As<IOfferRepository>();
            builder.RegisterType<ShoppingCartRepository>().As<IShoppingCartRepository>();
            builder.RegisterType<OrderRepository>().As<IOrderRepository>();
            builder.RegisterType<ReferenceDataRepository>().As<IReferenceDataRepository>();

            builder.RegisterGeneric(typeof(PaginatedList<>))
                   .As(typeof(IPaginatedList<>))
                   .InstancePerLifetimeScope();

            // ---- File service ----
            if (_configuration["Services/FileService"] == "aws")
            {
                builder.RegisterType<AmazonS3Client>().As(typeof(IAmazonS3));
                builder.RegisterType<S3FileService>().As<IFileService>();
            }
            else
            {
                // In .NET 8 the wwwroot path replaces HttpRuntime.AppDomainAppPath/Content
                var webRootPath = _environment.WebRootPath
                                  ?? AppContext.BaseDirectory;

                builder.RegisterInstance(new LocalFileService(webRootPath)).As<IFileService>();
            }

            // ---- Image validation service ----
            if (_configuration["Services/ImageValidationService"] == "aws")
            {
                builder.RegisterType<AmazonRekognitionClient>().As(typeof(IAmazonRekognition));
                builder.RegisterType<RekognitionImageValidationService>().As<IImageValidationService>();
            }
            else
            {
                builder.RegisterType<LocalImageValidationService>().As<IImageValidationService>();
            }

            // ---- Local authentication middleware ----
            // Only register when not using Cognito so the middleware can be
            // resolved from the container by app.UseMiddleware<LocalAuthenticationMiddleware>().
            if (_configuration["Services/Authentication"] != "aws")
            {
                builder.RegisterType<LocalAuthenticationMiddleware>();
            }
        }
    }

    // ---------------------------------------------------------------------------
    // HandleErrorFilter
    // Replaces the legacy System.Web.Mvc.HandleErrorAttribute registered in
    // FilterConfig.  Logs unhandled exceptions through the standard
    // Microsoft.Extensions.Logging pipeline (backed by NLog).
    // ---------------------------------------------------------------------------
    public class HandleErrorFilter : Microsoft.AspNetCore.Mvc.Filters.IExceptionFilter
    {
        private readonly Microsoft.Extensions.Logging.ILogger<HandleErrorFilter> _logger;

        public HandleErrorFilter(Microsoft.Extensions.Logging.ILogger<HandleErrorFilter> logger)
        {
            _logger = logger;
        }

        public void OnException(Microsoft.AspNetCore.Mvc.Filters.ExceptionContext context)
        {
            _logger.LogError(context.Exception,
                "Unhandled exception in {Controller}.{Action}",
                context.RouteData.Values["controller"],
                context.RouteData.Values["action"]);
        }
    }
}
