using MediaBrowser.Controller;
using MediaBrowser.Controller.Authentication;
using Jellyfin.Data.Events.Users;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Events.Authentication;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Keycloak
{
    /// <summary>
    /// Register Keycloak authentication provider with Jellyfin's DI container.
    /// </summary>
    public class PluginServiceRegistrator : IPluginServiceRegistrator
    {
        /// <inheritdoc />
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            serviceCollection.AddSingleton<IAuthenticationProvider, KeyCloakAuthenticationProviderPlugin>();
            serviceCollection.AddSingleton<LibraryAccessEnforcer>();
            serviceCollection.AddSingleton<IEventConsumer<UserUpdatedEventArgs>>(sp => sp.GetRequiredService<LibraryAccessEnforcer>());
            serviceCollection.AddSingleton<IEventConsumer<AuthenticationResultEventArgs>>(sp => sp.GetRequiredService<LibraryAccessEnforcer>());
            serviceCollection.AddSingleton<ILibraryPostScanTask>(sp => sp.GetRequiredService<LibraryAccessEnforcer>());
        }
    }
}
