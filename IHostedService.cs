using System;
using System.Threading;
using System.Threading.Tasks;

namespace Kinectv1
{
    /// <summary>
    /// Interface for long-running services that can be started and stopped asynchronously
    /// with proper cancellation token support.
    /// </summary>
    public interface IHostedService
    {
        /// <summary>
        /// Gets the name of the service for logging purposes.
        /// </summary>
        string ServiceName { get; }

        /// <summary>
        /// Gets whether the service is currently running.
        /// </summary>
        bool IsRunning { get; }

        /// <summary>
        /// Start the service asynchronously. Must be idempotent - calling multiple times 
        /// should not cause issues.
        /// </summary>
        /// <param name="cancellationToken">Token to monitor for cancellation requests</param>
        /// <returns>Task that completes when startup is finished</returns>
        Task StartAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Stop the service asynchronously. Must be idempotent - calling multiple times
        /// should not cause issues.
        /// </summary>
        /// <param name="cancellationToken">Token to monitor for cancellation requests during shutdown</param>
        /// <returns>Task that completes when shutdown is finished</returns>
        Task StopAsync(CancellationToken cancellationToken);
    }
}