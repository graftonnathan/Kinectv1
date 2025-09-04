using System;
using System.Threading;
using System.Threading.Tasks;

namespace Kinectv1.Mumble
{
    internal sealed class MumbleHostedService : IHostedService
    {
        public string ServiceName => "MumbleHostedService";
        public bool IsRunning => false;
        public event Action<string> OnStatus;
        public event Action<string> OnError;
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
