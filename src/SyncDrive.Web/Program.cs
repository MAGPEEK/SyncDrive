using System.Globalization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using SyncDrive.Web;
using SyncDrive.Web.App;

// Weboberfläche (ADR-11): läuft im Browser, spricht nur mit der API der eigenen Seite. Das Sitzungs-Cookie hängt der Browser selbst an.
var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<Root>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped(sp => new HttpDriveApi(sp.GetRequiredService<HttpClient>()));
builder.Services.AddScoped<IDriveApi>(sp => sp.GetRequiredService<HttpDriveApi>());
builder.Services.AddScoped<IPublicLinkApi>(sp => sp.GetRequiredService<HttpDriveApi>());
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(WebTexts.For(CultureInfo.CurrentUICulture));
builder.Services.AddScoped<SessionViewModel>();
builder.Services.AddScoped<BrowserViewModel>();
builder.Services.AddScoped<LiveUpdatesViewModel>();
builder.Services.AddScoped<SearchViewModel>();
builder.Services.AddScoped<RecentViewModel>();
builder.Services.AddScoped<TrashViewModel>();
builder.Services.AddScoped<VersionsViewModel>();
builder.Services.AddScoped<PortalViewModel>();
builder.Services.AddScoped<MarksViewModel>();
builder.Services.AddScoped<NotificationsViewModel>();
builder.Services.AddScoped<AccountViewModel>();
builder.Services.AddScoped<ShareViewModel>();
builder.Services.AddScoped<SharedByMeViewModel>();
builder.Services.AddScoped<PublicLinkViewModel>();
builder.Services.AddScoped<AdminViewModel>();
builder.Services.AddScoped<TeamsViewModel>();
builder.Services.AddScoped<PoliciesViewModel>();

await builder.Build().RunAsync();
