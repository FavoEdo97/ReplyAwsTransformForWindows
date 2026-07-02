using System.IO;
using System.Reflection;
using Amazon.Rekognition;
using Amazon.S3;
using Amazon.SimpleSystemsManagement;
using Autofac;
using Autofac.Extensions.DependencyInjection;
using Bookstore.Common;
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
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using NLog;

using NLog.Config;
using NLog.Extensions.Logging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NLog.Targets;

namespace Bookstore.Web
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // ---------------------------------------------------------------------------
            // 1. Configuration — load AWS SSM parameters on top of appsettings.json
            // ---------------------------------------------------------------------------
            var configuration = builder.Configuration;

            var rootPath = "/" + Constants.AppName;

            if (configuration["Services/Database"] == "aws")
            {
                // Load the DB connection string from SSM
                builder.Configuration.AddSystemsManager(
                    $"{rootPath}/Database/",
                    optional: true);
            }

            if (configuration["Services/Authentication"] == "aws")
            {
                // Load Cognito authentication parameters from SSM
                builder.Configuration.AddSystemsManager(
                    $"{rootPath}/Authentication/",
                    optional: true);
            }

            if (configuration["Services/FileService"] == "aws")
            {
                // Load S3 / CloudFront file-service parameters from SSM
                builder.Configuration.AddSystemsManager(
                    $"{rootPath}/Files/",
                    optional: true);
            }

            // ---------------------------------------------------------------------------
            // 2. Logging — NLog with conditional AWS CloudWatch vs. Debugger target
            // ---------------------------------------------------------------------------
            builder.Logging.ClearProviders();

            var nlogConfig = new LoggingConfiguration();
            Target loggingTarget;

            loggingTarget = new DebuggerTarget();

            nlogConfig.AddTarget("primary", loggingTarget);
            nlogConfig.LoggingRules.Add(new LoggingRule("*", NLog.LogLevel.Info, loggingTarget));
            LogManager.Configuration = nlogConfig;

            builder.Logging.AddNLog();

            // ---------------------------------------------------------------------------
            // 3. Autofac — use AutofacServiceProviderFactory and wire registrations via
            //    ConfigureContainer<ContainerBuilder>. In the minimal hosting model the
            //    factory alone is not enough; registrations must be supplied explicitly.
            // ---------------------------------------------------------------------------
            builder.Host.UseServiceProviderFactory(new AutofacServiceProviderFactory());

            builder.Host.ConfigureContainer<ContainerBuilder>((ctx, cb) =>
            {
                ConfigureAutofacContainer(cb, ctx.Configuration, ctx.HostingEnvironment as IWebHostEnvironment);
            });

            // ---------------------------------------------------------------------------
            // 4. MVC services
            // ---------------------------------------------------------------------------
            builder.Services.AddControllersWithViews(options =>
            {
                // Replaces FilterConfig.RegisterGlobalFilters: require authentication
                // globally; individual actions/controllers can opt out with [AllowAnonymous].
                options.Filters.Add(new AuthorizeFilter(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build()));
            });

            // ---------------------------------------------------------------------------
            // 5. Authentication — Cognito (OpenIdConnect + Cookie) or Local fallback
            // ---------------------------------------------------------------------------
            if (configuration["Services/Authentication"] == "aws")
            {
                builder.Services
                    .AddAuthentication(options =>
                    {
                        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                        options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
                    })
                    .AddCookie()
                    .AddOpenIdConnect(options =>
                    {
                        options.ClientId = configuration["Authentication/Cognito/LocalClientId"];
                        options.MetadataAddress = configuration["Authentication/Cognito/MetadataAddress"];
                        options.ResponseType = Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectResponseType.Code;
                        options.Scope.Add("openid");
                        options.Scope.Add("profile");
                        options.UsePkce = true;
                        options.SaveTokens = true;
                        options.UseTokenLifetime = false;
                        options.TokenValidationParameters = new TokenValidationParameters
                        {
                            NameClaimType = "cognito:username",
                            RoleClaimType = "cognito:groups"
                        };
                        // Preserve the redirect URI through the Cognito authorisation code flow
                        options.Events = new OpenIdConnectEvents
                        {
                            OnRedirectToIdentityProvider = context =>
                            {
                                var returnUrl = context.HttpContext.Request.GetReturnUrl();
                                if (!string.IsNullOrEmpty(returnUrl))
                                {
                                    context.ProtocolMessage.RedirectUri = returnUrl;
                                }
                                return System.Threading.Tasks.Task.CompletedTask;
                            },
                            OnAuthorizationCodeReceived = context =>
                            {
                                var returnUrl = context.HttpContext.Request.GetReturnUrl();
                                if (!string.IsNullOrEmpty(returnUrl))
                                {
                                    context.TokenEndpointRequest!.RedirectUri = returnUrl;
                                }
                                return System.Threading.Tasks.Task.CompletedTask;
                            },
                            OnTokenValidated = async context =>
                            {
                                var customerService = context.HttpContext
                                    .RequestServices.GetRequiredService<ICustomerService>();

                                var identity = (System.Security.Claims.ClaimsIdentity?)context.Principal?.Identity;
                                if (identity != null)
                                {
                                    var dto = new CreateOrUpdateCustomerDto(
                                        identity.GetSub(),
                                        identity.Name ?? string.Empty,
                                        identity.FindFirst(c => c.Type.Contains("givenname"))?.Value ?? string.Empty,
                                        identity.FindFirst(c => c.Type.Contains("surname"))?.Value ?? string.Empty);

                                    await customerService.CreateOrUpdateCustomerAsync(dto);
                                }
                            }
                        };
                    });
            }
            else
            {
                // Local authentication — the LocalAuthenticationMiddleware handles login
                // and injects a ClaimsPrincipal; a cookie scheme is still needed so that
                // [Authorize] attributes resolve correctly.
                builder.Services
                    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                    .AddCookie(options =>
                    {
                        options.LoginPath = "/Authentication/Login";
                    });
            }

            // ---------------------------------------------------------------------------
            // 6. Build
            // ---------------------------------------------------------------------------
            var app = builder.Build();

            // ---------------------------------------------------------------------------
            // 7. Middleware pipeline
            // ---------------------------------------------------------------------------
            if (app.Environment.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseExceptionHandler("/Home/Error");
                app.UseHsts();
            }

            app.UseHttpsRedirection();
            app.UseStaticFiles();

            app.UseRouting();

            app.UseAuthentication();
            app.UseAuthorization();

            // Inject the Local authentication middleware when not using Cognito
            if (configuration["Services/Authentication"] != "aws")
            {
                app.UseMiddleware<LocalAuthenticationMiddleware>();
            }

            // ---------------------------------------------------------------------------
            // 8. Routing — conventional MVC pattern (mirrors original RouteConfig)
            // ---------------------------------------------------------------------------
            app.MapControllerRoute(
                name: "Admin",
                pattern: "{area:exists}/{controller=Dashboard}/{action=Index}/{id?}");

            app.MapControllerRoute(
                name: "default",
                pattern: "{controller=Home}/{action=Index}/{id?}");

            app.Run();
        }

        // ---------------------------------------------------------------------------
        // 9. ConfigureAutofacContainer — all Autofac registrations (replaces
        //    DependencyInjectionSetup.ConfigureDependencyInjection). Called explicitly
        //    via builder.Host.ConfigureContainer<ContainerBuilder> above.
        // ---------------------------------------------------------------------------
        private static void ConfigureAutofacContainer(ContainerBuilder builder, IConfiguration configuration, IWebHostEnvironment? env)
        {
            // Domain services
            builder.RegisterType<BookService>().As<IBookService>();
            builder.RegisterType<OrderService>().As<IOrderService>();
            builder.RegisterType<ReferenceDataService>().As<IReferenceDataService>();
            builder.RegisterType<OfferService>().As<IOfferService>();
            builder.RegisterType<CustomerService>().As<ICustomerService>();
            builder.RegisterType<AddressService>().As<IAddressService>();
            builder.RegisterType<ShoppingCartService>().As<IShoppingCartService>();
            builder.RegisterType<ImageResizeService>().As<IImageResizeService>();

            // EF DbContext — scoped per HTTP request via InstancePerLifetimeScope
            var connectionString = configuration.GetConnectionString("BookstoreDatabaseConnection")
                ?? configuration["ConnectionStrings/BookstoreDatabaseConnection"];

            builder.RegisterType<ApplicationDbContext>()
                   .WithParameter("connectionString", connectionString)
                   .InstancePerLifetimeScope();

            // Repositories
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

            // File service — S3 or local disk
            if (configuration["Services/FileService"] == "aws")
            {
                builder.RegisterType<AmazonS3Client>().As<IAmazonS3>();
                builder.RegisterType<S3FileService>().As<IFileService>();
            }
            else
            {
                var webRootPath = env?.WebRootPath
                    ?? Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
                    ?? Directory.GetCurrentDirectory();

                builder.RegisterInstance(new LocalFileService(webRootPath)).As<IFileService>();
            }

            // Image validation service — Rekognition or local stub
            if (configuration["Services/ImageValidationService"] == "aws")
            {
                builder.RegisterType<AmazonRekognitionClient>().As<IAmazonRekognition>();
                builder.RegisterType<RekognitionImageValidationService>().As<IImageValidationService>();
            }
            else
            {
                builder.RegisterType<LocalImageValidationService>().As<IImageValidationService>();
            }

            // Local authentication middleware (DI-resolved)
            if (configuration["Services/Authentication"] != "aws")
            {
                builder.RegisterType<LocalAuthenticationMiddleware>();
            }
        }
    }
}
