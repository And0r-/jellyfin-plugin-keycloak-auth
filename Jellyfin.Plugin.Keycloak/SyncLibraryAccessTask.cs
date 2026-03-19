using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Keycloak
{
    /// <summary>
    /// Scheduled task that syncs library access for all Keycloak users.
    /// Can be triggered manually from Dashboard → Scheduled Tasks or via the plugin config page.
    /// </summary>
    public class SyncLibraryAccessTask : IScheduledTask
    {
        private readonly LibraryAccessEnforcer _enforcer;
        private readonly ILogger<SyncLibraryAccessTask> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="SyncLibraryAccessTask"/> class.
        /// </summary>
        public SyncLibraryAccessTask(LibraryAccessEnforcer enforcer, ILogger<SyncLibraryAccessTask> logger)
        {
            _enforcer = enforcer;
            _logger = logger;
        }

        /// <inheritdoc />
        public string Name => "Sync Keycloak Library Access";

        /// <inheritdoc />
        public string Key => "KeycloakSyncLibraryAccess";

        /// <inheritdoc />
        public string Description => "Fetches roles from Keycloak for all users and re-applies library access permissions.";

        /// <inheritdoc />
        public string Category => "Keycloak";

        /// <inheritdoc />
        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting Keycloak library access sync task");
            var result = await _enforcer.SyncAllUsers(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Keycloak sync complete: {Synced} synced, {Errors} errors", result.SyncedUsers, result.Errors);
        }

        /// <inheritdoc />
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            // No automatic triggers - manual only
            return Array.Empty<TaskTriggerInfo>();
        }
    }
}
