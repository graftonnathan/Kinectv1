using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Kinectv1.Llm
{
    public interface ILlmClient
    {
        IAsyncEnumerable<string> ChatStreamAsync(string system, string user, CancellationToken ct = default, string[] images = null);
        Task<string> ChatOnceAsync(string system, string user, CancellationToken ct = default, string[] images = null);
    }
}
